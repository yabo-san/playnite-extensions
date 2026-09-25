using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    /// <summary>
    /// The cases here are the four real outcomes observed against live archive.org on
    /// 2026-09-25, not hypotheticals. Each one has a different remedy, which is the
    /// only reason the classifier exists.
    /// </summary>
    public class FeedHealthTests
    {
        private static FeedEntry Entry(string locator, PartRole role = PartRole.Game)
        {
            return new FeedEntry
            {
                Id = "test",
                Name = "Test Entry",
                Parts = { new FeedPart { Role = role, Source = SourceKind.InternetArchive, Locator = locator } },
            };
        }

        private static FeedHealthChecker Checker(ArchiveLookup result)
        {
            return new FeedHealthChecker(_ => result);
        }

        private static ArchiveLookup Live(params (string name, long size)[] files)
        {
            return new ArchiveLookup
            {
                Exists = true,
                IsDark = false,
                Files = files.Select(f => new FileCandidate { Name = f.name, SizeBytes = f.size }).ToList(),
            };
        }

        [Fact]
        public void Resolving_spec_is_Ok()
        {
            var d = Checker(Live(("Prey.zip", 1_700_000_000))).Check(Entry("prey-2006::Prey.zip"));

            Assert.True(d.IsHealthy);
            Assert.False(d.NeedsAttention);
            Assert.Equal("Prey.zip", d.Parts[0].ResolvedFile);
        }

        [Fact]
        public void Renamed_upstream_is_Moved_and_suggests_the_replacement()
        {
            // The real case: the mod was rebranded to DUSKLIGHT and republished, so the
            // item is untouched but the filename in the spec no longer exists.
            var lookup = Live(
                ("Dusklight (v1.4.1).zip", 977_300_000),
                ("Dusklight (v1.0).zip", 973_200_000),
                ("Twilight Princess DUSK.jpg", 400_000));

            var d = Checker(lookup).Check(
                Entry("twilight-princess-ddsk::Twilight Princess Decompilation (v1.0).zip"));

            var part = d.Parts[0];
            Assert.Equal(FeedPartHealth.Moved, part.Health);
            Assert.Equal("Dusklight (v1.4.1).zip", part.SuggestedFile);

            // Still fetchable, so this is the window in which re-archiving works.
            Assert.True(part.CanStillFetch);
            Assert.False(part.NeedsFallback);
            Assert.Single(d.ReArchivable);
        }

        [Fact]
        public void Dark_and_HELD_means_this_instance_is_now_the_origin()
        {
            // 2 of the 10 dark entries measured on 2026-09-25 are this case: both
            // Castlevanias, dark upstream, sitting on the NAS.
            var checker = new FeedHealthChecker(
                _ => new ArchiveLookup { Exists = true, IsDark = true },
                id => id == "castlevania-lords-of-shadow-ultimate-edition");

            var d = checker.Check(Entry("castlevania-lords-of-shadow-ultimate-edition"));
            var part = d.Parts[0];

            Assert.Equal(FeedPartHealth.Dark, part.Health);
            Assert.False(part.CanStillFetch);       // nothing to re-archive, it is too late
            Assert.True(part.IsLastCopy);
            Assert.False(part.IsOrphaned);
            Assert.Single(d.LastCopyHeldLocally);
            Assert.Empty(d.Orphaned);
            Assert.Contains("only source", part.Detail);
        }

        [Fact]
        public void Dark_and_NOT_held_is_a_dead_link_being_advertised()
        {
            // The other 8. A subscriber sees an installable game and gets nothing,
            // which is worse than the entry simply not existing.
            var checker = new FeedHealthChecker(
                _ => new ArchiveLookup { Exists = true, IsDark = true },
                _ => false);

            var d = checker.Check(Entry("wwe-2k15"));
            var part = d.Parts[0];

            Assert.Equal(FeedPartHealth.Dark, part.Health);
            Assert.True(part.IsOrphaned);
            Assert.False(part.IsLastCopy);
            Assert.Single(d.Orphaned);
            Assert.Empty(d.LastCopyHeldLocally);
            Assert.Contains("withdraw it", part.Detail);
        }

        [Fact]
        public void Without_a_local_probe_a_dark_part_is_assumed_orphaned()
        {
            // The safe default: never claim a fallback that may not exist.
            var d = Checker(new ArchiveLookup { Exists = true, IsDark = true })
                .Check(Entry("perfect-dark-recompiled"));

            Assert.True(d.Parts[0].IsOrphaned);
            Assert.False(d.Parts[0].IsLastCopy);
            Assert.Empty(d.ReArchivable);
        }

        [Fact]
        public void Unknown_identifier_is_Missing_not_Dark()
        {
            // A typo must never be reported as a takedown; it would invent an
            // unrecoverable loss that never happened.
            var d = Checker(new ArchiveLookup { Exists = false, IsDark = false })
                .Check(Entry("this-identifier-does-not-exist-zzz999"));

            Assert.Equal(FeedPartHealth.Missing, d.Parts[0].Health);
            Assert.False(d.Parts[0].NeedsFallback);
        }

        [Fact]
        public void Live_item_with_no_archives_is_NoMatchingFile()
        {
            // Two curated items behave this way: "fav-rohankar" and "RAZE_CM".
            var d = Checker(Live(("cover.jpg", 50_000), ("meta.xml", 900)))
                .Check(Entry("fav-rohankar"));

            Assert.Equal(FeedPartHealth.NoMatchingFile, d.Parts[0].Health);
        }

        [Fact]
        public void Malformed_spec_is_reported_not_thrown()
        {
            var d = Checker(Live(("a.zip", 1))).Check(Entry("broken::"));

            Assert.Equal(FeedPartHealth.Missing, d.Parts[0].Health);
            Assert.Contains("names no file", d.Parts[0].Detail);
        }

        [Fact]
        public void A_lookup_failure_is_Unknown_and_claims_nothing_about_the_item()
        {
            var checker = new FeedHealthChecker(_ => throw new TimeoutException("network down"));

            var d = checker.Check(Entry("anything"));

            Assert.Equal(FeedPartHealth.Unknown, d.Parts[0].Health);
            // Critically NOT Dark: a flaky network must not look like a takedown.
            Assert.False(d.Parts[0].NeedsFallback);
        }

        [Fact]
        public void Non_archive_sources_are_left_to_their_own_checker()
        {
            var entry = new FeedEntry
            {
                Id = "gh", Name = "GH",
                Parts = { new FeedPart { Role = PartRole.Port, Source = SourceKind.GitHub, Locator = "a/b" } },
            };

            var d = Checker(Live()).Check(entry);

            Assert.Equal(FeedPartHealth.Unknown, d.Parts[0].Health);
            Assert.Contains("not checked here", d.Parts[0].Detail);
        }

        [Fact]
        public void Composite_entry_reports_each_part_separately()
        {
            var entry = new FeedEntry
            {
                Id = "recomp", Name = "Recomp",
                Parts =
                {
                    new FeedPart { Role = PartRole.Data, Source = SourceKind.InternetArchive, Locator = "gone-item" },
                    new FeedPart { Role = PartRole.Port, Source = SourceKind.GitHub, Locator = "a/b" },
                },
            };

            var d = new FeedHealthChecker(_ => new ArchiveLookup { Exists = true, IsDark = true }).Check(entry);

            Assert.Equal(2, d.Parts.Count);
            Assert.Equal(FeedPartHealth.Dark, d.Parts[0].Health);
            Assert.Equal(FeedPartHealth.Unknown, d.Parts[1].Health);
            Assert.True(d.NeedsAttention);
            Assert.Single(d.Summary(), s => s.Contains("Dark"));
        }
    }
}
