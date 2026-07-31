using System.Diagnostics;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Real 7-Zip CLI wrapper. SharpCompress CANNOT decode BCJ2-filtered / solid 7z
    /// entries — which is exactly the filter 7-Zip applies to .exe/.dll — so a SharpCompress .7z extract
    /// SILENTLY DROPS the game binaries while every data file lands fine. OutRun 2006 was the smoking gun:
    /// the repack's OR2006C2C.exe + dinput8.dll (emoose's tweaks, baked in) vanished, leaving a "0 exes"
    /// data-only folder, even though `7z l` shows them plainly inside the archive. So for .7z (and .rar) we
    /// shell out to a real 7z.exe. Bundled beside the engine (7z\7z.exe + 7z.dll, copied by build.ps1); falls
    /// back to a system 7-Zip install. When neither exists the caller falls back to SharpCompress with a warning.
    /// Also exposes a DRY-RUN listing (`ListEntries`) so a collision can decide self-contained-vs-marry-GitHub
    /// by peeking at the archive's file paths before committing.
    /// </summary>
    public static class SevenZipCli
    {
        private static string? _cached;
        private static bool _looked;

        public static string? FindExe()
        {
            if (_looked) return _cached;
            _looked = true;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "7z", "7z.exe"),
                Path.Combine(AppContext.BaseDirectory, "7z.exe"),
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            };
            _cached = candidates.FirstOrDefault(File.Exists);

            // [Linux engine readiness] The Windows 7-Zip exe is `7z.exe`; the official p7zip / 7-Zip-for-Linux
            // binaries are `7zz` (full) and `7za` (standalone) — find either on PATH so .7z/.rar extraction works
            // on Linux too. Also accept a bundled `7zz` next to the engine. Harmless on Windows (those files just
            // won't exist there).
            if (_cached == null)
            {
                var bundledLinux = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "7z", "7zz"),
                    Path.Combine(AppContext.BaseDirectory, "7zz"),
                };
                _cached = bundledLinux.FirstOrDefault(File.Exists)
                          ?? FindOnPath("7zz")
                          ?? FindOnPath("7za")
                          ?? FindOnPath("7z");
            }

            if (_cached != null) Log.Info($"7-Zip CLI: using {_cached}");
            else Log.Warn("7-Zip CLI: no 7z.exe / 7zz / 7za found (bundled, system, or on PATH) — .7z executables may be dropped by the SharpCompress fallback.");
            return _cached;
        }

        /// <summary>Resolve an executable basename against the PATH env var. Tries the bare name and, on Windows,
        /// the PATHEXT-implied `.exe`. Returns the first existing match, or null. Used to find the Linux 7-Zip
        /// binaries (7zz/7za) that don't live at a fixed path. Best-effort; never throws.</summary>
        private static string? FindOnPath(string name)
        {
            try
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathVar)) return null;
                var exts = OperatingSystem.IsWindows() ? new[] { "", ".exe" } : new[] { "" };
                foreach (var dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    foreach (var ext in exts)
                    {
                        var candidate = Path.Combine(dir.Trim(), name + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { /* PATH unreadable / weird entry — treat as not found */ }
            return null;
        }

        public static bool Available => FindExe() != null;

        /// <summary>True when the archive is a kind SharpCompress mangles (7z/rar executables) and we have a real 7z.</summary>
        public static bool ShouldUseFor(string archivePath)
        {
            var ext = Path.GetExtension(archivePath).ToLowerInvariant();
            return (ext == ".7z" || ext == ".rar") && Available;
        }

        /// <summary>DRY RUN: list entry paths inside the archive (forward-slash, directories excluded). Empty on failure.
        /// Reads the archive's own directory only — no extraction — so a collision can check "does this repack already
        /// contain an .exe?" cheaply before deciding whether to also pull the GitHub binary.</summary>
        public static List<string> ListEntries(string archivePath)
        {
            var exe = FindExe();
            var result = new List<string>();
            if (exe == null || !File.Exists(archivePath)) return result;
            try
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "l", "-slt", archivePath }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(120000);
                foreach (var line in outp.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Path = ", StringComparison.Ordinal))
                    {
                        var path = t.Substring(7).Trim();
                        if (!string.IsNullOrEmpty(path) &&
                            !string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase))
                            result.Add(path.Replace('\\', '/'));
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"7-Zip list failed for {Path.GetFileName(archivePath)}: {ex.Message}"); }
            return result;
        }

        /// <summary>Extract the whole archive (or just `members`, matched by 7-Zip's own path syntax) to destDir using
        /// full internal paths. Returns true only on a clean exit-0. 7-Zip stays within -o destDir (no path traversal).</summary>
        public static bool Extract(string archivePath, string destDir, IEnumerable<string>? members = null)
        {
            var exe = FindExe();
            if (exe == null || !File.Exists(archivePath)) return false;
            Directory.CreateDirectory(destDir);
            try
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                // x = extract WITH full paths; -o<dir> output (no space); -y assume-yes; -bso0/-bsp0 silence stdout/progress.
                foreach (var a in new[] { "x", archivePath, "-o" + destDir, "-y", "-bso0", "-bsp0" }) psi.ArgumentList.Add(a);
                if (members != null) foreach (var m in members) psi.ArgumentList.Add(m);
                using var p = Process.Start(psi)!;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) Log.Warn($"7-Zip extract exit {p.ExitCode} for {Path.GetFileName(archivePath)}: {err.Trim()}");
                return p.ExitCode == 0;
            }
            catch (Exception ex) { Log.Warn($"7-Zip extract failed for {Path.GetFileName(archivePath)}: {ex.Message}"); return false; }
        }
    }
}
