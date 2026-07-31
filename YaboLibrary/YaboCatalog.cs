using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Playnite.SDK;

namespace YaboLibrary
{
    /// <summary>
    /// One inner launchable game of a (possibly multi-game) port, mirroring the
    /// <c>games[]</c> array from <c>--list-json</c>. Multi-game ports (length &gt; 1)
    /// become a Playnite Play-button dropdown — one <see cref="Playnite.SDK.Models.GameAction"/> per entry.
    /// </summary>
    public class YaboGame
    {
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("rom")] public string Rom { get; set; }
        [JsonProperty("requires")] public string Requires { get; set; }
    }

    /// <summary>
    /// One declared data file (ROM/asset) the port needs. Mirrors <c>dataFiles[]</c>.
    /// Carried for diagnostics / future per-game data-state UI; not load-bearing for P0 import.
    /// </summary>
    public class YaboDataFile
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("optional")] public bool Optional { get; set; }
        [JsonProperty("sha1")] public string Sha1 { get; set; }
    }

    /// <summary>
    /// One catalog entry as emitted by <c>yabo-launcher.exe --list-json</c>.
    /// Property names map to the documented JSON surface:
    /// <c>{name, folderName, repository, category, status, cover, externalUrl, dataState, dataFiles[], games[]}</c>.
    /// </summary>
    public class YaboCatalogEntry
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("folderName")] public string FolderName { get; set; }
        [JsonProperty("repository")] public string Repository { get; set; }
        [JsonProperty("category")] public string Category { get; set; }

        /// <summary>Engine-reported status string (e.g. "installed", "notInstalled", "updateAvailable").</summary>
        [JsonProperty("status")] public string Status { get; set; }

        /// <summary>Resolved cover-art URL or local path — written straight onto the GameMetadata so our box wins.</summary>
        [JsonProperty("cover")] public string Cover { get; set; }

        /// <summary>Link-out URL for external (non-managed) entries; when set the tile is a URL action, not installed.</summary>
        [JsonProperty("externalUrl")] public string ExternalUrl { get; set; }

        /// <summary>Per-port data readiness string (e.g. "ready"/"armable"/"missing"); informational here.</summary>
        [JsonProperty("dataState")] public string DataState { get; set; }

        /// <summary>Engine-reported hidden/delisted flag (sports, delisted IA packs). When true the importer skips
        /// the entry unless the ImportHidden setting is on — so toggling something out of the launcher also keeps it
        /// out of Playnite.</summary>
        [JsonProperty("hidden")] public bool Hidden { get; set; }

        [JsonProperty("dataFiles")] public List<YaboDataFile> DataFiles { get; set; } = new List<YaboDataFile>();
        [JsonProperty("games")] public List<YaboGame> Games { get; set; } = new List<YaboGame>();

        // [conversion schema] Provenance the engine already emits — exported to Playnite as Links/Tags so the
        // library entry shows WHERE a game comes from (the HOW — dataFiles/route — stays internal).
        [JsonProperty("iaIdentifier")] public string IaIdentifier { get; set; }
        [JsonProperty("contentUrl")] public string ContentUrl { get; set; }
        [JsonProperty("website")] public string Website { get; set; }
        [JsonProperty("rohanLibrary")] public bool RohanLibrary { get; set; }
        [JsonProperty("experimental")] public bool Experimental { get; set; }

        /// <summary>True when the engine reports the port as installed (any non-"notInstalled" installed-ish state).</summary>
        [JsonIgnore]
        public bool IsInstalled =>
            !string.IsNullOrWhiteSpace(Status) &&
            !Status.Equals("notInstalled", StringComparison.OrdinalIgnoreCase) &&
            !Status.Equals("not-installed", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsExternal => !string.IsNullOrWhiteSpace(ExternalUrl);

        /// <summary>
        /// True when the engine reports a GitHub-release update is waiting for this installed port
        /// (status == "UpdateAvailable", matching <c>GameStatus.UpdateAvailable.ToString()</c> as emitted
        /// by <c>--list-json</c>). Surfaced into Playnite as an "Update available" Tag + a one-shot
        /// "N game(s) have updates" notification.
        /// </summary>
        [JsonIgnore]
        public bool HasUpdate =>
            !string.IsNullOrWhiteSpace(Status) &&
            Status.Equals("UpdateAvailable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Thin wrapper around the engine CLI (<c>yabo-launcher.exe</c>). Shells out with
    /// redirected stdout, no window, and honours a cancellation token. The CLI is the
    /// single source of truth — this plugin never touches the catalog/filesystem directly.
    /// </summary>
    public static class YaboCli
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        /// <summary>
        /// Run <c>--list-json</c> and deserialize the catalog. Returns an empty list on any
        /// failure (missing exe, non-zero exit, bad JSON, cancellation) — import then yields nothing
        /// rather than throwing into Playnite's import loop.
        /// </summary>
        public static List<YaboCatalogEntry> GetCatalog(string exePath, CancellationToken cancelToken)
        {
            var json = Run(exePath, "--list-json", cancelToken, out var exit);
            if (exit != 0 || string.IsNullOrWhiteSpace(json))
            {
                Logger.Warn($"Yabo: --list-json failed (exit {exit}) or returned empty output.");
                return new List<YaboCatalogEntry>();
            }

            try
            {
                // --list-json now emits an envelope { build, rid, games:[...] } so the catalog can carry the
                // build stamp. Tolerate BOTH that and the legacy bare [...] array (older engines / robustness).
                var token = Newtonsoft.Json.Linq.JToken.Parse(json);
                var arr = token.Type == Newtonsoft.Json.Linq.JTokenType.Array
                    ? token
                    : token["games"];
                return arr?.ToObject<List<YaboCatalogEntry>>()
                       ?? new List<YaboCatalogEntry>();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Yabo: failed to parse --list-json output.");
                return new List<YaboCatalogEntry>();
            }
        }

        /// <summary>
        /// Pull the owner's released feed into the local catalog (CANON refresh) BEFORE listing, so a
        /// subscriber's library reflects the latest "release" tag automatically on every Playnite refresh.
        /// Dormant + harmless when no RemoteFeedUrl is configured (the engine no-ops and reports dormant).
        /// All failures are swallowed — offline simply shows the last-synced catalog rather than breaking import.
        /// </summary>
        public static void SyncFeed(string exePath, CancellationToken cancelToken)
        {
            try
            {
                Run(exePath, "--sync-feed", cancelToken, out var exit);
                if (exit != 0)
                {
                    Logger.Info($"Yabo: --sync-feed exit {exit} (no feed configured or offline; showing local catalog).");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: --sync-feed failed; showing the local catalog.");
            }
        }

        /// <summary>
        /// Run the CLI with the given argument string, capturing stdout. Returns the captured
        /// stdout; <paramref name="exitCode"/> is the process exit code (-1 if it never started).
        /// Cancellation kills the process and returns what was captured.
        /// </summary>
        public static string Run(string exePath, string arguments, CancellationToken cancelToken, out int exitCode)
        {
            exitCode = -1;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                Logger.Warn($"Yabo: engine exe not found at '{exePath}'. Set it in the plugin settings.");
                return string.Empty;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            try
            {
                using (var proc = new Process { StartInfo = psi })
                {
                    var stdout = new StringBuilder();
                    proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Logger.Debug($"yabo stderr: {e.Data}"); };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    // Cooperative wait so we can react to Playnite's cancel token without WaitForExitAsync (net462).
                    while (!proc.WaitForExit(100))
                    {
                        if (cancelToken.IsCancellationRequested)
                        {
                            try { proc.Kill(); } catch { /* already exiting */ }
                            return stdout.ToString();
                        }
                    }

                    // Ensure async buffers are flushed.
                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                    return stdout.ToString();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Yabo: failed to run '{exePath} {arguments}'.");
                return string.Empty;
            }
        }
    }
}
