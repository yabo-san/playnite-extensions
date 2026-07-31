using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Locates game content the user already owns through Steam, with NO
    /// hardcoded paths. This mirrors the exact algorithm ironwail uses (Quake/common.c + steam.c):
    ///
    ///   1. read the Steam client root from HKCU\Software\Valve\Steam\SteamPath
    ///   2. parse &lt;steam&gt;\config\libraryfolders.vdf for every library folder (any drive)
    ///   3. read &lt;library&gt;\steamapps\appmanifest_&lt;appid&gt;.acf -> "installdir"
    ///   4. resolve &lt;library&gt;\steamapps\common\&lt;installdir&gt;
    ///
    /// Because library paths come out of libraryfolders.vdf, this works regardless of which drive
    /// the user's Steam library lives on. Everything is best-effort: any failure returns null/empty.
    /// Windows-only for now (returns null elsewhere); Linux/macOS Steam roots can be added later.
    /// </summary>
    public static class SteamContentLocator
    {
        // Steam appids for the content we care about.
        public const int AppIdDoom    = 2280; // "DOOM + DOOM II" (2024 KEX re-release; installdir "Ultimate Doom")
        public const int AppIdDoomII  = 2300; // legacy standalone "DOOM II"
        public const int AppIdFinalDoom = 2290; // legacy "Final DOOM"
        public const int AppIdQuake   = 2310; // "Quake" (classic + rerelease subfolder)
        public const int AppIdQuakeII = 2320; // "Quake II" (classic + rerelease subfolder; baseq2\pak0.pak)

        // IWAD basenames DoomLauncher recognises (IWadInfo.FromFileName). We only register these.
        private static readonly string[] KnownIWads =
        {
            "doom.wad", "doom1.wad", "doom2.wad", "plutonia.wad", "tnt.wad",
            "heretic.wad", "hexen.wad", "hexdd.wad", "strife1.wad", "freedoom1.wad", "freedoom2.wad",
            "nerve.wad", "sigil.wad", "sigil2.wad", "masterlevels.wad",
        };

        /// <summary>Steam client install root, or null if Steam isn't installed / not on Windows.</summary>
        public static string? GetSteamPath()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath") as string;
                if (string.IsNullOrWhiteSpace(path))
                    return null;
                // Steam stores this with forward slashes / lowercase; normalise to a real Windows path.
                path = path.Replace('/', '\\');
                return Directory.Exists(path) ? path : null;
            }
            catch (Exception ex)
            {
                Log.Error($"[SteamContentLocator] GetSteamPath failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>All Steam library roots (the client root + every entry in libraryfolders.vdf).</summary>
        public static IReadOnlyList<string> GetLibraryRoots()
        {
            var roots = new List<string>();
            var steam = GetSteamPath();
            if (steam == null)
                return roots;

            roots.Add(steam);

            var vdf = Path.Combine(steam, "config", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                return roots;

            try
            {
                var text = File.ReadAllText(vdf);
                // Each library has a:  "path"   "X:\\Some\\Dir"
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"(.+?)\"", RegexOptions.IgnoreCase))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\"); // unescape VDF backslashes
                    if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                        roots.Add(p);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[SteamContentLocator] parsing libraryfolders.vdf failed: {ex.Message}");
            }

            return roots;
        }

        /// <summary>
        /// Resolves the install directory of a Steam app by id, searching every library:
        /// reads appmanifest_&lt;appId&gt;.acf for "installdir" and returns steamapps\common\&lt;installdir&gt;.
        /// </summary>
        public static string? FindAppInstallDir(int appId)
        {
            foreach (var lib in GetLibraryRoots())
            {
                try
                {
                    var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                    if (!File.Exists(manifest))
                        continue;

                    var text = File.ReadAllText(manifest);
                    var m = Regex.Match(text, "\"installdir\"\\s+\"(.+?)\"", RegexOptions.IgnoreCase);
                    if (!m.Success)
                        continue;

                    var installDir = m.Groups[1].Value.Replace("\\\\", "\\");
                    var full = Path.Combine(lib, "steamapps", "common", installDir);
                    if (Directory.Exists(full))
                        return full;
                }
                catch (Exception ex)
                {
                    Log.Error($"[SteamContentLocator] FindAppInstallDir({appId}) in {lib} failed: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Absolute paths to Steam-owned IWAD files (doom.wad, doom2.wad, tnt.wad, plutonia.wad, …).
        /// Scans the DOOM/DOOM II/Final DOOM installs and their "rerelease" subfolders (the KEX
        /// re-release bundles all classic IWADs under one rerelease\ folder). De-duplicated by basename.
        /// </summary>
        public static IReadOnlyList<string> FindDoomIWads()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var appId in new[] { AppIdDoom, AppIdDoomII, AppIdFinalDoom })
            {
                var install = FindAppInstallDir(appId);
                if (install == null)
                    continue;

                // The classic IWADs ship in the install root and/or a rerelease\ subfolder.
                foreach (var dir in new[] { install, Path.Combine(install, "rerelease") })
                {
                    if (!Directory.Exists(dir))
                        continue;
                    foreach (var wad in KnownIWads)
                    {
                        var path = Path.Combine(dir, wad);
                        if (File.Exists(path) && seen.Add(wad))
                            result.Add(path);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Distinct directories that contain Steam-owned IWADs (e.g. the KEX "…\rerelease" folder),
        /// using forward slashes (Qt/Doomseeker-friendly, sidesteps INI backslash-escaping). Suitable
        /// for a WAD search path. Empty if no Steam Doom is found.
        /// </summary>
        public static IReadOnlyList<string> FindDoomIWadDirs()
        {
            var dirs = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var wad in FindDoomIWads())
            {
                var dir = Path.GetDirectoryName(wad);
                if (string.IsNullOrEmpty(dir))
                    continue;
                if (seen.Add(dir))
                    dirs.Add(dir.Replace('\\', '/'));
            }
            return dirs;
        }

        /// <summary>
        /// The Quake base directory (the folder containing id1\pak0.pak) for Steam's Quake.
        /// Prefers the rerelease subfolder when it has id1, otherwise the classic install root.
        /// Returns null if Steam Quake isn't found.
        /// </summary>
        public static string? FindQuakeBaseDir()
        {
            var install = FindAppInstallDir(AppIdQuake);
            if (install == null)
                return null;

            // Classic id1 lives at <install>\id1; rerelease at <install>\rerelease\id1.
            var rerelease = Path.Combine(install, "rerelease");
            if (File.Exists(Path.Combine(install, "id1", "pak0.pak")))
                return install;
            if (File.Exists(Path.Combine(rerelease, "id1", "pak0.pak")))
                return rerelease;

            // id1 exists but no pak0 yet (unlikely) — still hand back the most plausible basedir.
            if (Directory.Exists(Path.Combine(install, "id1")))
                return install;
            if (Directory.Exists(Path.Combine(rerelease, "id1")))
                return rerelease;

            return install;
        }

        // [yabo-launcher fork] Name->appid fallback for steam-data cards that don't carry an explicit
        // apps.json "steamAppId". Keyed by repository (owner/repo, case-insensitive) so it's robust against
        // display-name edits. Cards NOT listed here must set steamAppId in apps.json.
        private static readonly Dictionary<string, int> SteamDataAppIdByRepo =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["andrei-drexler/ironwail"]      = AppIdQuake,   // Quake (id1\pak0.pak, pak1.pak)
                ["kondrak/vkQuake2"]             = AppIdQuakeII, // Quake II (baseq2\pak0.pak, pak1.pak)
                ["OpenRCT2/OpenRCT2"]            = 285330,       // RollerCoaster Tycoon 2 (Data\g1.dat)
                ["TheFlyingFoool/DuckGameRebuilt"] = 312530,     // Duck Game
            };

        /// <summary>
        /// [yabo-launcher fork] Resolves the Steam appid a steam-data card sources its DATA from: the explicit
        /// per-card <paramref name="explicitAppId"/> (apps.json "steamAppId") when set, otherwise a built-in
        /// repository->appid fallback for the known cards. Returns null when neither resolves (the card must then
        /// declare steamAppId).
        /// </summary>
        public static int? ResolveSteamDataAppId(int? explicitAppId, string? repository)
        {
            if (explicitAppId.HasValue && explicitAppId.Value > 0)
                return explicitAppId.Value;
            if (!string.IsNullOrWhiteSpace(repository) &&
                SteamDataAppIdByRepo.TryGetValue(repository.Trim(), out var id))
                return id;
            return null;
        }

        /// <summary>
        /// [yabo-launcher fork] Finds a named data file (e.g. "pak0.pak", "g1.dat") inside a Steam app's install,
        /// searching the install root, its "rerelease" subfolder, and (recursively) the whole install dir — the
        /// classic IWAD/pak layout varies by title (id1\, baseq2\, rerelease\...). Case-insensitive match on the
        /// basename. Returns the absolute path of the first hit, or null. Best-effort.
        /// </summary>
        public static string? FindSteamDataFile(int appId, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;
            var install = FindAppInstallDir(appId);
            if (install == null || !Directory.Exists(install))
                return null;

            // Prefer shallow well-known spots first (avoids picking an unrelated copy deep in the tree).
            foreach (var dir in new[] { install, Path.Combine(install, "rerelease") })
            {
                if (!Directory.Exists(dir)) continue;
                var direct = Path.Combine(dir, fileName);
                if (File.Exists(direct)) return direct;
            }

            try
            {
                return Directory.EnumerateFiles(install, fileName, SearchOption.AllDirectories)
                                .OrderBy(p => p.Length) // shallowest wins
                                .FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Error($"[SteamContentLocator] FindSteamDataFile({appId}, {fileName}) failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>True if the given Steam app is installed/owned (its install dir resolves on disk).</summary>
        public static bool IsAppInstalled(int appId) => FindAppInstallDir(appId) != null;
    }
}
