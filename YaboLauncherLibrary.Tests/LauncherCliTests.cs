using System.IO;
using Xunit;

namespace YaboLauncherLibrary.Tests
{
    public class LauncherCliTests
    {
        [Theory]
        [InlineData("rk-e2e-halo-ce", "rk-e2e-halo-ce")]
        [InlineData("quiver:x/y", "quiver:x/y")]
        [InlineData("has space", "\"has space\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
        [InlineData("", "\"\"")]
        public void Quotes_one_argument_for_the_Windows_command_line(string arg, string quoted)
        {
            Assert.Equal(quoted, LauncherCli.Quote(arg));
        }

        [Fact]
        public void A_missing_launcher_says_where_to_set_it()
        {
            var e = Assert.Throws<FileNotFoundException>(() => LauncherCli.Start("/nope/launcher.exe", "install", "x"));
            Assert.Contains("plugin settings", e.Message);
        }
    }
}
