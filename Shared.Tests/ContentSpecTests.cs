using System;
using System.Collections.Generic;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    /// <summary>
    /// The specs used here are real entries from `dev/ia-matches.json`, not invented
    /// ones, so a change that breaks the owner's existing curation fails the build.
    /// </summary>
    public class ContentSpecTests
    {
        [Theory]
        [InlineData("pd-i686-windows::pd-i686-windows.zip", "pd-i686-windows", "pd-i686-windows.zip")]
        [InlineData("banjo-kazooie-recompiled.-7z::Banjo Kazooie Recompiled.7z",
                    "banjo-kazooie-recompiled.-7z", "Banjo Kazooie Recompiled.7z")]
        [InlineData("bomberman-64_recompiled::Bomberman 64 Recompiled/Bomberman 64.zip",
                    "bomberman-64_recompiled", "Bomberman 64 Recompiled/Bomberman 64.zip")]
        public void Parses_explicit_specs(string raw, string identifier, string file)
        {
            var spec = ContentSpec.Parse(raw);

            Assert.Equal(identifier, spec.Identifier);
            Assert.Equal(file, spec.FilePath);
            Assert.True(spec.IsExplicit);
            Assert.Equal(raw, spec.ToString());
        }

        [Fact]
        public void Parses_bare_identifier_as_auto_select()
        {
            var spec = ContentSpec.Parse("sega-rally-2-25th-anniversary");

            Assert.Null(spec.FilePath);
            Assert.False(spec.IsExplicit);
            Assert.Equal("sega-rally-2-25th-anniversary", spec.ToString());
        }

        [Fact]
        public void Auto_select_takes_the_largest_ARCHIVE_not_the_largest_file()
        {
            // An item's biggest file is often not its archive. Picking by size alone
            // would download a disc image or a video and call it the game.
            var files = new List<FileCandidate>
            {
                new FileCandidate { Name = "trailer.mp4", SizeBytes = 8_000_000_000 },
                new FileCandidate { Name = "Sega Rally 2 ~ 25th Anniversary Edition 2.1.0.7z", SizeBytes = 912_000_000 },
                new FileCandidate { Name = "patch.zip", SizeBytes = 5_000_000 },
                new FileCandidate { Name = "__ia_thumb.jpg", SizeBytes = 12_000 },
            };

            Assert.Equal("Sega Rally 2 ~ 25th Anniversary Edition 2.1.0.7z",
                         ContentSpec.Parse("sega-rally-2-25th-anniversary").Resolve(files));
        }

        [Fact]
        public void Explicit_spec_matches_a_nested_path()
        {
            var spec = ContentSpec.Parse("proper-sonic-origins::proper-sonic-origins/Sonic3AIR.zip");
            var files = new List<FileCandidate>
            {
                new FileCandidate { Name = "proper-sonic-origins/Sonic3AIR.zip", SizeBytes = 40_000_000 },
                new FileCandidate { Name = "proper-sonic-origins/SonicForever.zip", SizeBytes = 41_000_000 },
            };

            Assert.Equal("proper-sonic-origins/Sonic3AIR.zip", spec.Resolve(files));
        }

        [Fact]
        public void Explicit_spec_tolerates_a_windows_separator()
        {
            // Match paths get transcribed by hand, and a backslash is an easy slip.
            var spec = ContentSpec.Parse(@"item::folder\game.zip");
            var files = new List<FileCandidate> { new FileCandidate { Name = "folder/game.zip" } };

            Assert.Equal("folder/game.zip", spec.Resolve(files));
        }

        [Fact]
        public void Returns_null_when_nothing_matches_rather_than_throwing()
        {
            // Two curated items ("fav-rohankar", "RAZE_CM") contain no archive at all.
            // That is a state to show in the UI, not an exception to handle.
            var spec = ContentSpec.Parse("some-item");

            Assert.Null(spec.Resolve(new List<FileCandidate> { new FileCandidate { Name = "cover.jpg" } }));
            Assert.Null(spec.Resolve(new List<FileCandidate>()));
            Assert.Null(spec.Resolve(null));
        }

        [Theory]
        [InlineData("identifier-only::")]   // truncated edit, must not mean auto-select
        [InlineData("::file.zip")]          // no identifier
        [InlineData("   ")]
        [InlineData("")]
        [InlineData(null)]
        public void Rejects_malformed_specs(string raw)
        {
            Assert.Throws<ArgumentException>(() => ContentSpec.Parse(raw));
        }
    }
}
