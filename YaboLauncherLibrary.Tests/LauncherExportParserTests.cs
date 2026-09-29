using System;
using System.IO;
using System.Linq;
using Xunit;
using Yabo.Shared;

namespace YaboLauncherLibrary.Tests
{
    public class LauncherExportParserTests
    {
        internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "playnite-export.json");

        internal static LauncherExport Fixture() => LauncherExportParser.Read(FixturePath);

        [Fact]
        public void Reads_the_fixture_header_and_every_field_of_a_record()
        {
            var export = Fixture();

            Assert.Equal(1, export.SchemaVersion);
            Assert.Equal("1.6.0-fork.3", export.LauncherVersion);
            Assert.Equal(new DateTime(2026, 9, 29, 2, 30, 0, DateTimeKind.Utc), export.GeneratedAt?.ToUniversalTime());

            var halo = export.Games[0];
            Assert.Equal("rk-e2e-halo-ce", halo.Id);
            Assert.Equal("Halo: Combat Evolved", halo.Name);
            Assert.Equal("archive.org", halo.SourceKind);
            Assert.Equal("PC", halo.Platform);
            Assert.True(halo.Installed);
            Assert.Equal(@"C:\Games\rk-e2e-halo-ce", halo.InstallDir);
            Assert.Equal(@"C:\Games\rk-e2e-halo-ce\Halo\halo.exe", halo.Exe);
            Assert.Equal("-windowed", halo.Args);
            Assert.Equal(@"C:\Games\rk-e2e-halo-ce\Halo", halo.WorkingDir);
            Assert.True(halo.UpdateAvailable);
            Assert.EndsWith("rk-e2e-halo-ce.jpg", halo.CoverPath);
            Assert.EndsWith("hero.png", halo.HeroPath);
            Assert.Equal(new[] { "Shooters" }, halo.Tags);   // blank tags dropped
            Assert.Equal(new DateTime(2026, 9, 28, 21, 4, 11, DateTimeKind.Utc), halo.LastPlayed?.ToUniversalTime());
            Assert.Equal(5400UL, halo.PlaytimeSeconds);
            Assert.True(halo.Favorite);
            Assert.True(halo.Playable);
            Assert.Null(halo.Repository);
        }

        [Fact]
        public void Ports_carry_their_repository_and_folderName()
        {
            var port = Fixture().Games[1];
            Assert.Equal("quiver", port.SourceKind);
            Assert.Equal("harbourmasters/shipwright", port.Repository);
            Assert.Equal("HarbourMasters.Shipwright", port.FolderName);
            Assert.False(port.Playable);
        }

        [Fact]
        public void Manual_entries_fall_back_to_the_id_for_a_name_and_ignore_unknown_fields()
        {
            var manual = Fixture().Games[2];
            Assert.Equal("manual", manual.SourceKind);
            Assert.Equal(manual.Id, manual.Name);
            Assert.Empty(manual.Tags);
            Assert.Equal("2.1", manual.Version);
        }

        [Fact]
        public void Drops_records_without_an_id_and_repeated_ids()
        {
            var ids = Fixture().Games.Select(g => g.Id).ToList();
            Assert.Equal(new[] { "rk-e2e-halo-ce", "quiver:harbourmasters/shipwright", "6f1c2a1e-0b7d-4c55-9f0e-3d2a8b1c4e77" }, ids);
            Assert.Equal("Halo: Combat Evolved", Fixture().Games[0].Name);   // the first record wins
        }

        [Fact]
        public void A_newer_schema_is_refused_with_a_message_saying_to_update()
        {
            var e = Assert.Throws<NotSupportedException>(() => LauncherExportParser.Parse("{\"schemaVersion\":2,\"games\":[]}"));
            Assert.Contains("Update the y4bo Launcher plugin", e.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("null")]
        [InlineData("{\"games\":[]}")]
        [InlineData("{ not json")]
        public void Broken_files_are_format_errors(string json)
        {
            Assert.Throws<FormatException>(() => LauncherExportParser.Parse(json));
        }

        [Fact]
        public void Null_games_and_quiver_ids_without_a_repository()
        {
            var export = LauncherExportParser.Parse("{\"schemaVersion\":1,\"games\":null}");
            Assert.Empty(export.Games);
            var g = LauncherExportParser.Parse("{\"schemaVersion\":1,\"games\":[{\"id\":\"quiver:name:Thing\",\"source\":null,\"tags\":null}]}").Games.Single();
            Assert.Null(g.Repository);
            Assert.Null(g.SourceKind);
            Assert.Empty(g.Tags);
            Assert.Equal("quiver:name:Thing", g.Name);
        }

        [Fact]
        public void A_missing_file_is_not_retried()
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            Assert.Throws<FileNotFoundException>(() => LauncherExportParser.Read(missing, attempts: 5, delayMs: 5000));
        }
    }
}
