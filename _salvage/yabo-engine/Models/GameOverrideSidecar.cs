using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GithubLauncher.Services;

namespace GithubLauncher.Models
{
    // [per-game override sidecar — gamevault-exec style, no-rebuild like favorite-artists.json]
    /// <summary>
    /// [yabo-launcher fork] Per-game LAUNCH OVERRIDE read from disk at launch — modeled on GameVault's
    /// per-game <c>gamevault-exec</c> metadata (LaunchExecutable / LaunchParameters / InstallerParameters).
    /// Lets the user fix an odd launch (wrong auto-picked exe, missing/extra args, wrong working dir, silent
    /// installer flags) for ONE game WITHOUT editing apps.json or rebuilding the catalog — exactly the
    /// no-rebuild pattern of <c>favorite-artists.json</c>.
    ///
    /// The sidecar is a single JSON file named <c>.yabo-override.json</c> placed at the ROOT of the game's
    /// install directory (so it travels with the install and is naturally keyed by location — no separate
    /// folderName map to maintain). It is entirely OPTIONAL: absent or malformed → the launcher behaves
    /// exactly as it does today (see <see cref="TryLoad"/>, which never throws).
    ///
    /// Example <c>&lt;installDir&gt;\.yabo-override.json</c>:
    /// <code>
    /// {
    ///   "launchExe": "bin/win64/game.exe",
    ///   "launchArgs": "-skipintro -windowed",
    ///   "workingDir": "bin/win64",
    ///   "installerArgs": "/S"
    /// }
    /// </code>
    /// </summary>
    public sealed class GameOverrideSidecar
    {
        /// <summary>The sidecar file name, dropped at the root of a game's install directory.</summary>
        public const string FileName = ".yabo-override.json";

        /// <summary>
        /// Which executable to launch — a bare filename (matched anywhere under the install dir) or a path
        /// relative to the install dir. Overrides the auto "largest/first exe" pick. If it can't be resolved
        /// at launch time the launcher falls back to the normal pick (and logs a warning).
        /// </summary>
        [JsonPropertyName("launchExe")]
        public string? LaunchExe { get; set; }

        /// <summary>Extra command-line argument(s) appended to the launched exe (whitespace-separated).</summary>
        [JsonPropertyName("launchArgs")]
        public string? LaunchArgs { get; set; }

        /// <summary>
        /// Optional working-directory override — a bare folder name / relative path under the install dir, or
        /// an absolute path. When unset the launcher uses its normal cwd (the resolved exe's directory).
        /// </summary>
        [JsonPropertyName("workingDir")]
        public string? WorkingDir { get; set; }

        /// <summary>
        /// Optional silent-install flag(s) for an installer-style repack (the GameVault InstallerParameters
        /// analogue). Carried for completeness so installer flows can honor it; not consumed on the plain
        /// direct-launch path.
        /// </summary>
        [JsonPropertyName("installerArgs")]
        public string? InstallerArgs { get; set; }

        /// <summary>True if anything is actually overridden (an all-null/empty sidecar is treated as absent).</summary>
        [JsonIgnore]
        public bool HasAnything =>
            !string.IsNullOrWhiteSpace(LaunchExe) ||
            !string.IsNullOrWhiteSpace(LaunchArgs) ||
            !string.IsNullOrWhiteSpace(WorkingDir) ||
            !string.IsNullOrWhiteSpace(InstallerArgs);

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// [per-game override sidecar — gamevault-exec style, no-rebuild like favorite-artists.json]
        /// Try to load <c>.yabo-override.json</c> from the given install directory. Returns the parsed
        /// override, or null when there's no (usable) sidecar. NEVER throws — a missing file returns null
        /// silently; a malformed file logs a warning and returns null so the launch proceeds as today.
        /// </summary>
        /// <param name="installDir">The game's install directory (where the sidecar lives at its root).</param>
        /// <param name="gameName">Display name for log lines (optional).</param>
        public static GameOverrideSidecar? TryLoad(string? installDir, string? gameName = null)
        {
            if (string.IsNullOrWhiteSpace(installDir)) return null;
            try
            {
                var path = Path.Combine(installDir, FileName);
                if (!File.Exists(path)) return null;

                var sidecar = JsonSerializer.Deserialize<GameOverrideSidecar>(File.ReadAllText(path), ReadOptions);
                if (sidecar == null || !sidecar.HasAnything)
                    return null;

                Log.Info($"'{gameName ?? installDir}': per-game override sidecar loaded ({FileName})"
                         + (string.IsNullOrWhiteSpace(sidecar.LaunchExe) ? "" : $" launchExe='{sidecar.LaunchExe}'")
                         + (string.IsNullOrWhiteSpace(sidecar.LaunchArgs) ? "" : $" launchArgs='{sidecar.LaunchArgs}'")
                         + (string.IsNullOrWhiteSpace(sidecar.WorkingDir) ? "" : $" workingDir='{sidecar.WorkingDir}'"));
                return sidecar;
            }
            catch (Exception ex)
            {
                // Malformed sidecar must never break a launch — warn and behave exactly as without it.
                Log.Warn($"'{gameName ?? installDir}': {FileName} load failed (ignoring, launching as normal): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// [per-game override sidecar — gamevault-exec style] Resolve <see cref="LaunchExe"/> to a full path
        /// under <paramref name="installDir"/>. Accepts a relative path (resolved against the install dir) or a
        /// bare filename (matched anywhere underneath). Returns null if nothing matches, so the caller can fall
        /// back to its normal exe pick and warn. Never throws.
        /// </summary>
        public string? ResolveLaunchExe(string installDir)
        {
            if (string.IsNullOrWhiteSpace(LaunchExe) || string.IsNullOrWhiteSpace(installDir)) return null;
            try
            {
                var rel = LaunchExe.Replace('\\', Path.DirectorySeparatorChar)
                                   .Replace('/', Path.DirectorySeparatorChar)
                                   .Trim();

                // 1) Treat it as a path relative to the install dir (also covers an exe at the root).
                var direct = Path.GetFullPath(Path.Combine(installDir, rel));
                if (File.Exists(direct)) return direct;

                // 2) Treat it as a bare filename — match anywhere under the install dir (first match wins).
                var bare = Path.GetFileName(rel);
                if (!string.IsNullOrWhiteSpace(bare) && Directory.Exists(installDir))
                {
                    var hit = Directory.EnumerateFiles(installDir, bare, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
            catch { /* fall through to null → caller uses normal pick */ }
            return null;
        }

        /// <summary>
        /// [per-game override sidecar — gamevault-exec style] Resolve <see cref="WorkingDir"/> to a full
        /// directory path. Accepts a relative path under <paramref name="installDir"/> or an absolute path.
        /// Returns null if unset or the directory doesn't exist (caller keeps its default cwd). Never throws.
        /// </summary>
        public string? ResolveWorkingDir(string installDir)
        {
            if (string.IsNullOrWhiteSpace(WorkingDir)) return null;
            try
            {
                var wd = Path.IsPathRooted(WorkingDir)
                    ? WorkingDir
                    : Path.GetFullPath(Path.Combine(installDir, WorkingDir));
                return Directory.Exists(wd) ? wd : null;
            }
            catch { return null; }
        }
    }
}
