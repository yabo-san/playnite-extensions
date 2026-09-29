using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yabo.Shared;

namespace YaboLauncherLibrary.Tests
{
    public class GameIdResolverTests
    {
        private static LauncherGame Port(string repo, string name, string folderName = null) =>
            new LauncherGame { Id = "quiver:" + repo, Name = name, Source = "quiver:https://c", FolderName = folderName };

        private static LauncherGame Item(string id, string name) =>
            new LauncherGame { Id = id, Name = name, Source = "archive.org:u" };

        [Fact]
        public void New_records_keep_their_own_id()
        {
            var ids = GameIdResolver.Resolve(new[] { Item("a", "A") }, new KnownGame[0], null);
            Assert.Equal("a", ids["a"]);
        }

        [Fact]
        public void Legacy_YaboLibrary_records_are_found_by_folderName_then_repository_link_then_name()
        {
            var known = new[]
            {
                new KnownGame { GameId = "HarbourMasters.Shipwright", Name = "SoH (curated)" },
                new KnownGame { GameId = "sm64ex", Name = "Mario", LinkUrls = { "https://GitHub.com/sm64pc/sm64ex.git/" } },
                new KnownGame { GameId = "zelda3", Name = "Zelda 3!" },
            };
            var ids = GameIdResolver.Resolve(new[]
            {
                Port("harbourmasters/shipwright", "Ship of Harkinian", "HarbourMasters.Shipwright"),
                Port("sm64pc/sm64ex", "Super Mario 64"),
                Port("snesrev/zelda3", "zelda 3"),
            }, known, null);

            Assert.Equal("HarbourMasters.Shipwright", ids["quiver:harbourmasters/shipwright"]);
            Assert.Equal("sm64ex", ids["quiver:sm64pc/sm64ex"]);
            Assert.Equal("zelda3", ids["quiver:snesrev/zelda3"]);
        }

        [Fact]
        public void A_match_made_once_sticks_even_after_the_name_changes()
        {
            var previous = new Dictionary<string, string> { ["quiver:x/y"] = "legacy-y" };
            var ids = GameIdResolver.Resolve(new[] { Port("x/y", "Renamed") }, new[] { new KnownGame { GameId = "legacy-y", Name = "Old" } }, previous);
            Assert.Equal("legacy-y", ids["quiver:x/y"]);
        }

        [Fact]
        public void An_exact_id_beats_a_fuzzy_claim_and_no_game_is_claimed_twice()
        {
            var known = new[] { new KnownGame { GameId = "halo", Name = "Halo" } };
            var ids = GameIdResolver.Resolve(new[] { Item("other-halo-upload", "Halo"), Item("halo", "Halo") }, known, null);
            Assert.Equal("halo", ids["halo"]);
            Assert.Equal("other-halo-upload", ids["other-halo-upload"]);
        }

        [Fact]
        public void Two_known_games_with_the_same_name_are_not_guessed_between()
        {
            var known = new[] { new KnownGame { GameId = "a1", Name = "Doom" }, new KnownGame { GameId = "a2", Name = "DOOM" } };
            var ids = GameIdResolver.Resolve(new[] { Item("doom-ia", "Doom") }, known, null);
            Assert.Equal("doom-ia", ids["doom-ia"]);
        }

        [Fact]
        public void An_id_taken_by_a_legacy_match_gets_a_distinct_GameId()
        {
            var known = new[] { new KnownGame { GameId = "foo", Name = "Foo" } };
            var ids = GameIdResolver.Resolve(new[] { Port("o/foo", "Foo", "foo"), Item("foo2", "Bar") }, known, null);
            Assert.Equal("foo", ids["quiver:o/foo"]);
            var clash = GameIdResolver.Resolve(new[] { Port("o/foo", "Foo", "foo"), Item("foo", "Other") },
                new[] { new KnownGame { GameId = "legacy", Name = "x" } }, new Dictionary<string, string> { ["quiver:o/foo"] = "foo" });
            Assert.Equal("foo", clash["quiver:o/foo"]);
            Assert.Equal("foo#archive.org:u", clash["foo"]);
            Assert.Equal(2, clash.Values.Distinct().Count());
        }

        [Fact]
        public void Games_the_export_stopped_listing_are_only_marked_not_installed()
        {
            var known = new[]
            {
                new KnownGame { GameId = "kept", IsInstalled = true },
                new KnownGame { GameId = "gone", IsInstalled = true },
                new KnownGame { GameId = "gone-already-uninstalled", IsInstalled = false },
            };
            Assert.Equal(new[] { "gone" }, GameIdResolver.NoLongerExported(known, new[] { "kept" }));
        }

        [Theory]
        [InlineData("Ship of Harkinian!", "shipofharkinian")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NameKey(string name, string key) => Assert.Equal(key, GameIdResolver.NameKey(name));
    }
}
