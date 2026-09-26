using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace HydraPlaynite
{
    /// <summary>
    /// Imports the Hydra launcher's library into Playnite.
    ///
    /// Hydra stores everything in a LevelDB at %APPDATA%\hydralauncher\hydra-db,
    /// opened with `classic-level` at valueEncoding "json" (hydra:
    /// src/main/level/level.ts). Games live in the "games" sublevel keyed
    /// `${shop}:${objectId}` (src/main/level/sublevels/keys.ts).
    ///
    /// There is no LevelDB reader for .NET worth vendoring, and the values are
    /// Snappy-compressed so scraping the .ldb files yields nothing. The read is
    /// therefore delegated to a Node helper that uses the same library Hydra does.
    ///
    /// LevelDB takes an EXCLUSIVE lock, so Hydra must be closed during a refresh.
    /// That is a real constraint on the user, not an implementation detail, so it
    /// is surfaced as a plain message rather than an error.
    /// </summary>
    public class HydraLibraryPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("21ed79dd-9d95-478f-9d3c-69ede738099b");
        public override string Name => "Hydra";
        public override LibraryClient Client { get; } = new HydraClient();

        public HydraSettingsViewModel SettingsViewModel { get; }

        // The "install Node" notice is worth exactly one showing per Playnite session.
        // A library refresh runs on every startup and on demand, and a red toast that
        // repeats itself for a known, unchanged condition trains people to dismiss it.
        private bool nodeMissingReported;

        public HydraLibraryPlugin(IPlayniteAPI api) : base(api)
        {
            SettingsViewModel = new HydraSettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true };
        }

        public override ISettings GetSettings(bool firstRunSettings) => SettingsViewModel;

        public override UserControl GetSettingsView(bool firstRunSettings) => new HydraSettingsView();

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var result = new List<GameMetadata>();

            // This plugin is a bridge to Hydra, not a replacement for it, so the first
            // question is whether Hydra is here at all. Checked before the helper and
            // before Node because it is the cheapest test and the likeliest cause, and
            // because "the library reader failed" is a useless thing to tell someone
            // who simply does not have Hydra installed.
            var database = HydraDatabasePath();
            if (!Directory.Exists(database))
            {
                logger.Info($"Hydra: no database at '{database}'; Hydra is not installed. Nothing imported.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "hydra-not-installed",
                    "Hydra: Hydra Launcher is not installed, so there is no library to import. " +
                    "This plugin reads an existing Hydra install; it does not replace it.",
                    NotificationType.Info));
                return result;
            }

            string script = FindHelperScript();
            if (script == null)
            {
                // Not an exception: a Playnite user without the helper deployed should
                // get a sentence telling them what is missing, not a red error toast.
                logger.Warn("Hydra: dump-library.js not found; nothing imported.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "hydra-no-helper",
                    "Hydra: the library reader (dump-library.js) was not found. Expected it " +
                    "beside the extension or at ~/.config/scripts/hydra/dump-library.js.",
                    NotificationType.Error));
                return result;
            }

            // Node, before the helper runs: this is the failure that hid behind
            // "the system cannot find the file specified" for a month. Resolve it in
            // the places people actually have it, and if none of them has it, say
            // once what to install and where the reader expects to run.
            string node = FindNode(SettingsViewModel?.Settings?.NodePath);
            if (node == null)
            {
                if (!nodeMissingReported)
                {
                    nodeMissingReported = true;
                    logger.Warn("Hydra: node.exe not found on PATH or in the usual install folders; nothing imported.");
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "hydra-no-node",
                        "Hydra: Node is not installed, so the library reader (" + script + ") cannot run. " +
                        "Install it with `scoop install nodejs` or from nodejs.org, or set the path to node.exe " +
                        "in the Hydra plugin settings, then refresh the library.",
                        NotificationType.Error));
                }
                return result;
            }

            HydraDump dump;
            try
            {
                dump = RunHelper(node, script);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Hydra: helper failed.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "hydra-helper-failed", "Hydra: could not read the library: " + ex.Message,
                    NotificationType.Error));
                return result;
            }

            if (!string.IsNullOrEmpty(dump.Error))
            {
                logger.Warn("Hydra: helper reported: " + dump.Error);
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "hydra-helper-error", "Hydra: " + dump.Error, NotificationType.Error));
                return result;
            }

            foreach (var g in dump.Games ?? new List<HydraGame>())
            {
                if (string.IsNullOrWhiteSpace(g.Title))
                {
                    continue;
                }

                // A Hydra entry is only INSTALLED if it carries an executablePath, and
                // most do not: Hydra keeps catalogue entries for games you have merely
                // looked at. Importing those as installed would put dead Play buttons
                // all over the library, so installed-ness is derived from the path
                // actually existing on disk.
                bool installed = !string.IsNullOrWhiteSpace(g.ExecutablePath)
                                 && File.Exists(g.ExecutablePath);

                var meta = new GameMetadata
                {
                    Name = g.Title,
                    GameId = g.Key ?? (g.Shop + ":" + g.ObjectId),
                    Source = new MetadataNameProperty("Hydra"),
                    IsInstalled = installed,
                    Playtime = (ulong)Math.Max(0, g.PlayTimeInMilliseconds / 1000),
                };

                if (installed)
                {
                    meta.InstallDirectory = Path.GetDirectoryName(g.ExecutablePath);
                    meta.GameActions = new List<GameAction>
                    {
                        new GameAction
                        {
                            Type = GameActionType.File,
                            Path = g.ExecutablePath,
                            WorkingDir = Path.GetDirectoryName(g.ExecutablePath),
                            IsPlayAction = true,
                            Name = "Play",
                        }
                    };
                }

                if (g.InstalledSizeInBytes.HasValue && g.InstalledSizeInBytes.Value > 0)
                {
                    meta.InstallSize = (ulong)g.InstalledSizeInBytes.Value;
                }

                if (g.LastTimePlayed.HasValue)
                {
                    meta.LastActivity = g.LastTimePlayed;
                }

                if (g.Favorite)
                {
                    meta.Favorite = true;
                }

                // Hydra's art is remote URLs; Playnite downloads and caches these itself.
                if (!string.IsNullOrWhiteSpace(g.IconUrl))
                {
                    meta.Icon = new MetadataFile(g.IconUrl);
                }

                if (!string.IsNullOrWhiteSpace(g.CoverUrl))
                {
                    meta.CoverImage = new MetadataFile(g.CoverUrl);
                }

                // The shop a game came from is worth keeping: a Hydra "steam" entry and
                // a real Steam-library entry are the same game, and the tag makes the
                // overlap visible instead of looking like a duplicate import.
                if (!string.IsNullOrWhiteSpace(g.Shop))
                {
                    meta.Tags = new HashSet<MetadataProperty> { new MetadataNameProperty("hydra:" + g.Shop) };
                }

                result.Add(meta);
            }

            int playable = result.Count(r => r.IsInstalled);
            logger.Info($"Hydra: imported {result.Count} games, {playable} installed.");

            if (result.Count > 0 && playable == 0)
            {
                // Worth saying out loud: an import that adds entries none of which can be
                // launched looks broken unless you know Hydra keeps catalogue entries.
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "hydra-none-installed",
                    $"Hydra: imported {result.Count} games, none installed. These are " +
                    "catalogue entries. Install one in Hydra and refresh to get a Play action.",
                    NotificationType.Info));
            }

            return result;
        }

        /// <summary>
        /// Look beside the extension first so the plugin can ship self-contained, then
        /// fall back to the dotfiles copy. Two locations, no settings screen.
        /// </summary>
        /// <summary>
        /// Hydra's LevelDB. Its presence is what "Hydra is installed" means here: the
        /// exe can be uninstalled while the database survives, and the database is the
        /// only thing this plugin actually needs.
        /// </summary>
        private static string HydraDatabasePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "hydralauncher", "hydra-db");
        }

        private string FindHelperScript()
        {
            var candidates = new[]
            {
                Path.Combine(GetPluginUserDataPath(), "dump-library.js"),
                Path.Combine(Path.GetDirectoryName(typeof(HydraLibraryPlugin).Assembly.Location) ?? ".", "dump-library.js"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             ".config", "scripts", "hydra", "dump-library.js"),
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// Node, in the order people have it: the configured path, PATH, the
        /// nodejs.org installer's two folders, then scoop (the shim first, then the
        /// versioned app folder in case shims are off PATH). Returns null when none of
        /// them has a node.exe, and the caller decides what to say about that.
        /// </summary>
        internal static string FindNode(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return File.Exists(configured) ? configured : null;
            }

            var candidates = new List<string>();
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(';').Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                try { candidates.Add(Path.Combine(dir.Trim(), "node.exe")); } catch { }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(programFiles, "nodejs", "node.exe"));
            candidates.Add(Path.Combine(local, "Programs", "nodejs", "node.exe"));
            candidates.Add(Path.Combine(profile, "scoop", "shims", "node.exe"));
            candidates.Add(Path.Combine(profile, "scoop", "apps", "nodejs", "current", "node.exe"));
            candidates.Add(Path.Combine(profile, "scoop", "apps", "nodejs-lts", "current", "node.exe"));

            return candidates.FirstOrDefault(File.Exists);
        }

        private HydraDump RunHelper(string nodeExe, string scriptPath)
        {
            if (IsHydraRunning())
            {
                throw new Exception("Hydra is running. Close it and refresh again " +
                                    "(its database takes an exclusive lock).");
            }

            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = "\"" + scriptPath + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // The helper resolves `classic-level` relative to its own directory, so it
                // has to run from there or the require() fails.
                WorkingDirectory = Path.GetDirectoryName(scriptPath),
            };

            Process started;
            try
            {
                started = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // FindNode already proved the file exists, so this is a permissions or
                // policy failure on it, not a missing install. Name the path so the
                // message is about the right thing.
                throw new Exception("could not start " + nodeExe + ": " + ex.Message);
            }

            using (var proc = started)
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(60000))
                {
                    try { proc.Kill(); } catch { }
                    throw new Exception("the library reader timed out after 60s.");
                }

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    throw new Exception(string.IsNullOrWhiteSpace(stderr)
                        ? "the library reader returned nothing (is `classic-level` installed beside " + scriptPath + "?)"
                        : stderr.Trim());
                }

                // The helper always emits parseable JSON, {"games":[...]} or {"error":"..."},
                // so a nonzero exit still carries a usable message rather than a stack trace.
                return JsonConvert.DeserializeObject<HydraDump>(stdout);
            }
        }

        private static bool IsHydraRunning()
        {
            try
            {
                return Process.GetProcessesByName("Hydra").Length > 0
                       || Process.GetProcessesByName("hydralauncher").Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public class HydraClient : LibraryClient
    {
        public override bool IsInstalled => !string.IsNullOrEmpty(ClientExecutablePath);

        public override string Icon => null;

        private static string ClientExecutablePath
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var exe = Path.Combine(local, "Programs", "Hydra", "Hydra.exe");
                return File.Exists(exe) ? exe : null;
            }
        }

        public override void Open()
        {
            var exe = ClientExecutablePath;
            if (exe != null)
            {
                Process.Start(exe);
            }
        }
    }

    // Mirrors dump-library.js output, which mirrors hydra's src/types/level.types.ts.
    public class HydraDump
    {
        [JsonProperty("games")] public List<HydraGame> Games { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }

    public class HydraGame
    {
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("objectId")] public string ObjectId { get; set; }
        [JsonProperty("shop")] public string Shop { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("executablePath")] public string ExecutablePath { get; set; }
        [JsonProperty("iconUrl")] public string IconUrl { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }
        [JsonProperty("playTimeInMilliseconds")] public long PlayTimeInMilliseconds { get; set; }
        [JsonProperty("lastTimePlayed")] public DateTime? LastTimePlayed { get; set; }
        [JsonProperty("installedSizeInBytes")] public long? InstalledSizeInBytes { get; set; }
        [JsonProperty("favorite")] public bool Favorite { get; set; }
    }
}
