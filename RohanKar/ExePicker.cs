using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RohanKarPlaynite
{
    /// <summary>
    /// Picks which .exe in a game folder is actually the game.
    ///
    /// This is the whole problem. RohanKar's library.db records nothing about
    /// installed games (every table is empty on a real install), so the folder on
    /// disk is the only source of truth, and a repack folder routinely holds a
    /// dozen executables of which exactly one launches the game.
    ///
    /// Naive rules fail hard, measured against a real 19-game library:
    ///   - "biggest exe" picks PhysX (34MB) over Blur.exe (13MB), gfwlivesetup
    ///     (259MB) over LP2DX11.exe, and dotnetfx (23MB) over Dead To Rights 2.
    ///   - "first exe found" picks Config.exe for OutRun 2006 and SH2EEconfig.exe
    ///     for Silent Hill 2.
    ///
    /// So candidates are SCORED. Title match is the strongest real signal, depth
    /// is next (the game sits at the top, support junk lives in subfolders), and
    /// size only breaks ties because it is actively misleading as a primary one.
    ///
    /// Nothing here is certain, so the caller keeps every runner-up and exposes it
    /// as a secondary Playnite action — a wrong guess is then one right-click to
    /// fix rather than something that needs a code change.
    /// </summary>
    public static class ExePicker
    {
        /// <summary>Runtimes and installers shipped inside repacks. Never the game.</summary>
        private static readonly Regex Never = new Regex(
            @"^(vcredist|vc_redist|dotnetfx|dxwebsetup|dxsetup|gfwlivesetup|physx|" +
            @"oalinst|directx|unins\d*|unwise|.*_uninst|.*_code)$|(redist|vcredist|directx)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Real programs, but not THE game: configurators, mod tools, validators.</summary>
        private static readonly Regex Demote = new Regex(
            @"(config|setup|launcher|crashreport|validator|dgvoodoocpl|onisplit|" +
            @"troubleshoot|controls|replace-files)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Directories that hold support material rather than the game.</summary>
        private static readonly Regex BadDir = new Regex(
            @"(redist|soft|support|troubleshooting|directx|_commonredist|backup|tools|ar patch)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A bare `ws\b` matches "PPSSPPWindoWS" — any name ending in "ws" — which
        // silently cost WipEout its 64-bit build. The separator is required.
        private static readonly Regex Widescreen = new Regex(
            @"(widescreen|[_\-. ]ws\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Modern = new Regex(
            @"(dx11|64)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public class Candidate
        {
            public string FullPath;
            public string RelativePath;
            public double Score;
            public bool Excluded;
        }

        private static IEnumerable<string> Tokens(string s)
        {
            return Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+").Where(t => t.Length > 2);
        }

        private static string Flatten(string s)
        {
            return Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
        }

        public static double Score(string relativePath, long sizeBytes, string title)
        {
            string name = Path.GetFileNameWithoutExtension(relativePath) ?? "";
            var parts = relativePath.Split(Path.DirectorySeparatorChar);
            var dirs = parts.Take(Math.Max(0, parts.Length - 1)).ToArray();
            int depth = parts.Length - 1;

            if (Never.IsMatch(name) || dirs.Any(d => Never.IsMatch(d)))
            {
                return double.NegativeInfinity;
            }

            double s = 0;

            if (Demote.IsMatch(name)) s -= 60;
            if (dirs.Any(d => BadDir.IsMatch(d))) s -= 40;

            var titleTokens = new HashSet<string>(Tokens(title));
            var nameTokens = new HashSet<string>(Tokens(name));
            if (titleTokens.Count > 0 && nameTokens.Count > 0)
            {
                int overlap = titleTokens.Count(t => nameTokens.Contains(t));
                if (overlap > 0) s += 50 * overlap;

                // Squashed forms: "BanjoRecompiled" vs "Banjo Kazooie Recompiled".
                string fn = Flatten(name), ft = Flatten(title);
                if (fn.Length > 0 && (ft.Contains(fn) || fn.Contains(ft))) s += 40;
            }

            s -= depth * 8;
            s += Math.Min(sizeBytes / 1048576.0, 40) * 0.5;

            // Repacks ship widescreen variants beside the originals; prefer them.
            if (Widescreen.IsMatch(name)) s += 15;

            // Prefer modern renderer / 64-bit builds when a repack ships several.
            if (Modern.IsMatch(name)) s += 8;

            return s;
        }

        /// <summary>
        /// All executables under <paramref name="gameDir"/>, best first. Runtimes are
        /// marked Excluded rather than dropped, so a folder that contains ONLY
        /// runtimes can be reported as "nothing launchable" instead of silently
        /// producing a game that runs a vcredist.
        /// </summary>
        public static List<Candidate> Rank(string gameDir, string title, int maxDepth = 3)
        {
            var found = new List<Candidate>();
            Walk(gameDir, gameDir, 0, maxDepth, found, title);
            return found.OrderByDescending(c => c.Score).ToList();
        }

        private static void Walk(string root, string dir, int depth, int maxDepth,
                                 List<Candidate> found, string title)
        {
            if (depth > maxDepth) return;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); }
            catch { return; }

            foreach (var f in files)
            {
                long size;
                try { size = new FileInfo(f).Length; } catch { size = 0; }

                string rel = f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                double sc = Score(rel, size, title);

                found.Add(new Candidate
                {
                    FullPath = f,
                    RelativePath = rel,
                    Score = double.IsNegativeInfinity(sc) ? double.MinValue : sc,
                    Excluded = double.IsNegativeInfinity(sc),
                });
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (var s in subs)
            {
                Walk(root, s, depth + 1, maxDepth, found, title);
            }
        }
    }
}
