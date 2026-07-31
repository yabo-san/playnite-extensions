using GithubLauncher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GithubLauncher.Services
{
    /// <summary>
    /// Outcome of placing a single declared data file into a port's install dir.
    /// </summary>
    public class ProvideResult
    {
        public required string Name { get; init; }
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? PlacedPath { get; init; }

        public override string ToString() =>
            (Success ? "[OK]   " : "[MISS] ") + Name +
            (string.IsNullOrEmpty(Message) ? string.Empty : " - " + Message);
    }

    /// <summary>
    /// ROM/Data Library (#5): a central folder where the user drops their own game
    /// data (ROMs, IWADs, paks, disc images). Files are imported once and then
    /// "provided" (hardlinked or copied) into each port's expected install location.
    /// All operations are non-fatal and idempotent.
    /// </summary>
    public class RomLibraryService
    {
        private readonly string _libraryPath;

        public RomLibraryService(AppSettings settings)
        {
            _libraryPath = settings.ResolveLibraryPath();
        }

        public RomLibraryService(string libraryPath)
        {
            _libraryPath = string.IsNullOrWhiteSpace(libraryPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Library")
                : libraryPath;
        }

        public string LibraryPath => _libraryPath;

        public void EnsureLibraryExists()
        {
            try { Directory.CreateDirectory(_libraryPath); }
            catch (Exception ex) { Log($"Failed to create library at {_libraryPath}: {ex.Message}"); }
        }

        /// <summary>
        /// Copies a user-picked file into the Library, keeping its original file name.
        /// Idempotent: a file already present with matching content is left as-is.
        /// Returns the destination path in the Library, or null on failure.
        /// </summary>
        public string? ImportToLibrary(string sourcePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    Log($"ImportToLibrary: source missing: {sourcePath}");
                    return null;
                }

                EnsureLibraryExists();

                var fileName = Path.GetFileName(sourcePath);
                var destPath = Path.Combine(_libraryPath, fileName);

                // Idempotent: if an identically-named file already exists with the
                // same length, assume it's the same import and skip the copy.
                if (File.Exists(destPath) &&
                    new FileInfo(destPath).Length == new FileInfo(sourcePath).Length)
                {
                    Log($"ImportToLibrary: already present: {destPath}");
                    return destPath;
                }

                File.Copy(sourcePath, destPath, overwrite: true);
                Log($"ImportToLibrary: copied {fileName} -> {destPath}");
                return destPath;
            }
            catch (Exception ex)
            {
                Log($"ImportToLibrary failed for {sourcePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Copies a source file into the Library under a SPECIFIC name (so it matches a port's declared
        /// dataFile name, e.g. a NAS "Super Mario 64 USA.z64" staged as "Super Mario 64.z64"). Idempotent
        /// by name+length. Returns the destination path or null.
        /// </summary>
        public string? ImportAs(string sourcePath, string targetName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    Log($"ImportAs: source missing: {sourcePath}");
                    return null;
                }
                if (string.IsNullOrWhiteSpace(targetName))
                    return null;

                EnsureLibraryExists();
                var destPath = Path.Combine(_libraryPath, targetName);
                if (File.Exists(destPath) &&
                    new FileInfo(destPath).Length == new FileInfo(sourcePath).Length)
                {
                    Log($"ImportAs: already present: {destPath}");
                    return destPath;
                }
                File.Copy(sourcePath, destPath, overwrite: true);
                Log($"ImportAs: {sourcePath} -> {destPath}");
                return destPath;
            }
            catch (Exception ex)
            {
                Log($"ImportAs failed for {sourcePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// For each data file the port declares, find the matching file in the Library
        /// (by exact name, then by hash if provided) and place it into
        /// GetInstallPath() + targetSubpath. Hardlinks when on the same volume,
        /// otherwise copies. Validates hash when supplied. Non-fatal + idempotent.
        /// </summary>
        public List<ProvideResult> ProvideToPort(GameInfo game, string gamesFolder)
        {
            var results = new List<ProvideResult>();

            if (game == null)
                return results;

            if (game.DataFiles == null || game.DataFiles.Count == 0)
                return results; // nothing declared

            string installPath;
            try
            {
                installPath = game.GetInstallPath(gamesFolder);
            }
            catch (Exception ex)
            {
                Log($"ProvideToPort: cannot resolve install path for {game.Name}: {ex.Message}");
                return results;
            }

            if (string.IsNullOrWhiteSpace(installPath))
            {
                results.Add(new ProvideResult { Name = game.Name ?? "?", Success = false, Message = "Install path not resolved." });
                return results;
            }

            // [yabo-launcher fork] Place data NEXT TO the resolved exe, not blindly at the install
            // root. Engines load their data relative to their own exe; several ports extract that exe
            // into a versioned subfolder (devilutionx\, dsda-doom-0.29.4-win-x64\, vkquake2-1.5.9_win64\,
            // Dr. Mario 64 Recompiled x64-Release\). ResolveExecutableDir mirrors launch-time exe
            // resolution and returns the install root unchanged when the exe is already at the root,
            // so this is a no-op for the ~22 ports whose exe sits at the root.
            string placementBase;
            try
            {
                placementBase = game.ResolveExecutableDir(installPath);
                if (string.IsNullOrWhiteSpace(placementBase))
                    placementBase = installPath;
            }
            catch (Exception ex)
            {
                Log($"ProvideToPort: exe-dir resolution failed for {game.Name}, using install root: {ex.Message}");
                placementBase = installPath;
            }

            if (!string.Equals(placementBase, installPath, StringComparison.OrdinalIgnoreCase))
                Log($"ProvideToPort: placing data next to exe for {game.Name}: {placementBase}");

            // [yabo-launcher fork — REVERTED 2026-06-01] We USED to drop a portable.txt next to N64Recomp
            // exes (ROM staged as <game_id>.z64) on the theory that the recomp would auto-load a "stored ROM"
            // from config_path/<game_id>.z64 and skip its in-game picker. That theory was WRONG and ACTIVELY
            // HARMFUL: Banjo-Kazooie: Recompiled crashes on startup (0xc0000409 abort in ucrtbase) whenever
            // portable.txt is present. These recomps use an in-app ROM-SELECT MENU (picker), NOT auto-load —
            // the portable.txt config-path redirect breaks them. So we no longer write it. The launcher just
            // stages the ROM next to the exe under its real name; the user points the recomp's menu at it once
            // and it remembers. (Verified live: Banjo launches with NO portable.txt; crashes WITH it.)

            foreach (var need in game.DataFiles)
            {
                var label = need?.Name ?? "(unnamed)";
                try
                {
                    if (need == null || string.IsNullOrWhiteSpace(need.Name))
                    {
                        results.Add(new ProvideResult { Name = label, Success = false, Message = "Entry has no name." });
                        continue;
                    }

                    var source = FindInLibrary(need);
                    if (source == null)
                    {
                        results.Add(new ProvideResult { Name = label, Success = false, Message = "Not found in Library." });
                        continue;
                    }

                    // Validate hash if one was declared.
                    if (!ValidateHash(source, need, out var hashError))
                    {
                        results.Add(new ProvideResult { Name = label, Success = false, Message = hashError });
                        continue;
                    }

                    // Target = exe dir + optional subpath + the expected file name. targetSubpath is now
                    // relative to the exe's directory (e.g. Quake II's "baseq2" sits under the engine exe).
                    var subpath = (need.TargetSubpath ?? string.Empty).Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    var targetDir = string.IsNullOrWhiteSpace(subpath) ? placementBase : Path.Combine(placementBase, subpath);
                    Directory.CreateDirectory(targetDir);
                    var targetPath = Path.Combine(targetDir, need.Name);

                    // Idempotent: skip if already placed with matching length.
                    if (File.Exists(targetPath) &&
                        new FileInfo(targetPath).Length == new FileInfo(source).Length)
                    {
                        results.Add(new ProvideResult { Name = label, Success = true, Message = "Already present.", PlacedPath = targetPath });
                        continue;
                    }

                    var method = PlaceFile(source, targetPath);
                    results.Add(new ProvideResult { Name = label, Success = true, Message = method, PlacedPath = targetPath });
                }
                catch (Exception ex)
                {
                    Log($"ProvideToPort: error placing {label} for {game.Name}: {ex.Message}");
                    results.Add(new ProvideResult { Name = label, Success = false, Message = ex.Message });
                }
            }

            return results;
        }

        /// <summary>
        /// Locate the file for a need in the Library: exact filename first, then by
        /// SHA-1 / XXH3 hash across all Library files if a hash was declared.
        /// </summary>
        private string? FindInLibrary(DataFileNeed need)
        {
            if (!Directory.Exists(_libraryPath))
                return null;

            // 1) exact name match
            var byName = Path.Combine(_libraryPath, need.Name!);
            if (File.Exists(byName))
                return byName;

            // 2) hash match (only if a hash is declared)
            if (string.IsNullOrWhiteSpace(need.Sha1) && string.IsNullOrWhiteSpace(need.Xxh3))
                return null;

            foreach (var candidate in Directory.EnumerateFiles(_libraryPath))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(need.Sha1) &&
                        HashEquals(ComputeSha1(candidate), need.Sha1))
                    {
                        return candidate;
                    }

                    // XXH3 matching intentionally not used for lookup: see note on
                    // ValidateHash. Filename + SHA-1 cover the supported cases.
                }
                catch (Exception ex)
                {
                    Log($"FindInLibrary: hash failed on {candidate}: {ex.Message}");
                }
            }

            return null;
        }

        private bool ValidateHash(string source, DataFileNeed need, out string error)
        {
            error = string.Empty;
            try
            {
                if (!string.IsNullOrWhiteSpace(need.Sha1))
                {
                    var actual = ComputeSha1(source);
                    if (!HashEquals(actual, need.Sha1))
                    {
                        error = $"SHA-1 mismatch (expected {need.Sha1}, got {actual}).";
                        return false;
                    }
                }

                // NOTE: XXH3-64 (used by N64Recomp) is parsed and round-tripped but
                // not validated here, because a reference-accurate XXH3 implementation
                // is not bundled (a wrong port would reject valid ROMs). When a real
                // XXH3 routine is wired in, validate need.Xxh3 here. Until then,
                // name-based matching + optional SHA-1 cover the supported cases.
            }
            catch (Exception ex)
            {
                error = $"Hash validation error: {ex.Message}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Places <paramref name="source"/> at <paramref name="targetPath"/>. Tries a
        /// hardlink when both reside on the same volume (cheap, no extra disk),
        /// falls back to a symlink, then to a plain copy. Returns the method used.
        /// </summary>
        private string PlaceFile(string source, string targetPath)
        {
            // Remove any stale target first so link/copy can recreate it.
            if (File.Exists(targetPath))
            {
                try { File.Delete(targetPath); } catch { /* fall through to copy attempt */ }
            }

            if (SameVolume(source, targetPath))
            {
                // Hardlink: identical bytes, no extra storage, same volume only.
                if (TryCreateHardLink(targetPath, source))
                    return "Hardlinked.";

                // Symlink fallback (also same-volume friendly, may need privileges).
                try
                {
                    File.CreateSymbolicLink(targetPath, source);
                    return "Symlinked.";
                }
                catch (Exception ex)
                {
                    Log($"PlaceFile: symlink fallback failed ({ex.Message}); copying.");
                }
            }

            // Cross-volume (or all links failed): copy.
            File.Copy(source, targetPath, overwrite: true);
            return "Copied.";
        }

        private static bool SameVolume(string a, string b)
        {
            try
            {
                var ra = Path.GetPathRoot(Path.GetFullPath(a));
                var rb = Path.GetPathRoot(Path.GetFullPath(b));
                return !string.IsNullOrEmpty(ra) &&
                       string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCreateHardLink(string linkPath, string existingFile)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return CreateHardLink(linkPath, existingFile, IntPtr.Zero);
                }

                // POSIX: link(2) via the link command keeps this dependency-free.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ln",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(existingFile);
                psi.ArgumentList.Add(linkPath);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(10000);
                return p.ExitCode == 0 && File.Exists(linkPath);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private static string ComputeSha1(string path)
        {
            using var sha = SHA1.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static bool HashEquals(string actual, string expected)
        {
            return !string.IsNullOrWhiteSpace(actual) &&
                   actual.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
                         .Equals(expected.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                                 StringComparison.OrdinalIgnoreCase);
        }

        private static void Log(string message) =>
            System.Diagnostics.Debug.WriteLine($"[RomLibrary] {message}");
    }
}
