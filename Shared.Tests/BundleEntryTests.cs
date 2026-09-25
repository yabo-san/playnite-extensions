using System.Linq;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    /// <summary>
    /// The owner's answer to "an engine is not publishable": do not publish the engine,
    /// publish a bundle that is the launcher plus an engine plus the game data.
    ///
    /// Every identifier and repo below is real. The data halves are on a curated
    /// uploader whose whole catalogue is game data with no executables, which is why it
    /// pairs with these and with nothing else.
    /// </summary>
    public class BundleEntryTests
    {
        private static FeedEntry DoomBundle()
        {
            return new FeedEntry
            {
                Id = "doom-bundle",
                Name = "Doom (DoomLauncher + Doom Retro)",
                Install = InstallKind.Portable,
                ExeHint = "DoomLauncher.exe",
                Notes = "Opens DoomLauncher with the IWADs registered and the WAD library available.",
                Parts =
                {
                    new FeedPart
                    {
                        Role = PartRole.Launcher, Source = SourceKind.GitHub,
                        Locator = "nstlaurent/DoomLauncher",
                    },
                    new FeedPart
                    {
                        Role = PartRole.Port, Source = SourceKind.GitHub,
                        Locator = "bradharding/doomretro",
                    },
                    new FeedPart
                    {
                        // 1.35 GB, the IWADs
                        Role = PartRole.Data, Source = SourceKind.InternetArchive,
                        Locator = "doom_complete::Doom.zip", StageTo = "iwads",
                    },
                    new FeedPart
                    {
                        // 2.38 GB across 138 files: Brutal Doom, SIGIL, DHTP, Maximum Doom 2.
                        // Optional because the bundle is playable without it.
                        Role = PartRole.Data, Source = SourceKind.InternetArchive,
                        Locator = "doom-wads", StageTo = "wads", Optional = true,
                    },
                    new FeedPart
                    {
                        // The publisher's only original contribution: the file that makes
                        // the launcher find the engines and the IWADs. No upstream, so it
                        // travels inline.
                        Role = PartRole.Config, Acquire = AcquisitionKind.Generated,
                        StageTo = "DoomLauncher/settings.ini",
                        // Deliberately a placeholder. DoomLauncher's real configuration
                        // format is not verified yet, and inventing a plausible-looking
                        // one would produce a bundle that installs and silently does not
                        // work. The test covers the MECHANISM, not the content.
                        Content = "; PLACEHOLDER: DoomLauncher config format unverified\n"
                                + "IWadDirectory=iwads\nWadDirectory=wads\n",
                    },
                },
            };
        }

        private static FeedEntry QuakeBundle()
        {
            return new FeedEntry
            {
                Id = "quake-bundle",
                Name = "Quake (QuakeInjector + ironwail)",
                Install = InstallKind.Portable,
                ExeHint = "QuakeInjector",
                Parts =
                {
                    new FeedPart
                    {
                        Role = PartRole.Launcher, Source = SourceKind.GitHub,
                        Locator = "hrehfeld/QuakeInjector",
                    },
                    new FeedPart
                    {
                        Role = PartRole.Port, Source = SourceKind.GitHub,
                        Locator = "andrei-drexler/ironwail",
                    },
                    new FeedPart
                    {
                        // 1.05 GB, the PAKs
                        Role = PartRole.Data, Source = SourceKind.InternetArchive,
                        Locator = "quake-complete::Quake.zip", StageTo = "id1",
                    },
                    new FeedPart
                    {
                        Role = PartRole.Config, Acquire = AcquisitionKind.Generated,
                        StageTo = "quakeinjector.cfg",
                        // Same: placeholder until QuakeInjector's real format is checked.
                        Content = "# PLACEHOLDER: QuakeInjector config format unverified\n"
                                + "quakeDirectory=.\nenginePath=ironwail.exe\n",
                    },
                },
            };
        }

        [Fact]
        public void A_launcher_bundle_validates()
        {
            Assert.Empty(DoomBundle().Validate());
            Assert.Empty(QuakeBundle().Validate());
        }

        [Fact]
        public void The_launcher_is_what_the_user_opens_not_the_engine()
        {
            // Primary drives the Play action. Pointing it at doomretro.exe would skip
            // the front end that knows which WAD to load.
            var doom = DoomBundle();

            Assert.Equal(PartRole.Launcher, doom.Primary.Role);
            Assert.Equal("nstlaurent/DoomLauncher", doom.Primary.Locator);
        }

        [Fact]
        public void A_bundle_is_composite_and_stages_data_into_subpaths()
        {
            var doom = DoomBundle();

            Assert.True(doom.IsComposite);
            Assert.Equal(5, doom.Parts.Count);
            // A Config part has no Locator at all, so the predicate must tolerate null.
            Assert.Equal("iwads", doom.Parts.Single(p => p.Locator?.StartsWith("doom_complete") == true).StageTo);
            Assert.Equal("id1", QuakeBundle().Parts.Single(p => p.Role == PartRole.Data).StageTo);
        }

        [Fact]
        public void The_mod_library_is_optional_so_the_bundle_works_without_2_GB_of_wads()
        {
            var wads = DoomBundle().Parts.Single(p => p.Locator == "doom-wads");

            Assert.True(wads.Optional);
            Assert.Equal(PartRole.Data, wads.Role);
        }

        [Fact]
        public void A_launcher_with_no_engine_is_rejected()
        {
            // The one always-wrong combination: the front end opens on an empty library.
            var broken = new FeedEntry
            {
                Id = "launcher-only", Name = "DoomLauncher alone",
                Parts =
                {
                    new FeedPart { Role = PartRole.Launcher, Source = SourceKind.GitHub, Locator = "nstlaurent/DoomLauncher" },
                    new FeedPart { Role = PartRole.Data, Source = SourceKind.InternetArchive, Locator = "doom_complete::Doom.zip" },
                },
            };

            Assert.Contains("no engine for it to run", string.Join(" ", broken.Validate()));
        }

        [Fact]
        public void An_engine_on_its_own_is_still_rejected()
        {
            // The original finding, unchanged: publishing GZDoom alone gives someone a
            // binary and no game. This is why all 12 Doom catalogue entries are not
            // feed material on their own.
            var engineOnly = new FeedEntry
            {
                Id = "gzdoom", Name = "GZDoom",
                Parts = { new FeedPart { Role = PartRole.Port, Source = SourceKind.GitHub, Locator = "ZDoom/gzdoom" } },
            };

            Assert.Contains("no data part", string.Join(" ", engineOnly.Validate()));
        }
    }
}
