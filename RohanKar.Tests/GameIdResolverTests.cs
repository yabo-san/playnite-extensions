using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yabo.Shared;
using RohanKarPlaynite;

namespace RohanKar.Tests
{
    public class GameIdResolverTests
    {
        private static LauncherGame Item(string id, string name, string installDir = null) =>
            new LauncherGame { Id = id, Name = name, Source = "archive.org:u", Installed = installDir != null, InstallDir = installDir };

        [Fact]
        public void The_GameId_is_the_launchers_library_id()
        {
            var known = new[] { new KnownGame { GameId = "rk-e2e-halo-ce", Name = "Halo (renamed in Playnite)" } };
            var ids = GameIdResolver.Resolve(new[] { Item("rk-e2e-halo-ce", "Halo: Combat Evolved"), Item("new-one", "New") }, known, null);
            Assert.Equal("rk-e2e-halo-ce", ids["rk-e2e-halo-ce"]);
            Assert.Equal("new-one", ids["new-one"]);
        }

        [Fact]
        public void A_record_from_the_folder_scanning_plugin_is_found_by_its_install_folder()
        {
            // "(12)Blur" was imported with GameId 12; the launcher adopted the folder as blur-repack
            var known = new[] { new KnownGame { GameId = "12", Name = "Blur", InstallDirectory = @"D:\Games\(12)Blur\" } };
            var ids = GameIdResolver.Resolve(new[] { Item("blur-repack", "Blur (2010)", "d:/games/(12)Blur") }, known, null);
            Assert.Equal("12", ids["blur-repack"]);
        }

        [Fact]
        public void Then_by_a_name_only_one_known_game_has()
        {
            var known = new[]
            {
                new KnownGame { GameId = "7", Name = "Lost Planet 2" },
                new KnownGame { GameId = "a1", Name = "Doom" },
                new KnownGame { GameId = "a2", Name = "DOOM" },
            };
            var ids = GameIdResolver.Resolve(new[] { Item("lp2", "Lost Planet 2!"), Item("doom-ia", "Doom") }, known, null);
            Assert.Equal("7", ids["lp2"]);
            Assert.Equal("doom-ia", ids["doom-ia"]);   // two Dooms: no guess
        }

        [Fact]
        public void A_match_made_once_sticks_even_after_the_name_changes()
        {
            var previous = new Dictionary<string, string> { ["blur-repack"] = "12" };
            var ids = GameIdResolver.Resolve(new[] { Item("blur-repack", "Renamed") }, new[] { new KnownGame { GameId = "12", Name = "Old" } }, previous);
            Assert.Equal("12", ids["blur-repack"]);
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
        public void Two_known_games_in_one_folder_are_not_guessed_between()
        {
            var known = new[]
            {
                new KnownGame { GameId = "1", Name = "A", InstallDirectory = @"C:\g" },
                new KnownGame { GameId = "2", Name = "B", InstallDirectory = @"C:\G" },
            };
            Assert.Equal("x", GameIdResolver.Resolve(new[] { Item("x", "X", @"C:\g") }, known, null)["x"]);
        }

        [Fact]
        public void An_id_taken_by_a_match_gets_a_distinct_GameId()
        {
            var clash = GameIdResolver.Resolve(new[] { Item("blur-repack", "Blur"), Item("12", "Other") },
                new[] { new KnownGame { GameId = "legacy", Name = "x" } }, new Dictionary<string, string> { ["blur-repack"] = "12" });
            Assert.Equal("12", clash["blur-repack"]);
            Assert.Equal("12#archive.org:u", clash["12"]);
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
        [InlineData(@"C:\Games\X\", @"c:\games\x")]
        [InlineData("c:/games/x", @"c:\games\x")]
        [InlineData(" ", null)]
        [InlineData(null, null)]
        public void FolderKey(string path, string key) => Assert.Equal(key, GameIdResolver.FolderKey(path));

        [Theory]
        [InlineData("Ship of Harkinian!", "shipofharkinian")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NameKey(string name, string key) => Assert.Equal(key, GameIdResolver.NameKey(name));
    }
}
