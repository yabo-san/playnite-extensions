using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace QuiverPlaynite
{
    /// <summary>
    /// Imports Quiver Launcher's library into Playnite.
    ///
    /// Quiver (tgeorgiadis/quiver-launcher) is plain files on disk, which is a better
    /// integration surface than parsing its console output:
    ///
    ///     &lt;root&gt;\apps.json                   the user's app list (Services/AppCatalogService.cs)
    ///     &lt;root&gt;\Apps\&lt;folderName&gt;\          the install, unless apps.json sets installPath
    ///         version.txt                    written on a finished install (Services/GameStatusService.cs:35)
    ///         install-incomplete.txt         present while a download is unfinished
    ///         selected_executable.txt        the exe the user picked when there were several (Models/GameInfo.cs:1300)
    ///
    /// The root is the Velopack app dir on Windows (Services/QuiverLauncherPaths.cs),
    /// %LOCALAPPDATA%\QuiverLauncher for a normal install; unpackaged builds keep the
    /// files beside the exe. Quiver's CLI (Services/CLIHandler.cs) is used only for
    /// what Quiver owns: --download, --uninstall, and --run when the user wants its
    /// auto-update before launch. Names passed to the CLI match Name or FolderName
    /// case-insensitively (CLIHandler.FindGame).
    /// </summary>
    public class QuiverLibraryPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("9d2f4e7a-3b61-4c58-8e0f-6a1d5c7b2e93");
        public override string Name => "Quiver";

        // Shown in Playnite's library filter and on each game's source.
        public override string LibraryIcon => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(QuiverLibraryPlugin).Assembly.Location), "icon.png");
        public override LibraryClient Client { get; } = new QuiverClient();

        public QuiverSettingsViewModel SettingsViewModel { get; }

        public QuiverLibraryPlugin(IPlayniteAPI api) : base(api)
        {
            SettingsViewModel = new QuiverSettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true };
        }

        public override ISettings GetSettings(bool firstRunSettings) => SettingsViewModel;

        public override UserControl GetSettingsView(bool firstRunSettings) => new QuiverSettingsView();

        // ---- library ------------------------------------------------------------

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var result = new List<GameMetadata>();

            var root = QuiverPaths.ResolveRoot(SettingsViewModel?.Settings?.QuiverRoot);
            if (root == null)
            {
                // No apps.json anywhere we know to look: Quiver is not installed, or it
                // is a portable build whose folder we were not told about. Say which
                // folders were tried, so the fix is obvious.
                logger.Info("Quiver: no apps.json found; nothing imported.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "quiver-not-installed",
                    "Quiver: no Quiver library found. Install Quiver Launcher, or set its data " +
                    "folder in the plugin settings for a portable build. Looked in: " +
                    string.Join("; ", QuiverPaths.CandidateRoots()),
                    NotificationType.Info));
                return result;
            }

            List<QuiverApp> apps;
            try
            {
                apps = QuiverPaths.ReadApps(root);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Quiver: could not read apps.json.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "quiver-apps-json", "Quiver: could not read " + Path.Combine(root, "apps.json") + ": " + ex.Message,
                    NotificationType.Error));
                return result;
            }

            var appsFolder = QuiverPaths.AppsFolder(root);
            var quiverExe = QuiverPaths.FindExe(root);
            bool throughQuiver = SettingsViewModel?.Settings?.LaunchThroughQuiver ?? false;

            foreach (var app in apps)
            {
                if (string.IsNullOrWhiteSpace(app.Name))
                {
                    continue;
                }

                var folder = app.InstallPath(appsFolder);
                bool installed = QuiverPaths.IsInstalled(folder);
                var exe = installed ? QuiverPaths.SelectedExecutable(folder) : null;

                var meta = new GameMetadata
                {
                    Name = app.DisplayName,
                    // apps.json has no id field; folderName is required to be unique
                    // there (Quiver's README) and survives a rename of the display name.
                    GameId = app.FolderName ?? app.Name,
                    Source = new MetadataNameProperty("Quiver"),
                    IsInstalled = installed,
                };

                if (installed)
                {
                    meta.InstallDirectory = folder;
                    var version = QuiverPaths.InstalledVersion(folder);
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        meta.Version = version;
                    }

                    var action = BuildPlayAction(app, folder, exe, quiverExe, throughQuiver);
                    if (action != null)
                    {
                        meta.GameActions = new List<GameAction> { action };
                    }
                }

                if (!string.IsNullOrWhiteSpace(app.AppIconUrl))
                {
                    meta.Icon = new MetadataFile(app.AppIconUrl);
                }

                var tags = new HashSet<MetadataProperty>();
                foreach (var t in app.Tags ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(t)) tags.Add(new MetadataNameProperty("quiver:" + t));
                }
                if (tags.Count > 0) meta.Tags = tags;

                if (!string.IsNullOrWhiteSpace(app.Repository))
                {
                    var host = string.Equals(app.RepositorySource, "gitlab", StringComparison.OrdinalIgnoreCase)
                        ? "https://gitlab.com/" : "https://github.com/";
                    meta.Links = new List<Link> { new Link("Repository", host + app.Repository) };
                }

                result.Add(meta);
            }

            int playable = result.Count(r => r.IsInstalled);
            logger.Info($"Quiver: imported {result.Count} apps from {root}, {playable} installed.");
            return result;
        }

        /// <summary>
        /// Direct launch when we know the exe; through Quiver when the user asked for
        /// its auto-update, or when Quiver has not decided which exe to run yet (more
        /// than one candidate and no selected_executable.txt: Quiver opens its own
        /// picker for that case, CLIHandler.RunGame).
        /// </summary>
        private static GameAction BuildPlayAction(QuiverApp app, string folder, string exe, string quiverExe, bool throughQuiver)
        {
            if (!throughQuiver && exe != null)
            {
                return new GameAction
                {
                    Type = GameActionType.File,
                    Path = exe,
                    WorkingDir = Path.GetDirectoryName(exe),
                    IsPlayAction = true,
                    Name = "Play",
                };
            }

            if (quiverExe != null)
            {
                return new GameAction
                {
                    Type = GameActionType.File,
                    Path = quiverExe,
                    Arguments = "--run \"" + app.Name + "\"",
                    WorkingDir = Path.GetDirectoryName(quiverExe),
                    IsPlayAction = true,
                    Name = "Play (via Quiver)",
                };
            }

            return null;
        }

        // ---- install / uninstall -------------------------------------------------

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new QuiverInstallController(args.Game, this);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new QuiverUninstallController(args.Game, this);
        }

        /// <summary>The app row behind a Playnite game, matched on folderName (the GameId).</summary>
        internal QuiverApp FindApp(string gameId, out string root)
        {
            root = QuiverPaths.ResolveRoot(SettingsViewModel?.Settings?.QuiverRoot);
            if (root == null) return null;
            try
            {
                return QuiverPaths.ReadApps(root).FirstOrDefault(a =>
                    string.Equals(a.FolderName, gameId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Name, gameId, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        internal string ResolveExe() => QuiverPaths.FindExe(QuiverPaths.ResolveRoot(SettingsViewModel?.Settings?.QuiverRoot));
    }

    /// <summary>
    /// Runs <c>--download "&lt;name&gt;"</c> and reports the folder Quiver produced.
    /// Quiver does the release lookup, the download and the extraction; we only
    /// wait for it and read the result off disk.
    /// </summary>
    public class QuiverInstallController : InstallController
    {
        private readonly QuiverLibraryPlugin plugin;
        private CancellationTokenSource cts;

        public QuiverInstallController(Game game, QuiverLibraryPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Install via Quiver";
        }

        public override void Install(InstallActionArgs args)
        {
            var exe = plugin.ResolveExe();
            var app = plugin.FindApp(Game.GameId, out var root);
            if (exe == null || app == null)
            {
                plugin.PlayniteApi.Notifications.Add("quiver-install-noexe",
                    "Quiver: cannot install; Quiver Launcher's exe or its app list was not found.",
                    NotificationType.Error);
                return;
            }

            cts = new CancellationTokenSource();
            Task.Run(() =>
            {
                QuiverCli.Run(exe, "--download \"" + app.Name + "\"", cts.Token, out var exit);

                var folder = app.InstallPath(QuiverPaths.AppsFolder(root));
                if (!QuiverPaths.IsInstalled(folder))
                {
                    plugin.PlayniteApi.Notifications.Add("quiver-install-failed-" + Game.GameId,
                        $"Quiver: '{app.Name}' did not finish installing (exit {exit}). Open Quiver to see why; " +
                        "a release with more than one asset, or a rate-limited GitHub, are the usual reasons.",
                        NotificationType.Error);
                    return;
                }

                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData
                {
                    InstallDirectory = folder
                }));
            });
        }

        public override void Dispose()
        {
            cts?.Cancel();
            base.Dispose();
        }
    }

    /// <summary>Runs <c>--uninstall "&lt;name&gt;"</c> and reports back when the folder is gone.</summary>
    public class QuiverUninstallController : UninstallController
    {
        private readonly QuiverLibraryPlugin plugin;
        private CancellationTokenSource cts;

        public QuiverUninstallController(Game game, QuiverLibraryPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Uninstall via Quiver";
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var exe = plugin.ResolveExe();
            var app = plugin.FindApp(Game.GameId, out var root);
            if (exe == null || app == null)
            {
                plugin.PlayniteApi.Notifications.Add("quiver-uninstall-noexe",
                    "Quiver: cannot uninstall; Quiver Launcher's exe or its app list was not found.",
                    NotificationType.Error);
                return;
            }

            cts = new CancellationTokenSource();
            Task.Run(() =>
            {
                QuiverCli.Run(exe, "--uninstall \"" + app.Name + "\"", cts.Token, out var exit);
                var folder = app.InstallPath(QuiverPaths.AppsFolder(root));
                if (QuiverPaths.IsInstalled(folder))
                {
                    plugin.PlayniteApi.Notifications.Add("quiver-uninstall-failed-" + Game.GameId,
                        $"Quiver: '{app.Name}' is still installed after --uninstall (exit {exit}).",
                        NotificationType.Error);
                    return;
                }
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            });
        }

        public override void Dispose()
        {
            cts?.Cancel();
            base.Dispose();
        }
    }

    /// <summary>Playnite's notion of "the client": whether Quiver is installed and how to open it.</summary>
    public class QuiverClient : LibraryClient
    {
        public override bool IsInstalled => QuiverPaths.FindExe(QuiverPaths.ResolveRoot(null)) != null;
        public override string Icon => null;

        public override void Open()
        {
            var exe = QuiverPaths.FindExe(QuiverPaths.ResolveRoot(null));
            if (exe != null)
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
            }
        }
    }

    /// <summary>Where Quiver keeps things, read off Services/QuiverLauncherPaths.cs.</summary>
    internal static class QuiverPaths
    {
        public static IEnumerable<string> CandidateRoots()
        {
            // Where the owner's chezmoi dotfiles unpack it (.chezmoiexternals/launchers.toml.tmpl),
            // next to Hydra. Checked first because that is how this machine installs launchers.
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                      ".local", "share", "launchers", "quiver");
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "QuiverLauncher");
            yield return Path.Combine(local, "Programs", "QuiverLauncher");
            // An unpackaged build keeps apps.json beside its exe; the only such
            // place we can guess is next to an exe on PATH.
            foreach (var exe in ExesOnPath())
            {
                yield return Path.GetDirectoryName(exe);
            }
        }

        /// <summary>The first candidate that actually holds an apps.json, or the override if it does.</summary>
        public static string ResolveRoot(string overrideRoot)
        {
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return File.Exists(Path.Combine(overrideRoot, "apps.json")) ? overrideRoot : null;
            }
            return CandidateRoots().FirstOrDefault(r => r != null && File.Exists(Path.Combine(r, "apps.json")));
        }

        public static string AppsFolder(string root)
        {
            // settings.json may move the apps folder; Quiver stores it under a
            // handful of key spellings across versions, so read the obvious ones and
            // fall back to the default DefaultAppsDirectory = <root>\Apps.
            try
            {
                var settingsPath = Path.Combine(root, "settings.json");
                if (File.Exists(settingsPath))
                {
                    var json = JObject.Parse(File.ReadAllText(settingsPath));
                    foreach (var key in new[] { "appsFolder", "AppsFolder", "gamesFolder", "GamesFolder" })
                    {
                        var v = (string)json[key];
                        if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v)) return v;
                    }
                }
            }
            catch
            {
                // A broken settings.json is Quiver's problem; the default still works.
            }
            return Path.Combine(root, "Apps");
        }

        public static List<QuiverApp> ReadApps(string root)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(root, "apps.json")));
            var array = json["apps"] as JArray ?? new JArray();
            return array.Select(a => a.ToObject<QuiverApp>()).Where(a => a != null).ToList();
        }

        public static bool IsInstalled(string folder)
        {
            return !string.IsNullOrEmpty(folder)
                   && File.Exists(Path.Combine(folder, "version.txt"))
                   && !File.Exists(Path.Combine(folder, "install-incomplete.txt"));
        }

        public static string InstalledVersion(string folder)
        {
            try { return File.ReadAllText(Path.Combine(folder, "version.txt")).Trim(); }
            catch { return null; }
        }

        /// <summary>
        /// The exe Quiver would run: selected_executable.txt if the user picked one,
        /// else the only .exe at the folder root. Several exes and no pick returns
        /// null, and Play goes through Quiver so its picker can ask.
        /// </summary>
        public static string SelectedExecutable(string folder)
        {
            try
            {
                var selected = Path.Combine(folder, "selected_executable.txt");
                if (File.Exists(selected))
                {
                    var saved = File.ReadAllText(selected).Trim();
                    if (saved.Length > 0)
                    {
                        var full = Path.IsPathRooted(saved) ? saved : Path.Combine(folder, saved);
                        if (File.Exists(full)) return full;
                    }
                }

                var exes = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(e => !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return exes.Count == 1 ? exes[0] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Velopack installs put the running app under &lt;root&gt;\current\; the Desktop
        /// project's AssemblyName is QuiverLauncher.Desktop, the older App project's is
        /// QuiverLauncher. Then PATH.
        /// </summary>
        public static string FindExe(string root)
        {
            var names = new[] { "QuiverLauncher.Desktop.exe", "QuiverLauncher.exe" };
            var dirs = new List<string>();
            if (root != null)
            {
                dirs.Add(Path.Combine(root, "current"));
                dirs.Add(root);
            }
            foreach (var d in dirs)
            {
                foreach (var n in names)
                {
                    var p = Path.Combine(d, n);
                    if (File.Exists(p)) return p;
                }
            }
            return ExesOnPath().FirstOrDefault();
        }

        private static IEnumerable<string> ExesOnPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(';').Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                foreach (var n in new[] { "QuiverLauncher.Desktop.exe", "QuiverLauncher.exe" })
                {
                    string p = null;
                    try { p = Path.Combine(dir.Trim(), n); } catch { }
                    if (p != null && File.Exists(p)) yield return p;
                }
            }
        }
    }

    internal static class QuiverCli
    {
        public static string Run(string exe, string arguments, CancellationToken token, out int exit)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                // A download can legitimately take a long time; only a cancel ends it early.
                while (!p.WaitForExit(500))
                {
                    if (token.IsCancellationRequested)
                    {
                        try { p.Kill(); } catch { }
                        break;
                    }
                }
                exit = p.HasExited ? p.ExitCode : -1;
                return (stdout.Result ?? string.Empty) + (stderr.Result ?? string.Empty);
            }
        }
    }

    /// <summary>One row of apps.json, the fields Quiver's SerializeApp writes.</summary>
    public class QuiverApp
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("project")] public string Project { get; set; }
        [JsonProperty("customDisplayName")] public string CustomDisplayName { get; set; }
        [JsonProperty("repository")] public string Repository { get; set; }
        [JsonProperty("repositorySource")] public string RepositorySource { get; set; }
        [JsonProperty("folderName")] public string FolderName { get; set; }
        [JsonProperty("installPath")] public string InstallPathOverride { get; set; }
        [JsonProperty("appIconUrl")] public string AppIconUrl { get; set; }
        [JsonProperty("tags")] public List<string> Tags { get; set; }

        /// <summary>Quiver shows "Name (Project)" by default; a custom display name wins.</summary>
        public string DisplayName =>
            !string.IsNullOrWhiteSpace(CustomDisplayName) ? CustomDisplayName
            : !string.IsNullOrWhiteSpace(Project) ? Name + " (" + Project + ")"
            : Name;

        /// <summary>Mirrors GameInfo.GetInstallPath: installPath override, else Apps\folderName.</summary>
        public string InstallPath(string appsFolder)
        {
            if (!string.IsNullOrWhiteSpace(InstallPathOverride)) return InstallPathOverride;
            return string.IsNullOrWhiteSpace(FolderName) ? null : Path.Combine(appsFolder, FolderName);
        }
    }
}
