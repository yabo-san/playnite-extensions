using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    /// <summary>
    /// The `parts` array is the first schema extension to apps.json. It exists because
    /// the flat shape carries one `repository` and a Doom collision needs two GitHub
    /// upstreams: the launcher and the engine.
    /// </summary>
    public class CatalogPartsTests
    {
        private const string DoomRow = @"{
          'name': 'Doom',
          'folderName': 'doom.collision',
          'executableName': 'DoomLauncher',
          'parts': [
            { 'role':'launcher', 'source':'github', 'locator':'nstlaurent/DoomLauncher' },
            { 'role':'engine',   'source':'github', 'locator':'bradharding/doomretro' },
            { 'role':'data', 'source':'internet-archive', 'locator':'doom_complete::Doom.zip', 'targetSubpath':'iwads' },
            { 'role':'data', 'source':'internet-archive', 'locator':'doom-wads', 'targetSubpath':'wads', 'optional':true },
            { 'role':'config', 'ingest':'generated', 'targetSubpath':'DoomLauncher/settings.ini', 'content':'IWadDirectory=iwads' }
          ]
        }";

        [Fact]
        public void A_parts_array_produces_a_valid_multi_upstream_collision()
        {
            var e = CatalogImport.FromCatalogRow(JObject.Parse(DoomRow.Replace('\'', '"')));

            Assert.Equal("doom.collision", e.Id);
            Assert.Equal(5, e.Parts.Count);
            Assert.True(e.IsComposite);
            Assert.Empty(e.Validate());
        }

        [Fact]
        public void Two_github_upstreams_survive_the_read()
        {
            // The whole reason the extension exists: one `repository` cannot hold both.
            var e = CatalogImport.FromCatalogRow(JObject.Parse(DoomRow.Replace('\'', '"')));
            var gh = e.Parts.Where(p => p.Source == SourceKind.GitHub).ToList();

            Assert.Equal(2, gh.Count);
            Assert.Equal(PartRole.Launcher, gh[0].Role);
            Assert.Equal(PartRole.Port, gh[1].Role);   // "engine" is an alias for port
        }

        [Fact]
        public void Play_opens_the_launcher_and_staging_paths_are_read()
        {
            var e = CatalogImport.FromCatalogRow(JObject.Parse(DoomRow.Replace('\'', '"')));

            Assert.Equal("nstlaurent/DoomLauncher", e.Primary.Locator);
            Assert.Equal("DoomLauncher", e.ExeHint);
            // The Config part carries no Locator, so the predicate must tolerate null.
            Assert.Equal("iwads", e.Parts.Single(p => p.Locator?.StartsWith("doom_complete") == true).StageTo);
            Assert.True(e.Parts.Single(p => p.Locator == "doom-wads").Optional);
        }

        [Fact]
        public void A_parts_array_replaces_the_flat_fields_rather_than_merging()
        {
            // Otherwise a migrated entry would emit its old flat part twice.
            var row = JObject.Parse(@"{
              ""name"": ""Migrated"", ""folderName"": ""m"",
              ""repository"": ""old/flat"", ""iaIdentifier"": ""old-item"",
              ""parts"": [ { ""role"":""game"", ""source"":""internet-archive"", ""locator"":""new-item"" } ]
            }");

            var e = CatalogImport.FromCatalogRow(row);

            Assert.Single(e.Parts);
            Assert.Equal("new-item", e.Parts[0].Locator);
        }

        [Theory]
        [InlineData("launcher", PartRole.Launcher)]
        [InlineData("engine", PartRole.Port)]
        [InlineData("port", PartRole.Port)]
        [InlineData("data", PartRole.Data)]
        [InlineData("patch", PartRole.Patch)]
        [InlineData("config", PartRole.Config)]
        [InlineData("", PartRole.Game)]
        public void Roles_use_the_catalogue_spelling(string raw, PartRole expected)
        {
            Assert.Equal(expected, CatalogImport.ParseRole(raw));
        }

        [Theory]
        [InlineData("github", SourceKind.GitHub)]
        [InlineData("internet-archive", SourceKind.InternetArchive)]
        [InlineData("ia", SourceKind.InternetArchive)]
        [InlineData("itch", SourceKind.Itch)]
        [InlineData("nonsense", SourceKind.Unknown)]
        public void Sources_use_the_catalogue_spelling(string raw, SourceKind expected)
        {
            Assert.Equal(expected, CatalogImport.ParseSource(raw));
        }
    }
}
