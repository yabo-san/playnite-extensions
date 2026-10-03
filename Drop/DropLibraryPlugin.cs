using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Yabo.Shared;

namespace DropPlaynite
{
    /// <summary>
    /// Your Drop library as Playnite cards.
    ///
    /// Same rule as the Hydra and RohanKar plugins: the client that already solves
    /// the hard part (here Drop's server: storage, chunking, encryption, accounts)
    /// stays in charge, and this plugin puts a card in front of it. It speaks Drop's
    /// client protocol directly rather than driving the desktop app, because the
    /// desktop app has no CLI and its database is a Rust <c>pot</c> blob.
    ///
    /// Endpoints and formats are read off <c>Drop-OSS/drop</c> and
    /// <c>Drop-OSS/drop-app</c>; the file-level comments cite where.
    /// </summary>
    public class DropLibraryPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("5b8e1c2a-7d43-4f06-9a1e-3c2b6d8f0e47");
        public override string Name => "Drop";

        // Shown in Playnite's library filter and on each game's source.
        public override string LibraryIcon => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(DropLibraryPlugin).Assembly.Location), "icon.png");
        public override LibraryClient Client { get; } = new DropDesktopClient();

        public DropSettingsViewModel SettingsViewModel { get; }
        public DropSettings Settings => SettingsViewModel.Settings;

        public DropLibraryPlugin(IPlayniteAPI api) : base(api)
        {
            SettingsViewModel = new DropSettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true };
        }

        public override ISettings GetSettings(bool firstRunSettings) => SettingsViewModel;

        public override UserControl GetSettingsView(bool firstRunView) => new DropSettingsView();

        public void SaveSettings() => SettingsViewModel.Save();

        // ---- wiring -------------------------------------------------------------

        public DropClient Api()
        {
            var baseUrl = Settings.BaseUrl?.Trim();
            return new DropClient(
                baseUrl,
                (path, auth) => WithSchemeFallback(auth, a => DropHttp.Get(baseUrl, path, a)),
                (path, auth, body) => WithSchemeFallback(auth, a => DropHttp.Post(baseUrl, path, a, body)));
        }

        /// <summary>
        /// Client calls only: on a 403, retry once with the other signing scheme
        /// (Nonce for Drop 0.3.x, JWT for newer servers) and keep whichever worked.
        /// </summary>
        private string WithSchemeFallback(string auth, Func<string, string> call)
        {
            try { return call(auth); }
            catch (System.Net.WebException ex) when (
                (ex.Response as System.Net.HttpWebResponse)?.StatusCode == System.Net.HttpStatusCode.Forbidden
                && auth != null && (auth.StartsWith("Nonce ") || auth.StartsWith("JWT ")))
            {
                var previous = Settings.AuthScheme;
                Settings.AuthScheme = auth.StartsWith("JWT ") ? "Nonce" : "JWT";
                try
                {
                    var result = call(DropAuth.ClientHeader(Settings));
                    SavePluginSettings(Settings);
                    logger.Info("Drop: server accepts " + Settings.AuthScheme + " client auth; remembered.");
                    return result;
                }
                catch
                {
                    Settings.AuthScheme = previous;
                    throw;
                }
            }
        }

        /// <summary>A fresh client header, or null when not signed in. Ten-second JWTs, so never cache it.</summary>
        public string ClientAuth() => DropAuth.ClientHeader(Settings);

        public string InstallDirFor(string gameName) => Path.Combine(Settings.EffectiveInstallRoot, Sanitise(gameName));

        public static string Sanitise(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var s = new string((name ?? "game").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(s) ? "game" : s;
        }

        // ---- library ------------------------------------------------------------

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var result = new List<GameMetadata>();

            if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
            {
                logger.Info("Drop: no instance URL configured; nothing imported.");
                return result;
            }
            var auth = ClientAuth();
            if (auth == null)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage("drop-not-signed-in",
                    "Drop: not signed in. Open the plugin settings and sign in with a code.", NotificationType.Info));
                return result;
            }

            IReadOnlyList<DropGame> games;
            try
            {
                games = Api().Library(auth);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Drop: library request failed.");
                PlayniteApi.Notifications.Add(new NotificationMessage("drop-library-failed",
                    "Drop: could not read the library: " + DropHttp.Describe(ex), NotificationType.Error));
                return result;
            }

            var hide = Settings.HideUploaded
                ? new HashSet<string>(Settings.UploadedGames ?? new List<string>(), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var client = Api();

            foreach (var g in games)
            {
                if (string.IsNullOrWhiteSpace(g.Name)) continue;
                // Owner's rule: a game sent from this Playnite is already a card here;
                // its Drop twin must not become a second one.
                if (hide.Contains(g.Id) || hide.Contains(g.Name)) continue;

                var installDir = InstallDirFor(g.Name);
                var marker = DropInstallMarker.Read(installDir);

                var meta = new GameMetadata
                {
                    Name = g.Name,
                    GameId = g.Id,
                    Source = new MetadataNameProperty("Drop"),
                    Description = g.Description,
                    IsInstalled = marker != null,
                };
                if (marker != null)
                {
                    meta.InstallDirectory = installDir;
                    try
                    {
                        meta.GameActions = BuildPlayActions(g.Id, marker.VersionId, installDir);
                    }
                    catch (Exception ex)
                    {
                        // The card still imports as installed; Play just needs a refresh.
                        logger.Warn($"Drop: could not read launches for {g.Name}: {DropHttp.Describe(ex)}");
                    }
                }
                var cover = client.ObjectUrl(g.CoverObjectId);
                var icon = client.ObjectUrl(g.IconObjectId);
                var banner = client.ObjectUrl(g.BannerObjectId);
                if (cover != null) meta.CoverImage = new MetadataFile(cover);
                if (icon != null) meta.Icon = new MetadataFile(icon);
                if (banner != null) meta.BackgroundImage = new MetadataFile(banner);
                if (DateTime.TryParse(g.ReleaseDate, out var released)) meta.ReleaseDate = new ReleaseDate(released);

                result.Add(meta);
            }

            logger.Info($"Drop: imported {result.Count} games ({games.Count - result.Count} hidden or nameless).");
            return result;
        }

        /// <summary>
        /// One GameAction per Windows launch in the version's table. If the command
        /// names a file inside the install folder, run it directly; otherwise hand it
        /// to <c>cmd /C</c> from the install folder, which is what the desktop client
        /// does on Windows (<c>process/src/process_handlers.rs:43</c>).
        /// </summary>
        public List<GameAction> BuildPlayActions(string gameId, string versionId, string installDir)
        {
            var actions = new List<GameAction>();
            var detail = Api().VersionDetail(ClientAuth(), gameId, versionId);
            var first = true;
            foreach (var l in detail.Launches.Where(l => l.Platform == "windows" && !string.IsNullOrWhiteSpace(l.Command)))
            {
                var direct = Path.Combine(installDir, l.Command.Replace('/', '\\'));
                var action = File.Exists(direct)
                    ? new GameAction { Type = GameActionType.File, Path = direct, WorkingDir = installDir }
                    : new GameAction { Type = GameActionType.File, Path = "cmd.exe", Arguments = "/C \"" + l.Command + "\"", WorkingDir = installDir };
                action.Name = string.IsNullOrWhiteSpace(l.Name) ? "Play" : l.Name;
                action.IsPlayAction = first;
                first = false;
                actions.Add(action);
            }
            return actions;
        }

        /// <summary>Puts freshly built play actions on the Playnite record right after an install.</summary>
        public void ApplyPlayActions(Guid playniteGameId, List<GameAction> actions)
        {
            if (actions == null || actions.Count == 0) return;
            var game = PlayniteApi.Database.Games.Get(playniteGameId);
            if (game == null) return;
            game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>(actions);
            PlayniteApi.Database.Games.Update(game);
        }

        // ---- install / uninstall ------------------------------------------------

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new DropInstallController(args.Game, this);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new DropUninstallController(args.Game, this);
        }

        // ---- send to drop -------------------------------------------------------

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (!Settings.CanSend) yield break;
            // Any game, from any library, as long as it has a folder to send. Our own
            // Drop cards are excluded: sending a Drop game back to Drop is a loop.
            var game = args.Games?.Count == 1 ? args.Games[0] : null;
            if (game == null || game.PluginId == Id || string.IsNullOrWhiteSpace(game.InstallDirectory)) yield break;

            yield return new GameMenuItem
            {
                Description = "Send to Drop",
                MenuSection = "Drop",
                Action = _ => DropSend.Run(this, game),
            };
        }

        // ---- sign in ------------------------------------------------------------

        /// <summary>
        /// The device-code flow from the settings page. Order matters: the websocket
        /// listener must be up before the user approves the code, or the server's
        /// approve call fails with "No client listening for authorization".
        /// </summary>
        public void SignInInteractive(DropSettingsViewModel vm)
        {
            var baseUrl = vm.Settings.BaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Set the Drop instance URL first.", "Drop");
                return;
            }

            DropAuthStart start;
            try
            {
                start = new DropClient(baseUrl,
                    (p, a) => DropHttp.Get(baseUrl, p, a),
                    (p, a, b) => DropHttp.Post(baseUrl, p, a, b)).BeginAuth("Playnite", "Windows");
            }
            catch (Exception ex)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Could not start sign-in: " + DropHttp.Describe(ex), "Drop");
                return;
            }
            if (!start.UsesCode)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Drop did not return a sign-in code. Is the URL the instance root?", "Drop");
                return;
            }

            var client = Api();
            var cts = new CancellationTokenSource();
            var wait = Task.Run(() => DropAuth.WaitForApprovalAsync(client, baseUrl, start.Code, TimeSpan.FromMinutes(10), cts.Token));

            PlayniteApi.Dialogs.ShowMessage(
                "Your sign-in code is:\n\n    " + start.Code + "\n\nIn Drop's web UI open Settings, Clients, and enter it. Then press OK here.",
                "Drop");

            DropCredentials creds = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = "Waiting for Drop to approve code " + start.Code + "...";
                try
                {
                    while (!wait.Wait(500))
                    {
                        if (progress.CancelToken.IsCancellationRequested)
                        {
                            cts.Cancel();
                            return;
                        }
                    }
                    creds = wait.Result;
                }
                catch (AggregateException ex)
                {
                    throw ex.InnerException ?? ex;
                }
            }, new GlobalProgressOptions("Drop sign-in", true) { IsIndeterminate = true });

            if (creds == null)
            {
                if (wait.IsFaulted)
                {
                    var inner = wait.Exception?.InnerException;
                    PlayniteApi.Dialogs.ShowErrorMessage("Sign-in failed: " + (inner == null ? "unknown error" : DropHttp.Describe(inner)), "Drop");
                }
                return;
            }
            if (!creds.IsComplete)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Drop approved the code but returned an incomplete credential. Try again.", "Drop");
                return;
            }

            vm.Settings.ClientId = creds.ClientId;
            vm.Settings.PrivateKeyPem = creds.PrivateKey;
            vm.Save();
            PlayniteApi.Dialogs.ShowMessage("Signed in as client " + creds.ClientId + ". Update the library to see your Drop games.", "Drop");
        }
    }

    /// <summary>
    /// The "client" from Playnite's point of view is the Drop desktop app if it is
    /// installed; it is optional, the plugin does not need it, so its absence is not
    /// an error.
    /// </summary>
    public class DropDesktopClient : LibraryClient
    {
        public override bool IsInstalled => ExePath != null;
        public override string Icon => null;

        private static string ExePath
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var candidate in new[]
                {
                    Path.Combine(local, "Programs", "Drop", "Drop.exe"),
                    Path.Combine(local, "Drop", "Drop.exe"),
                })
                {
                    if (File.Exists(candidate)) return candidate;
                }
                return null;
            }
        }

        public override void Open()
        {
            var exe = ExePath;
            if (exe != null) System.Diagnostics.Process.Start(exe);
        }
    }
}
