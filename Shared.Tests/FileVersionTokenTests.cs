using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yabo.Shared;

namespace Shared.Tests
{
    public class FileVersionTokenTests
    {
        [Theory]
        // The real Dusklight files, which are the reason this exists.
        [InlineData("Dusklight (v1.4.1).zip", "1.4.1")]
        [InlineData("Dusklight (v1.0).zip", "1.0")]
        // Shapes seen across the curated catalogue.
        [InlineData("Sega Rally 2 ~ 25th Anniversary Edition 2.1.0.7z", "2.1.0")]
        [InlineData("Game_v2.1.zip", "2.1")]
        [InlineData("Some Port [v0.9.2].7z", "0.9.2")]
        [InlineData("Thing v3.zip", "3.0")]
        public void Parses_semantic_versions(string name, string expected)
        {
            Assert.Equal(Version.Parse(expected), FileVersionToken.Parse(name));
        }

        [Theory]
        [InlineData("Build_r123.zip", 123)]
        [InlineData("port build 45.7z", 45)]
        public void Parses_revisions_below_any_real_version(string name, int rev)
        {
            var parsed = FileVersionToken.Parse(name);

            Assert.Equal(new Version(0, 0, 0, rev), parsed);
            // A revision must never outrank a semantic version.
            Assert.True(parsed < FileVersionToken.Parse("thing v0.1.zip"));
        }

        [Theory]
        // The identifier date-stamp style archive.org applies on collision.
        [InlineData("silent-hill-homecoming_202601.7z", 2026, 1, 0)]
        [InlineData("prototype-2_20260115.zip", 2026, 1, 15)]
        public void Parses_date_stamps(string name, int y, int m, int d)
        {
            Assert.Equal(new Version(y, m, d), FileVersionToken.Parse(name));
        }

        [Theory]
        [InlineData("Prey.zip")]
        [InlineData("gamedata.zip")]
        [InlineData("Banjo Kazooie Recompiled.7z")]
        [InlineData("")]
        [InlineData(null)]
        public void Returns_null_when_there_is_no_version(string name)
        {
            // Null rather than 0.0, so a caller can distinguish "unversioned" from "v0".
            Assert.Null(FileVersionToken.Parse(name));
        }

        [Fact]
        public void An_extension_is_not_mistaken_for_a_version()
        {
            Assert.Null(FileVersionToken.Parse("archive.7z"));
            Assert.Null(FileVersionToken.Parse("setup.zip"));
        }

        [Fact]
        public void Newest_picks_the_highest_version_not_the_biggest_file()
        {
            // The whole point. Largest-archive gets Dusklight right by coincidence;
            // here the newer build is SMALLER, which is where size selection breaks.
            var files = new List<FileCandidate>
            {
                new FileCandidate { Name = "Dusklight (v1.0).zip", SizeBytes = 999_000_000 },
                new FileCandidate { Name = "Dusklight (v1.4.1).zip", SizeBytes = 500_000_000 },
            };

            Assert.Equal("Dusklight (v1.4.1).zip", FileVersionToken.Newest(files)?.Name);
            // ... and confirm the old selector really would have got it wrong.
            Assert.Equal("Dusklight (v1.0).zip", ContentSpec.LargestArchive(files));
        }

        [Fact]
        public void Newest_matches_largest_on_the_real_Dusklight_files()
        {
            var files = new List<FileCandidate>
            {
                new FileCandidate { Name = "Dusklight (v1.4.1).zip", SizeBytes = 977_300_000 },
                new FileCandidate { Name = "Dusklight (v1.0).zip", SizeBytes = 973_200_000 },
                new FileCandidate { Name = "Twilight Princess DUSK.jpg", SizeBytes = 400_000 },
            };

            Assert.Equal("Dusklight (v1.4.1).zip", FileVersionToken.Newest(files)?.Name);
        }

        [Fact]
        public void Newest_ignores_unversioned_files_entirely()
        {
            var files = new List<FileCandidate>
            {
                new FileCandidate { Name = "readme.txt", SizeBytes = 100 },
                new FileCandidate { Name = "Game (v1.2).zip", SizeBytes = 200 },
            };

            Assert.Equal("Game (v1.2).zip", FileVersionToken.Newest(files)?.Name);
        }

        [Fact]
        public void Newest_returns_null_when_nothing_is_versioned()
        {
            var files = new List<FileCandidate>
            {
                new FileCandidate { Name = "Prey.zip", SizeBytes = 1 },
                new FileCandidate { Name = "gamedata.zip", SizeBytes = 2 },
            };

            // Caller falls back to largest-archive; there is nothing to order on.
            Assert.Null(FileVersionToken.Newest(files));
        }

        [Fact]
        public void Ties_break_on_size()
        {
            var files = new List<FileCandidate>
            {
                new FileCandidate { Name = "Game (v2.0) lite.zip", SizeBytes = 100 },
                new FileCandidate { Name = "Game (v2.0) full.zip", SizeBytes = 900 },
            };

            Assert.Equal("Game (v2.0) full.zip", FileVersionToken.Newest(files)?.Name);
        }
    }
}
