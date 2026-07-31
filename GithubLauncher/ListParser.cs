using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GithubLauncherPlaynite
{
    /// <summary>
    /// Turns `GithubLauncher --list` console output into game names.
    ///
    /// Kept free of any Playnite dependency ON PURPOSE: this is the one part of
    /// the plugin that could not be verified against a populated library, so it
    /// has to be runnable standalone against captured output.
    ///
    /// What IS verified, against this machine: the banner, the version line, the
    /// update notice, the horizontal rule and the "ERROR: No games found in
    /// library." line are all correctly rejected, leaving zero games. What is NOT
    /// verified is a real list of games, because the library here is empty.
    /// </summary>
    public static class ListParser
    {
        // Console output carries ANSI clear-screen/cursor codes — verified as REAL
        // escape sequences in the captured output (0x1B 0x5B 0x32 0x4A = ESC[2J).
        //
        // The ESC is REQUIRED, hence the explicit (char)27. Written without it, this also matches
        // a bare "[U", which silently ate the opening of "[UPDATE AVAILABLE] New
        // version ..." and turned that notice into a game named "PDATE AVAILABLE]
        // New version v1.73 ...". Caught by running the filters over a populated
        // list rather than only over the empty one.
        private static readonly Regex Ansi =
            new Regex(((char)27) + @"\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

        private static readonly Regex Chrome = new Regex(
            @"^(Launcher Version|\[UPDATE|ERROR|Usage:|Commands:|Games:|Available games|-{1,2}[a-z])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Marker =
            new Regex(@"^(?:[-*•]|\d+[.)])\s+(?<n>.+)$", RegexOptions.Compiled);

        public static List<string> Parse(string output)
        {
            var games = new List<string>();
            if (string.IsNullOrWhiteSpace(output)) return games;

            // An explicit empty-library message is authoritative; never try to
            // salvage names out of it.
            if (output.IndexOf("No games found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return games;
            }

            foreach (var raw in output.Split('\n'))
            {
                var line = Ansi.Replace(raw, "").Trim().Trim('\r');
                if (line.Length == 0) continue;

                // Rules and box drawing.
                if (line.All(c => c == '─' || c == '-' || c == '=' || c == '_')) continue;

                // The ASCII-art banner is mostly punctuation; real titles are not.
                int letters = line.Count(char.IsLetterOrDigit);
                if (letters < line.Length / 3) continue;

                if (Chrome.IsMatch(line)) continue;

                var m = Marker.Match(line);
                if (m.Success) line = m.Groups["n"].Value.Trim();

                // Trailing metadata such as "   (installed)" or "   v1.2".
                line = Regex.Replace(line, @"\s{2,}.*$", "").Trim();
                if (line.Length == 0) continue;

                games.Add(line);
            }

            return games;
        }
    }
}
