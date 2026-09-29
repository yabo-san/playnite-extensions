using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Yabo.Shared;

namespace RohanKarPlaynite
{
    /// <summary>
    /// The y4bo launcher's library in Playnite.
    ///
    /// Earlier versions scanned the install folder and guessed each game's exe,
    /// because library.db was empty on a real install. The launcher now writes
    /// playnite-export.json on every library change, with the exe it picked, so this
    /// reads that file instead, watches it, and drives installs, uninstalls and
    /// "open in launcher" through the launcher's own CLI.
    /// </summary>
    public class RohanKarLibraryPlugin : LibraryPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // FIXED: every game this plugin ever imported carries it as PluginId.
        public override Guid Id { get; } = Guid.Parse("ecc7bf2f-5416-49d4-88c6-648397ed2668");

        public override string Name => "RohanKar";

        public override LibraryClient Client { get; }

        private const string GameIdsFile = "game-ids.json";

        private readonly object sync = new object();
        private readonly RohanKarSettingsViewModel settingsViewModel;
        private FileSystemWatcher watcher;
        private Timer debounce;
        private LauncherExport current;
        private Dictionary<string, string> gameIds = new Dictionary<string, string>(StringComparer.Ordinal);

        public RohanKarLibraryPlugin(IPlayniteAPI api) : base(api)
        {
            settingsViewModel = new RohanKarSettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true };
            Client = new RohanKarClient(() => Settings.EffectiveLauncherExePath);
            gameIds = LoadGameIds();
        }

        public RohanKarSettings Settings => settingsViewModel.Settings;

        public override ISettings GetSettings(bool firstRunSettings) => settingsViewModel;

        public override UserControl GetSettingsView(bool firstRunSettings) => new RohanKarSettingsView(PlayniteApi);

        // ─── Import ───────────────────────────────────────────────────────────

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var export = ReadExport(notify: true);
            if (export == null) return new List<GameMetadata>();
            var known = KnownGames();
            var ids = Resolve(export, known);
            var byId = known.GroupBy(k => k.GameId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var games = export.Games
                .TakeWhile(_ => !args.CancelToken.IsCancellationRequested)
                .Select(r => GameMapper.ToMetadata(r, ids[r.Id], byId.TryGetValue(ids[r.Id], out var k) ? k : null))
                .ToList();
            MarkNotInstalled(GameIdResolver.NoLongerExported(known, ids.Values));
            return games;
        }

        /// <summary>
        /// The export changed: bring Playnite's records in line without waiting for a
        /// library update. New records are imported; existing ones get install state,
        /// install folder, Play action, playtime and last played. Name, art, tags and
        /// everything else the user may have edited are left alone.
        /// </summary>
        public void ApplyExport()
        {
            var export = ReadExport(notify: false);
            if (export == null) return;
            var known = KnownGames();
            var ids = Resolve(export, known);
            var games = PlayniteApi.Database.Games.Where(g => g.PluginId == Id && g.GameId != null)
                .GroupBy(g => g.GameId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            using (PlayniteApi.Database.BufferedUpdate())
            {
                foreach (var r in export.Games)
                {
                    var gameId = ids[r.Id];
                    if (!games.TryGetValue(gameId, out var game))
                    {
                        PlayniteApi.Database.ImportGame(GameMapper.ToMetadata(r, gameId, null), this);
                        continue;
                    }
                    if (game.IsInstalling || game.IsUninstalling) continue;   // the controller reports those
                    game.IsInstalled = r.Installed;
                    game.InstallDirectory = r.Installed ? r.InstallDir : null;
                    game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>(
                        (game.GameActions ?? Enumerable.Empty<GameAction>()).Where(a => !IsOurPlayAction(a))
                        .Concat(GameMapper.PlayActions(r)));
                    if (r.PlaytimeSeconds > game.Playtime) game.Playtime = r.PlaytimeSeconds;
                    var played = r.LastPlayed?.ToLocalTime();
                    if (played != null && (game.LastActivity == null || played > game.LastActivity)) game.LastActivity = played;
                    PlayniteApi.Database.Games.Update(game);
                }
            }
            MarkNotInstalled(GameIdResolver.NoLongerExported(known, ids.Values));
        }

        private static bool IsOurPlayAction(GameAction a)
        {
            return a.IsPlayAction && a.Type == GameActionType.File && a.Name == GameMapper.PlayActionName;
        }

        // Records the export stopped listing stay in Playnite, not installed
        private void MarkNotInstalled(List<string> gameIds)
        {
            if (gameIds.Count == 0) return;
            var set = new HashSet<string>(gameIds, StringComparer.Ordinal);
            using (PlayniteApi.Database.BufferedUpdate())
            {
                foreach (var game in PlayniteApi.Database.Games.Where(g => g.PluginId == Id && set.Contains(g.GameId)).ToList())
                {
                    game.IsInstalled = false;
                    PlayniteApi.Database.Games.Update(game);
                }
            }
        }

        private List<KnownGame> KnownGames()
        {
            return PlayniteApi.Database.Games
                .Where(g => g.PluginId == Id && !string.IsNullOrEmpty(g.GameId))
                .Select(g => new KnownGame
                {
                    GameId = g.GameId,
                    Name = g.Name,
                    InstallDirectory = g.InstallDirectory,
                    HasCover = !string.IsNullOrEmpty(g.CoverImage),
                    HasBackground = !string.IsNullOrEmpty(g.BackgroundImage),
                    IsInstalled = g.IsInstalled,
                })
                .ToList();
        }

        private Dictionary<string, string> Resolve(LauncherExport export, List<KnownGame> known)
        {
            lock (sync)
            {
                var ids = GameIdResolver.Resolve(export.Games, known, gameIds);
                if (!ids.All(kv => gameIds.TryGetValue(kv.Key, out var v) && v == kv.Value))
                {
                    foreach (var kv in ids) gameIds[kv.Key] = kv.Value;
                    SaveGameIds();
                }
                return ids;
            }
        }

        // ─── Controllers ──────────────────────────────────────────────────────

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new RohanKarInstallController(args.Game, this, ExportIdOf(args.Game));
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new RohanKarUninstallController(args.Game, this, ExportIdOf(args.Game));
        }

        // The game's own Play action runs the exe. Without one: the exe from the export
        // if it has one by now (just installed), else the launcher opens on the item.
        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            if (args.Game.GameActions?.Any(a => a.IsPlayAction) == true) yield break;
            var exportId = ExportIdOf(args.Game);
            var record = FindRecord(exportId);
            if (record?.Playable == true)
            {
                var play = GameMapper.PlayActions(record)[0];
                yield return new AutomaticPlayController(args.Game)
                {
                    Name = play.Name,
                    Type = AutomaticPlayActionType.File,
                    Path = play.Path,
                    Arguments = play.Arguments,
                    WorkingDir = play.WorkingDir,
                    TrackingMode = TrackingMode.Default,
                };
                yield break;
            }
            yield return new RohanKarOpenInLauncherController(args.Game, this, exportId);
        }

        /// <summary>The export id a Playnite game came from (its GameId unless a legacy record was matched).</summary>
        public string ExportIdOf(Game game)
        {
            lock (sync)
            {
                return gameIds.FirstOrDefault(kv => kv.Value == game.GameId).Key ?? game.GameId;
            }
        }

        public LauncherGame FindRecord(string exportId)
        {
            return ReadExport(notify: false)?.Games.FirstOrDefault(g => g.Id == exportId);
        }

        public void ClearUninstalling(Guid gameId)
        {
            var game = PlayniteApi.Database.Games.Get(gameId);
            if (game == null) return;
            game.IsUninstalling = false;
            PlayniteApi.Database.Games.Update(game);
        }

        public void Notify(string id, string text)
        {
            PlayniteApi.Notifications.Add("rohankar-" + id, text, NotificationType.Error);
        }

        // ─── The export file ──────────────────────────────────────────────────

        private LauncherExport ReadExport(bool notify)
        {
            var path = Settings.EffectiveExportPath;
            try
            {
                var export = LauncherExportParser.Read(path);
                lock (sync) current = export;
                PlayniteApi.Notifications.Remove("rohankar-export");
                return export;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is NotSupportedException)
            {
                Logger.Warn(e, $"RohanKar: could not read {path}");
                if (notify)
                {
                    var why = e is FileNotFoundException || e is DirectoryNotFoundException
                        ? $"No playnite-export.json at {path}. Start the y4bo launcher once, or set the path in the plugin settings."
                        : $"Could not read {path}: {e.Message}";
                    PlayniteApi.Notifications.Add("rohankar-export", why, NotificationType.Error);
                }
                lock (sync) return notify ? null : current;
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            StartWatching();
            ScheduleApply(0);   // catch up on changes made while Playnite was closed
        }

        /// <summary>Applies the export after <paramref name="delayMs"/>, batching bursts of changes.</summary>
        public void ScheduleApply(int delayMs = 750) => debounce?.Change(delayMs, Timeout.Infinite);

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args) => StopWatching();

        public void OnSettingsSaved() => StartWatching();

        // The launcher replaces the file by rename, so Renamed and Created matter as much as Changed
        private void StartWatching()
        {
            StopWatching();
            var path = Settings.EffectiveExportPath;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Logger.Warn($"RohanKar: not watching {path}, its folder does not exist");
                return;
            }
            debounce = new Timer(_ => SafeApply(), null, Timeout.Infinite, Timeout.Infinite);
            watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            watcher.Changed += (s, e) => ScheduleApply();
            watcher.Created += (s, e) => ScheduleApply();
            watcher.Renamed += (s, e) => ScheduleApply();
            watcher.EnableRaisingEvents = true;
        }

        private void StopWatching()
        {
            watcher?.Dispose();
            watcher = null;
            debounce?.Dispose();
            debounce = null;
        }

        private void SafeApply()
        {
            try
            {
                ApplyExport();
            }
            catch (Exception e)
            {
                Logger.Error(e, "RohanKar: applying playnite-export.json failed");
            }
        }

        // ─── game-ids.json: export id → GameId, so matches to legacy records stick ──

        private string GameIdsPath => Path.Combine(GetPluginUserDataPath(), GameIdsFile);

        private Dictionary<string, string> LoadGameIds()
        {
            try
            {
                if (File.Exists(GameIdsPath))
                {
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(GameIdsPath))
                        ?? new Dictionary<string, string>();
                }
            }
            catch (Exception e)
            {
                Logger.Warn(e, "RohanKar: game-ids.json unreadable, matching again");
            }
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private void SaveGameIds()
        {
            try
            {
                Directory.CreateDirectory(GetPluginUserDataPath());
                var tmp = GameIdsPath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(gameIds, Formatting.Indented));
                if (File.Exists(GameIdsPath)) File.Delete(GameIdsPath);
                File.Move(tmp, GameIdsPath);
            }
            catch (Exception e)
            {
                Logger.Warn(e, "RohanKar: could not save game-ids.json");
            }
        }
    }

    /// <summary>The launcher itself, for Playnite's "open client".</summary>
    public class RohanKarClient : LibraryClient
    {
        private readonly Func<string> exePath;

        public RohanKarClient(Func<string> exePath) => this.exePath = exePath;

        public override bool IsInstalled => File.Exists(exePath());

        public override string Icon => null;

        public override void Open()
        {
            if (IsInstalled) System.Diagnostics.Process.Start(exePath());
        }
    }
}
