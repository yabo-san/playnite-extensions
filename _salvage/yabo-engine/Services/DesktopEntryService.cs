using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GithubLauncher.Services
{
    // [yabo-launcher fork — Linux launcher integration]
    // Turns yabo's game list into freedesktop ".desktop" shortcuts so every yabo game shows up in the Linux
    // app menu / rofi / wofi / fuzzel / GNOME-search / KDE-Kickoff with ZERO per-launcher config.
    //
    // WHY THIS EXISTS (the "look up existing integrations" answer):
    //   On Windows/Mac, surfacing Playnite games in a launcher (Flow Launcher, Raycast) is a two-part trick:
    //   a companion Playnite plugin (FlowLauncherExporter) writes the library to JSON, and the launcher reads
    //   that JSON and launches by URI (playnite://playnite/start/<Id>). On Linux Playnite isn't native, so
    //   yabo PLAYS THE EXPORTER ITSELF: the engine already has the game list (--list-json), and the Linux unit
    //   of "a launchable thing" is a .desktop file. We write one per game, Exec= back into our own --play path
    //   so staging / Proton / shader logic stays in the loop. Every Linux launcher indexes the folder for free.
    //
    // Files are tagged "yabo-<folderName>.desktop" so we own our namespace: on each run we rewrite ours and
    // PRUNE any stale yabo entry whose game is gone (uninstalled), without ever touching the user's own entries.
    public static class DesktopEntryService
    {
        private const string FilePrefix = "yabo-";

        public class Entry
        {
            public string FolderName = "";   // stable id → file name (yabo-<folder>.desktop)
            public string Name = "";         // display name in the menu
            public string LaunchName = "";    // the arg passed to --play (the catalog Name)
            public string? IconPath;          // ABSOLUTE local path; URLs/empty → no Icon (launcher shows default)
            public string? Category;          // yabo category → grouped under Categories=Game;Yabo;<Category>;
            public string? Comment;
        }

        // The freedesktop user applications dir, honoring $XDG_DATA_HOME (spec default ~/.local/share).
        public static string ApplicationsDir()
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var home = Environment.GetEnvironmentVariable("HOME")
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dataHome = !string.IsNullOrWhiteSpace(xdg) ? xdg! : Path.Combine(home, ".local", "share");
            return Path.Combine(dataHome, "applications");
        }

        // Render one .desktop file body. Public so it can be unit-tested without touching the filesystem.
        public static string Render(Entry e, string launcherExecPath)
        {
            var sb = new StringBuilder();
            sb.Append("[Desktop Entry]\n");
            sb.Append("Type=Application\n");
            sb.Append("Version=1.0\n");
            sb.Append("Name=").Append(SanitizeValue(e.Name)).Append('\n');
            if (!string.IsNullOrWhiteSpace(e.Comment))
                sb.Append("Comment=").Append(SanitizeValue(e.Comment!)).Append('\n');
            // Exec: quote the binary path and the game name per the spec's reserved-char rules.
            sb.Append("Exec=").Append(ExecQuote(launcherExecPath))
              .Append(" --play ").Append(ExecQuote(e.LaunchName)).Append('\n');
            if (!string.IsNullOrWhiteSpace(e.IconPath))
                sb.Append("Icon=").Append(SanitizeValue(e.IconPath!)).Append('\n');
            sb.Append("Terminal=false\n");
            // Categories must end with a semicolon; "Game" is the registered menu group, "Yabo" is our tag.
            var cats = "Game;Yabo;";
            if (!string.IsNullOrWhiteSpace(e.Category))
                cats += SanitizeValue(e.Category!.Replace(";", " ")) + ";";
            sb.Append("Categories=").Append(cats).Append('\n');
            sb.Append("StartupNotify=false\n");
            // Our marker so a prune pass can be 100% sure a file is ours even if the user renamed it.
            sb.Append("X-Yabo-Folder=").Append(SanitizeValue(e.FolderName)).Append('\n');
            return sb.ToString();
        }

        // Write all entries, prune stale yabo entries, refresh the desktop DB. Returns (written, pruned).
        // dryRun → compute + report only (used for testing on a non-Linux dev box).
        public static (int written, int pruned, string dir) Sync(
            IEnumerable<Entry> entries, string launcherExecPath, bool dryRun = false)
        {
            var dir = ApplicationsDir();
            var list = entries.ToList();
            var wanted = new HashSet<string>(
                list.Select(e => FilePrefix + Slug(e.FolderName) + ".desktop"),
                StringComparer.OrdinalIgnoreCase);

            if (!dryRun) Directory.CreateDirectory(dir);

            int written = 0;
            foreach (var e in list)
            {
                var file = Path.Combine(dir, FilePrefix + Slug(e.FolderName) + ".desktop");
                var body = Render(e, launcherExecPath);
                if (!dryRun)
                {
                    File.WriteAllText(file, body, new UTF8Encoding(false));
                    TryChmodExec(file);
                }
                written++;
            }

            // Prune: any yabo-*.desktop we previously wrote whose game is no longer in the set (uninstalled).
            int pruned = 0;
            if (!dryRun && Directory.Exists(dir))
            {
                foreach (var path in Directory.EnumerateFiles(dir, FilePrefix + "*.desktop"))
                {
                    var nameOnly = Path.GetFileName(path);
                    if (!wanted.Contains(nameOnly))
                    {
                        try { File.Delete(path); pruned++; } catch { /* leave it; not fatal */ }
                    }
                }
                TryUpdateDesktopDatabase(dir);
            }

            return (written, pruned, dir);
        }

        // --- helpers ---------------------------------------------------------

        // .desktop value-escaping: backslash + the leading-space/newline/CR/tab control codes (spec §value types).
        private static string SanitizeValue(string s)
            => s.Replace("\\", "\\\\")
                .Replace("\n", " ").Replace("\r", " ").Replace("\t", " ")
                .Trim();

        // Exec field quoting (spec §Exec): wrap in double quotes, escape " and \ (and reserved $ ` chars).
        private static string ExecQuote(string s)
        {
            var inner = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                         .Replace("$", "\\$").Replace("`", "\\`");
            return "\"" + inner + "\"";
        }

        // File-name slug: keep it filesystem-safe even if a folderName ever carries an odd char.
        private static string Slug(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s.Trim())
                sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
            var outp = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(outp) ? "game" : outp;
        }

        private static void TryChmodExec(string file)
        {
            // .desktop launchers don't strictly need +x in a menu, but GNOME flags non-exec ones as "untrusted".
            if (!OperatingSystem.IsLinux()) return;
            try
            {
                var mode = File.GetUnixFileMode(file);
                File.SetUnixFileMode(file, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            }
            catch { /* best-effort */ }
        }

        private static void TryUpdateDesktopDatabase(string dir)
        {
            if (!OperatingSystem.IsLinux()) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("update-desktop-database", $"\"{dir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { /* not installed on every distro; the entries still work, search index just lags */ }
        }
    }
}
