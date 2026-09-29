using System;
using System.Linq;
using Playnite.SDK.Models;
using Xunit;
using Yabo.Shared;
using RohanKarPlaynite;

namespace RohanKar.Tests
{
    public class GameMapperTests
    {
        private static readonly Func<string, bool> AllExist = _ => true;
        private static readonly Func<string, bool> NoneExist = _ => false;

        private static LauncherGame Record(int i) => LauncherExportParserTests.Fixture().Games[i];

        [Fact]
        public void An_installed_game_gets_a_Play_action_running_exe_with_args_in_workingDir()
        {
            var meta = GameMapper.ToMetadata(Record(0), "rk-e2e-halo-ce", null, AllExist);

            Assert.Equal("rk-e2e-halo-ce", meta.GameId);
            Assert.Equal("Halo: Combat Evolved", meta.Name);
            Assert.Equal("RohanKar", ((MetadataNameProperty)meta.Source).Name);
            Assert.True(meta.IsInstalled);
            Assert.Equal(@"C:\Games\rk-e2e-halo-ce", meta.InstallDirectory);
            var play = Assert.Single(meta.GameActions);
            Assert.Equal(GameActionType.File, play.Type);
            Assert.True(play.IsPlayAction);
            Assert.Equal("Play", play.Name);
            Assert.Equal(@"C:\Games\rk-e2e-halo-ce\Halo\halo.exe", play.Path);
            Assert.Equal("-windowed", play.Arguments);
            Assert.Equal(@"C:\Games\rk-e2e-halo-ce\Halo", play.WorkingDir);
        }

        [Fact]
        public void Playtime_last_played_tags_and_the_update_tag()
        {
            var meta = GameMapper.ToMetadata(Record(0), "x", null, AllExist);
            Assert.Equal(5400UL, meta.Playtime);
            Assert.Equal(new DateTime(2026, 9, 28, 21, 4, 11, DateTimeKind.Utc), meta.LastActivity?.ToUniversalTime());
            Assert.Equal(new[] { "Shooters", "Update available" },
                meta.Tags.Cast<MetadataNameProperty>().Select(t => t.Name).OrderBy(n => n));
        }

        [Fact]
        public void Cover_and_background_come_from_the_files_on_disk()
        {
            var meta = GameMapper.ToMetadata(Record(0), "x", null, AllExist);
            Assert.EndsWith("rk-e2e-halo-ce.jpg", meta.CoverImage.Path);
            Assert.EndsWith("hero.png", meta.BackgroundImage.Path);

            var missing = GameMapper.ToMetadata(Record(0), "x", null, NoneExist);
            Assert.Null(missing.CoverImage);
            Assert.Null(missing.BackgroundImage);
        }

        [Fact]
        public void A_cover_or_background_already_set_in_Playnite_is_never_replaced()
        {
            var coverSet = GameMapper.ToMetadata(Record(0), "x", new KnownGame { GameId = "x", HasCover = true }, AllExist);
            Assert.Null(coverSet.CoverImage);
            Assert.NotNull(coverSet.BackgroundImage);

            var both = GameMapper.ToMetadata(Record(0), "x", new KnownGame { GameId = "x", HasCover = true, HasBackground = true }, AllExist);
            Assert.Null(both.CoverImage);
            Assert.Null(both.BackgroundImage);
        }

        [Fact]
        public void A_game_that_is_not_installed_has_no_actions_and_no_install_folder()
        {
            var meta = GameMapper.ToMetadata(Record(1), "quiver:harbourmasters/shipwright", null, AllExist);
            Assert.Equal("quiver:harbourmasters/shipwright", meta.GameId);
            Assert.False(meta.IsInstalled);
            Assert.Null(meta.InstallDirectory);
            Assert.Empty(meta.GameActions);
            Assert.Null(meta.Tags);
            Assert.Null(meta.CoverImage);
            Assert.Null(meta.LastActivity);
            Assert.Equal("Nintendo", ((MetadataNameProperty)meta.Platforms.Single()).Name);
        }

        [Fact]
        public void Installed_without_an_exe_has_no_Play_action_and_a_missing_workingDir_is_the_exe_folder()
        {
            var noExe = new LauncherGame { Id = "a", Name = "A", Installed = true, InstallDir = @"C:\a" };
            Assert.Empty(GameMapper.ToMetadata(noExe, "a", null, AllExist).GameActions);

            var manual = GameMapper.ToMetadata(Record(2), "m", null, AllExist);
            var play = Assert.Single(manual.GameActions);
            Assert.Null(play.Arguments);
            Assert.Equal(System.IO.Path.GetDirectoryName(@"D:\Apps\Tool\tool.exe"), play.WorkingDir);
            Assert.Equal("2.1", manual.Version);
        }

        [Theory]
        [InlineData("PC", null, "pc_windows")]
        [InlineData(null, null, "pc_windows")]
        [InlineData("Nintendo", "Nintendo", null)]
        [InlineData("PlayStation", "PlayStation", null)]
        [InlineData("Xbox", "Xbox", null)]
        [InlineData("Other", "Other", null)]
        [InlineData("Sega", "Other", null)]
        public void Platforms(string platform, string name, string spec)
        {
            var p = GameMapper.Platform(platform);
            if (spec != null) Assert.Equal(spec, ((MetadataSpecProperty)p).Id);
            else Assert.Equal(name, ((MetadataNameProperty)p).Name);
        }
    }
}
