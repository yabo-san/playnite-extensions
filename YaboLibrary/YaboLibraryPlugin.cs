using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace YaboLibrary
{
    /// <summary>
    /// Yabo Launcher library importer. Imports the port catalog from the engine CLI
    /// (<c>yabo-launcher.exe --list-json</c>) into Playnite, wiring install / uninstall / play
    /// to <c>--download</c> / <c>--uninstall</c> / <c>--play</c>. Multi-game ports become a
    /// Play-button dropdown; link-out ports become a URL action.
    /// </summary>
    public class YaboLibraryPlugin : LibraryPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // FIXED — DO NOT CHANGE. This Guid becomes every imported game's PluginId; changing it
        // orphans the entire library. Generated once at scaffold time.
        public override Guid Id { get; } = Guid.Parse("1a390433-18ff-42bd-8741-8c1778a7cab9");

        public override string Name => "Yabo Launcher";

        // Source name shown on each game and used for the per-field "Store" metadata precedence.
        private const string SourceName = "Yabo Launcher";

        // Tag applied to games the engine reports as having a waiting GitHub-release update
        // (status "UpdateAvailable" in --list-json). Added/removed additively in the refresh path.
        private const string UpdateAvailableTag = "Update available";

        // Marker file under GetPluginUserDataPath() recording the set of GameIds we last notified as
        // having updates, so the "N game(s) have updates" notification fires once per change (not every sync).
        private const string LastUpdateSetFileName = "last-update-set.txt";

        // Tag applied to games whose source link (IA contentUrl / direct download, or GitHub repo) the engine
        // reports as DEAD via --check-url. Added/removed additively, like UpdateAvailableTag — removed when the
        // link comes back alive on a later sweep.
        private const string SourceOfflineTag = "Source offline";

        // Marker file under GetPluginUserDataPath() recording the set of GameIds whose source link we last
        // notified as DEAD, so the "N game(s) have a dead source link" notification fires once per change
        // (mirrors LastUpdateSetFileName for the dead-source sweep).
        private const string LastDeadSetFileName = "last-dead-set.txt";

        public override LibraryClient Client => null;

        public YaboLibrarySettingsViewModel SettingsViewModel { get; }

        public YaboLibraryPlugin(IPlayniteAPI api) : base(api)
        {
            SettingsViewModel = new YaboLibrarySettingsViewModel(this);
            Properties = new LibraryPluginProperties
            {
                HasSettings = true,
                HasCustomizedGameImport = false
            };
        }

        private string ExePath => SettingsViewModel?.Settings?.ExePath;

        // ----------------------------------------------------------------------------------
        // snesrev-native identification + isnesrev companion (shader / controls editors)
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// Explicit allow-list of snesrev-native cards whose folderName does NOT carry the
        /// <c>snesrev.</c> prefix but which are still driven by the snesrev engine (and so expose the
        /// isnesrev shader/controls editors). Compared case-insensitively against <c>folderName</c>.
        /// </summary>
        private static readonly HashSet<string> SnesrevAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "JRickey.BattleShip",
            "RadzPrower.Zelda-3-Launcher",
        };

        /// <summary>
        /// snesrev-native predicate. A card is snesrev-native when its <c>folderName</c> begins with the
        /// <c>snesrev.</c> prefix (e.g. <c>snesrev.smw</c>, <c>snesrev.sm</c>) OR it is in the explicit
        /// <see cref="SnesrevAllowList"/> (the BattleShip + zelda3 cards, which share the snesrev runtime
        /// but use their own repo-derived folderName). apps.json has no dedicated engine/native field, so
        /// folderName is the stable, drift-proof signal.
        /// </summary>
        private static bool IsSnesrevNative(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }
            return folderName.StartsWith("snesrev.", StringComparison.OrdinalIgnoreCase)
                   || SnesrevAllowList.Contains(folderName);
        }

        /// <summary>
        /// Resolve the isnesrev.exe path: the explicit plugin setting if set, otherwise the default
        /// beside the engine — <c>&lt;engineDir&gt;\isnesrev\isnesrev.exe</c>. Returns null when neither
        /// can be formed (no engine path), in which case the GLSL/controls actions are not attached.
        /// </summary>
        private string ResolveIsnesrevPath()
        {
            var configured = SettingsViewModel?.Settings?.IsnesrevPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return null;
            }

            try
            {
                var engineDir = Path.GetDirectoryName(exe);
                if (string.IsNullOrWhiteSpace(engineDir))
                {
                    return null;
                }
                return Path.Combine(engineDir, "isnesrev", "isnesrev.exe");
            }
            catch
            {
                return null;
            }
        }

        // ----------------------------------------------------------------------------------
        // Patch-note "bell" — one notification per published release
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// On startup, surface the engine's latest published release notes as ONE non-blocking
        /// Playnite notification — gated to fire exactly once per release version.
        /// </summary>
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);
            TryShowReleaseNotification();
        }

        /// <summary>
        /// Read the engine feed's <c>release</c> block (apps.json beside the exe); if its version
        /// differs from the last one we showed, raise a single Info notification and remember it.
        /// All failures are swallowed — a missing/unpublished feed simply shows nothing.
        /// </summary>
        private void TryShowReleaseNotification()
        {
            try
            {
                var release = YaboReleaseFeed.ReadRelease(ExePath);
                if (release == null || string.IsNullOrWhiteSpace(release.Version))
                {
                    return; // no published release block yet
                }

                var userData = GetPluginUserDataPath();
                var lastShown = YaboReleaseFeed.GetLastShownVersion(userData);
                if (string.Equals(lastShown, release.Version, StringComparison.Ordinal))
                {
                    return; // already announced this version — gate to once per release
                }

                // Build a friendly one-line body: "<title> — <notes>" with an optional "(+N games)" tail.
                var title = string.IsNullOrWhiteSpace(release.Title)
                    ? $"Yabo {release.Version}"
                    : release.Title;
                var body = new StringBuilder(title);
                if (!string.IsNullOrWhiteSpace(release.Notes))
                {
                    body.Append(" — ").Append(release.Notes);
                }
                if (release.Added != null && release.Added.Count > 0)
                {
                    body.Append($" (+{release.Added.Count} new)");
                }

                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "yabo-release-" + release.Version,
                    body.ToString(),
                    NotificationType.Info));

                // Persist BEFORE returning so a crash mid-session can't re-show; once-per-version.
                YaboReleaseFeed.SetLastShownVersion(userData, release.Version);
                Logger.Info($"Yabo: announced release {release.Version}.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: release-note notification failed (non-fatal).");
            }
        }

        // ----------------------------------------------------------------------------------
        // Main menu — "Add a game from GitHub/IA links…"
        // ----------------------------------------------------------------------------------

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@Yabo Launcher",
                Description = "Yabo: Add a game from GitHub/IA links…",
                Action = _ => AddGameFromLinks()
            };
            yield return new MainMenuItem
            {
                MenuSection = "@Yabo Launcher",
                Description = "Yabo: Collide a binary + data…",
                Action = _ => CollideBinaryAndData()
            };
            yield return new MainMenuItem
            {
                MenuSection = "@Yabo Launcher",
                Description = "Yabo: Scan for installed games…",
                Action = _ => ScanForInstalledGames()
            };
            yield return new MainMenuItem
            {
                MenuSection = "@Yabo Launcher",
                Description = "Yabo: Check game sources",
                Action = _ => CheckGameSources()
            };
        }

        // ----------------------------------------------------------------------------------
        // Dead-source surfacing — "tell me when a game's source link goes dead" (#25)
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// Explicit menu action: for every catalog card that has a source URL (its IA/direct-download
        /// <c>contentUrl</c>, else its GitHub repo), shell the engine's <c>--check-url "&lt;url&gt;"</c>
        /// (which prints ALIVE/DEAD) on a throttled pool of workers, collect the DEAD ones, then surface
        /// ONE Playnite notification "N game(s) have a dead source link" — gated to once per change like the
        /// update notification — and toggle a "Source offline" tag on the affected games. Runs off the UI
        /// thread; all failures are swallowed (a dead-source sweep must never break Playnite).
        /// </summary>
        private void CheckGameSources()
        {
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Set the path to yabo-launcher.exe in the Yabo Launcher settings first.",
                    "Yabo Launcher");
                return;
            }

            PlayniteApi.Notifications.Add("yabo-source-check-start",
                "Checking game source links…", NotificationType.Info);
            // Network-heavy: run off the UI thread, explicit-only (NOT auto-run on every library update).
            System.Threading.Tasks.Task.Run(() => RunSourceSweep(exe, announceWhenClean: true));
        }

        /// <summary>
        /// The catalog-wide dead-source sweep shared by the menu action and the opportunistic once-per-session
        /// run. Builds the (GameId → source URL) map from the catalog, checks each distinct URL once via
        /// <c>--check-url</c> on a small throttled worker pool, then folds the result back per game: raises the
        /// gated "N game(s) have a dead source link" notification and toggles the "Source offline" tag.
        /// When <paramref name="announceWhenClean"/> is true (explicit menu run) a clean result is reported too.
        /// </summary>
        private void RunSourceSweep(string exe, bool announceWhenClean)
        {
            try
            {
                var catalog = YaboCli.GetCatalog(exe, System.Threading.CancellationToken.None);
                if (catalog == null || catalog.Count == 0)
                {
                    if (announceWhenClean)
                    {
                        PlayniteApi.Notifications.Add("yabo-source-check-done",
                            "Yabo: no catalog to check.", NotificationType.Info);
                    }
                    return;
                }

                // Map each card with a source URL: GameId (folderName) → URL. Check the contentUrl (IA / direct
                // download) when present, otherwise the GitHub repo URL — the same provenance order used for the
                // primary "Source" link. Cards with no source URL are skipped (nothing to go dead).
                var urlByGame = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in catalog)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FolderName))
                    {
                        continue;
                    }
                    var url = ResolveSourceUrl(entry);
                    if (!string.IsNullOrWhiteSpace(url) && !urlByGame.ContainsKey(entry.FolderName))
                    {
                        urlByGame[entry.FolderName] = url;
                    }
                }

                if (urlByGame.Count == 0)
                {
                    if (announceWhenClean)
                    {
                        PlayniteApi.Notifications.Add("yabo-source-check-done",
                            "Yabo: no game source links to check.", NotificationType.Info);
                    }
                    return;
                }

                // Check each DISTINCT url once (many cards share a repo/IA item) on a small worker pool so we
                // don't hammer the network — bounded concurrency, result cached by url.
                var distinctUrls = urlByGame.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var deadByUrl = CheckUrlsThrottled(exe, distinctUrls);

                // Fold per-url verdicts back to the set of GameIds whose source is dead.
                var deadSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in urlByGame)
                {
                    if (deadByUrl.TryGetValue(kv.Value, out var isDead) && isDead)
                    {
                        deadSet.Add(kv.Key);
                    }
                }

                ApplySourceOfflineTags(deadSet);
                NotifyDeadSources(deadSet, announceWhenClean);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: source-link sweep failed (non-fatal).");
                if (announceWhenClean)
                {
                    PlayniteApi.Notifications.Add("yabo-source-check-done",
                        "Yabo: checking game sources failed — see the extension log.",
                        NotificationType.Error);
                }
            }
        }

        /// <summary>
        /// The single source URL to liveness-check for a catalog card: its IA / direct-download
        /// <c>contentUrl</c> when set, otherwise its GitHub repo URL (normalised to https://github.com/…).
        /// Returns null when the card has neither (e.g. a data-only / external link-out card) — nothing to check.
        /// </summary>
        private static string ResolveSourceUrl(YaboCatalogEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.ContentUrl) &&
                entry.ContentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return entry.ContentUrl.Trim();
            }
            if (!string.IsNullOrWhiteSpace(entry.Repository))
            {
                return entry.Repository.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? entry.Repository.Trim()
                    : $"https://github.com/{entry.Repository.Trim()}";
            }
            return null;
        }

        /// <summary>
        /// Run <c>--check-url "&lt;url&gt;"</c> for each url on a bounded worker pool (default 4) so the sweep
        /// parallelizes without hammering the network. Returns url → isDead. The engine prints ALIVE/DEAD;
        /// per the engine's own sweep convention any output matching /dead/i means dead, anything else alive,
        /// and an engine failure is treated as NOT dead (we don't want a transient engine error to spam
        /// false "dead source" alerts).
        /// </summary>
        private Dictionary<string, bool> CheckUrlsThrottled(string exe, List<string> urls)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (urls == null || urls.Count == 0)
            {
                return result;
            }

            const int maxParallel = 4;
            using (var throttle = new System.Threading.SemaphoreSlim(maxParallel))
            {
                var tasks = new List<System.Threading.Tasks.Task>();
                var gate = new object();
                foreach (var url in urls)
                {
                    throttle.Wait();
                    tasks.Add(System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            int exit;
                            var output = YaboCli.Run(exe, $"--check-url {Quote(url)}",
                                System.Threading.CancellationToken.None, out exit);
                            // DEAD only when the engine succeeded AND its output says so (avoids false positives
                            // from engine/transient failures).
                            var isDead = exit == 0 && !string.IsNullOrWhiteSpace(output) &&
                                         output.IndexOf("DEAD", StringComparison.OrdinalIgnoreCase) >= 0;
                            lock (gate)
                            {
                                result[url] = isDead;
                            }
                        }
                        finally
                        {
                            throttle.Release();
                        }
                    }));
                }
                System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
            }
            return result;
        }

        /// <summary>
        /// Add/remove the "Source offline" tag on our own games to match <paramref name="deadSet"/> (GameIds
        /// whose source link is currently dead). Additive + self-removing, exactly like the "Update available"
        /// tag toggle — a game that drops out of the dead set has its tag removed so it never sticks stale.
        /// </summary>
        private void ApplySourceOfflineTags(HashSet<string> deadSet)
        {
            try
            {
                var tag = PlayniteApi.Database.Tags.Add(SourceOfflineTag);
                if (tag == null)
                {
                    return;
                }
                var ours = PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList();
                foreach (var game in ours)
                {
                    if (string.IsNullOrWhiteSpace(game.GameId))
                    {
                        continue;
                    }
                    var shouldHave = deadSet.Contains(game.GameId);
                    var has = game.TagIds != null && game.TagIds.Contains(tag.Id);
                    if (shouldHave && !has)
                    {
                        if (game.TagIds == null) { game.TagIds = new List<Guid>(); }
                        game.TagIds.Add(tag.Id);
                        PlayniteApi.Database.Games.Update(game);
                    }
                    else if (!shouldHave && has)
                    {
                        game.TagIds.Remove(tag.Id);
                        PlayniteApi.Database.Games.Update(game);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: applying 'Source offline' tags failed (non-fatal).");
            }
        }

        /// <summary>
        /// Raise ONE non-blocking "N game(s) have a dead source link" notification — but only when the set of
        /// dead-source games actually CHANGED since the last sweep (gated to once per change, like
        /// <see cref="NotifyUpdatesAvailable"/>). The set is persisted under the plugin user-data dir so a sweep
        /// that finds the same dead links is silent, and the notification clears when links come back alive.
        /// When <paramref name="announceWhenClean"/> is true (explicit menu run) a clean result is reported even
        /// if unchanged, so the user gets feedback that the action ran.
        /// </summary>
        private void NotifyDeadSources(HashSet<string> deadSet, bool announceWhenClean)
        {
            try
            {
                var userData = GetPluginUserDataPath();
                var previous = ReadGameIdSet(userData, LastDeadSetFileName);
                var unchanged = deadSet.SetEquals(previous);

                // Persist the new set first so a crash can't cause a re-announce of the same state.
                WriteGameIdSet(userData, LastDeadSetFileName, deadSet);

                if (deadSet.Count == 0)
                {
                    PlayniteApi.Notifications.Remove("yabo-dead-sources");
                    if (announceWhenClean)
                    {
                        PlayniteApi.Notifications.Add("yabo-source-check-done",
                            "Yabo: all game source links are alive.", NotificationType.Info);
                    }
                    return;
                }

                if (unchanged && !announceWhenClean)
                {
                    return; // same dead set as last time and not an explicit run — stay silent.
                }

                var body = deadSet.Count == 1
                    ? "1 game has a dead source link — its download source has gone offline (check its 'Source offline' tag)."
                    : $"{deadSet.Count} games have a dead source link — their download sources have gone offline (filter by the 'Source offline' tag).";
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "yabo-dead-sources",
                    body,
                    NotificationType.Error));
                Logger.Info($"Yabo: announced {deadSet.Count} game(s) with a dead source link.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: dead-source notification failed (non-fatal).");
            }
        }

        /// <summary>Read a persisted newline-delimited set of GameIds from a marker file under the plugin user-data dir.</summary>
        private static HashSet<string> ReadGameIdSet(string pluginUserDataPath, string fileName)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(pluginUserDataPath, fileName);
                if (!File.Exists(path))
                {
                    return set;
                }
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw?.Trim();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        set.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Yabo: failed to read marker '{fileName}'.");
            }
            return set;
        }

        /// <summary>Persist a set of GameIds to a marker file under the plugin user-data dir (newline-delimited).</summary>
        private static void WriteGameIdSet(string pluginUserDataPath, string fileName, HashSet<string> set)
        {
            try
            {
                Directory.CreateDirectory(pluginUserDataPath);
                var path = Path.Combine(pluginUserDataPath, fileName);
                File.WriteAllLines(path, set);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Yabo: failed to persist marker '{fileName}'.");
            }
        }

        /// <summary>
        /// "Collide a binary + data" — the explicit marriage flow. Prompts (in order) for a GitHub repo
        /// (owner/repo, the binary engine), an Internet Archive data link (the ROM/asset overlay), and a
        /// display name, then shells the engine <c>--add-game --repo "&lt;repo&gt;" --ia "&lt;ia&gt;" --name "&lt;name&gt;"</c>
        /// to write the married binary+data overlay card (another agent owns the engine side of --add-game).
        /// Unlike <see cref="AddGameFromLinks"/> (where IA + name are optional), the collide flow treats all
        /// three as the intended inputs of a marriage. On success the user is told to Update Library to import.
        /// </summary>
        private void CollideBinaryAndData()
        {
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Set the path to yabo-launcher.exe in the Yabo Launcher settings first.",
                    "Yabo Launcher");
                return;
            }

            // 1) GitHub repo (the binary engine) — required.
            var repoRes = PlayniteApi.Dialogs.SelectString(
                "GitHub repo for the binary/engine (owner/repo or full URL):",
                "Collide — GitHub binary",
                string.Empty);
            if (!repoRes.Result || string.IsNullOrWhiteSpace(repoRes.SelectedString))
            {
                return; // cancelled or empty — abort silently
            }
            var repo = repoRes.SelectedString.Trim();

            // 2) Internet Archive data link (the ROM/asset overlay) — required for a collision.
            var iaRes = PlayniteApi.Dialogs.SelectString(
                "Internet Archive data link or identifier (the ROM/asset to overlay):",
                "Collide — Internet Archive data",
                string.Empty);
            if (!iaRes.Result || string.IsNullOrWhiteSpace(iaRes.SelectedString))
            {
                return; // cancelled or empty — a collision needs the data side
            }
            var ia = iaRes.SelectedString.Trim();

            // 3) Display name for the married card.
            var nameRes = PlayniteApi.Dialogs.SelectString(
                "Display name for the combined game:",
                "Collide — Name",
                string.Empty);
            if (!nameRes.Result || string.IsNullOrWhiteSpace(nameRes.SelectedString))
            {
                return; // cancelled or empty
            }
            var name = nameRes.SelectedString.Trim();

            // Build & shell --add-game with all three values (the engine writes the binary+data overlay card).
            var sb = new StringBuilder("--add-game --repo ");
            sb.Append(Quote(repo))
              .Append(" --ia ").Append(Quote(ia))
              .Append(" --name ").Append(Quote(name));

            int exit;
            var output = YaboCli.Run(exe, sb.ToString(), System.Threading.CancellationToken.None, out exit);
            if (exit != 0)
            {
                Logger.Warn($"Yabo: collide --add-game failed (exit {exit}). Output: {output}");
                PlayniteApi.Dialogs.ShowMessage(
                    $"Colliding the binary + data failed (exit code {exit}).\n\n" +
                    "Check that the GitHub repo and Internet Archive link are correct and the engine is configured." +
                    (string.IsNullOrWhiteSpace(output) ? string.Empty : "\n\n" + output.Trim()),
                    "Yabo Launcher");
                return;
            }

            PlayniteApi.Dialogs.ShowMessage(
                $"Collided \"{name}\" — the {repo} binary married to the Internet Archive data overlay.\n\n" +
                "Run Library → Update Library (or restart Playnite) to import the new game card.",
                "Yabo Launcher");
        }

        /// <summary>
        /// Folder-scan adoption (RohanKar parity). Prompts for a parent folder of game folders and shells
        /// <c>--scan-installs "&lt;parentFolder&gt;"</c> — the engine adopts every immediate subfolder that matches a
        /// catalog entry (GitHub-binary entries get update tracking; the engine writes installPath + the
        /// detected exe), with NO download and NO move. Reports the matched/adopted summary the CLI prints, then
        /// asks the user to Update Library so the newly-installed cards refresh (the SDK has no re-import hook).
        /// </summary>
        private void ScanForInstalledGames()
        {
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Set the path to yabo-launcher.exe in the Yabo Launcher settings first.",
                    "Yabo Launcher");
                return;
            }

            // The engine scans the immediate subfolders of whatever parent we pass. Default the user toward
            // their yabo games folder when one is configured, but let them point at any download folder.
            var configuredGames = SettingsViewModel?.Settings?.GamesPath;
            if (!string.IsNullOrWhiteSpace(configuredGames))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Pick the folder that CONTAINS your installed game folders (each game in its own subfolder).\n\n" +
                    $"Tip: your configured yabo games folder is:\n{configuredGames}",
                    "Yabo — Scan for installed games");
            }

            var parent = PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrWhiteSpace(parent))
            {
                return; // cancelled
            }

            PlayniteApi.Notifications.Add("yabo-scan-start",
                $"Scanning {parent} for installed games…", NotificationType.Info);
            RunEngineAsync(exe, $"--scan-installs {Quote(parent)}", (exit, output) =>
            {
                if (exit != 0)
                {
                    Logger.Warn($"Yabo: --scan-installs failed (exit {exit}). Output: {output}");
                    PlayniteApi.Notifications.Add("yabo-scan-done",
                        "Yabo: scan for installed games failed — see the extension log.",
                        NotificationType.Error);
                    return;
                }

                // The CLI prints a per-adoption line plus a final "OK scan-installs: N matched, M adopted, …".
                var summary = ParseScanSummary(output);
                PlayniteApi.Notifications.Add("yabo-scan-done",
                    (summary ?? "Yabo: scan finished.") +
                    " Run Library → Update Library to refresh installed states.",
                    NotificationType.Info);
            });
        }

        /// <summary>
        /// Pull a human-readable summary out of <c>--scan-installs</c> stdout. The engine prints a final line
        /// <c>OK scan-installs: N matched, M adopted, F failed, S skipped (of T subfolder(s)).</c>; we surface
        /// the adopted/matched counts. Returns null when no recognizable summary line is present.
        /// </summary>
        private static string ParseScanSummary(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var idx = line.IndexOf("OK scan-installs:", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    return "Yabo: " + line.Substring(idx + "OK scan-installs:".Length).Trim();
                }
            }
            return null;
        }

        /// <summary>
        /// Prompt the user for a GitHub repo (required), an optional Internet Archive link, and an
        /// optional display name, then shell the engine <c>--add-game</c> command. On success, ask
        /// the user to refresh the library so the new card imports (the SDK exposes no per-library
        /// re-import trigger, so a manual "Update Library" / restart is the supported path).
        /// </summary>
        private void AddGameFromLinks()
        {
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Set the path to yabo-launcher.exe in the Yabo Launcher settings first.",
                    "Yabo Launcher");
                return;
            }

            // 1) GitHub repo — required.
            var repoRes = PlayniteApi.Dialogs.SelectString(
                "GitHub repo for the port (owner/name or full URL):",
                "Add a game — GitHub repo",
                string.Empty);
            if (!repoRes.Result || string.IsNullOrWhiteSpace(repoRes.SelectedString))
            {
                return; // cancelled or empty — abort silently
            }
            var repo = repoRes.SelectedString.Trim();

            // 2) Internet Archive link — optional.
            var iaRes = PlayniteApi.Dialogs.SelectString(
                "Internet Archive link or identifier (optional — leave blank to skip):",
                "Add a game — Internet Archive",
                string.Empty);
            var ia = (iaRes.Result && !string.IsNullOrWhiteSpace(iaRes.SelectedString))
                ? iaRes.SelectedString.Trim()
                : null;

            // 3) Display name — optional.
            var nameRes = PlayniteApi.Dialogs.SelectString(
                "Display name (optional — leave blank to let the engine infer it):",
                "Add a game — Name",
                string.Empty);
            var name = (nameRes.Result && !string.IsNullOrWhiteSpace(nameRes.SelectedString))
                ? nameRes.SelectedString.Trim()
                : null;

            // Build the CLI argument string, quoting each value.
            var sb = new StringBuilder("--add-game --repo ");
            sb.Append(Quote(repo));
            if (!string.IsNullOrWhiteSpace(ia))
            {
                sb.Append(" --ia ").Append(Quote(ia));
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                sb.Append(" --name ").Append(Quote(name));
            }

            int exit;
            var output = YaboCli.Run(exe, sb.ToString(), System.Threading.CancellationToken.None, out exit);
            if (exit != 0)
            {
                Logger.Warn($"Yabo: --add-game failed (exit {exit}). Output: {output}");
                PlayniteApi.Dialogs.ShowMessage(
                    $"Adding the game failed (exit code {exit}).\n\n" +
                    "Check that the GitHub repo is correct and the engine is configured." +
                    (string.IsNullOrWhiteSpace(output) ? string.Empty : "\n\n" + output.Trim()),
                    "Yabo Launcher");
                return;
            }

            var added = string.IsNullOrWhiteSpace(name) ? repo : name;
            PlayniteApi.Dialogs.ShowMessage(
                $"Added \"{added}\" to the Yabo catalog.\n\n" +
                "Run Library → Update Library (or restart Playnite) to import the new game card.",
                "Yabo Launcher");
        }

        /// <summary>Wrap a value in double quotes for the CLI, stripping any embedded quotes.</summary>
        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", string.Empty) + "\"";
        }

        // ----------------------------------------------------------------------------------
        // Right-click game menu — re-wire orphaned engine capabilities into Playnite
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// libultraship / Harbour-Masters folderName suffixes (the segment after the last dot). These ports
        /// keep a JSON config beside the exe that the engine's <c>--get-config-json</c>/<c>--set-config-json</c>
        /// can read/write; mirror the engine's <c>HmJsonByFolderSuffix</c> table so detection can't drift.
        /// </summary>
        private static readonly HashSet<string> HmLibultrashipSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "shipwright",
            "2ship2harkinian",
            "starship",
            "ghostship",
            "spaghettikart",
        };

        /// <summary>
        /// True when a Playnite game is a Harbour-Masters / libultraship-family port — detected from its
        /// <c>GameId</c> (= engine folderName), which is the stable, drift-proof signal (display name renames
        /// don't affect it). Matches the <c>harbourmasters.</c> prefix OR a known libultraship folder suffix.
        /// </summary>
        private static bool IsHarbourMastersFamily(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }
            if (folderName.StartsWith("harbourmasters.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var suffix = folderName.Contains('.')
                ? folderName.Substring(folderName.LastIndexOf('.') + 1)
                : folderName;
            return HmLibultrashipSuffixes.Contains(suffix);
        }

        /// <summary>
        /// GitHub-binary predicate. A catalog card is a "GitHub binary" when its entry carries a
        /// <c>repository</c> (owner/name) in <c>--list-json</c> — i.e. yabo can track that GitHub repo's
        /// releases for updates and (re)install the binary from a release asset. IA-only cards have no
        /// <c>repository</c>, so adoption (which exists to wire up GitHub-release update tracking against an
        /// existing on-disk install) doesn't apply to them. Looked up by <c>folderName</c> (the stable
        /// engine identity == Playnite GameId), so a renamed display name never changes the verdict.
        /// </summary>
        private bool IsGitHubBinaryCard(string folderName)
        {
            var entry = FindCatalogEntry(folderName);
            return entry != null && !string.IsNullOrWhiteSpace(entry.Repository);
        }

        /// <summary>
        /// Look up a single catalog entry by <c>folderName</c> (== Playnite GameId) from the engine's
        /// <c>--list-json</c>. Returns null when the engine isn't configured, the catalog can't be read, or
        /// no entry matches. Used by the right-click menu's adopt gating; a per-invocation call is fine
        /// (menu builds are user-triggered and infrequent, and the other menu items already shell the engine).
        /// </summary>
        private YaboCatalogEntry FindCatalogEntry(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return null;
            }
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return null;
            }
            try
            {
                var catalog = YaboCli.GetCatalog(exe, System.Threading.CancellationToken.None);
                return catalog?.FirstOrDefault(e =>
                    e != null && string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Yabo: catalog lookup for '{folderName}' failed (non-fatal).");
                return null;
            }
        }

        /// <summary>
        /// Right-click menu for Yabo games. Surfaces engine capabilities that have no other entry point in
        /// Playnite: open/copy the install folder, update / change version (installed only), and — for
        /// Harbour-Masters / libultraship ports — edit the per-game enhancement config. All actions shell the engine via <see cref="YaboCli"/>
        /// using the SAME stable catalog identity (<c>--play "&lt;name&gt;"</c>) the controllers use.
        /// </summary>
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            // NOTE: display targeting ("send this game to a screen") deliberately does
            // NOT live here — it is the standalone YaboDisplay plugin. It applies to
            // every game, not just ours, and has nothing to do with port management.
            // Only our own games, and only when a single game is the target (these actions are per-game).
            var games = args?.Games?.Where(g => g != null && g.PluginId == Id).ToList();
            if (games == null || games.Count != 1)
            {
                yield break;
            }

            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                yield break; // no engine configured → nothing actionable
            }

            var game = games[0];
            var name = ResolveCliName(game);
            if (name == null)
            {
                // External link-out (URL action) — it has no install dir / shader / config surface.
                yield break;
            }

            // --- Universal: folder access -----------------------------------------------------------
            yield return new GameMenuItem
            {
                MenuSection = "Yabo",
                Description = "Open install folder",
                Action = _ => MenuOpenFolder(exe, name)
            };
            yield return new GameMenuItem
            {
                MenuSection = "Yabo",
                Description = "Copy install path",
                Action = _ => MenuCopyPath(exe, name)
            };

            // --- Installed-only: update / change version --------------------------------------------
            if (game.IsInstalled)
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Yabo",
                    Description = "Check for updates / Update",
                    Action = _ => MenuUpdate(exe, name)
                };
                yield return new GameMenuItem
                {
                    MenuSection = "Yabo",
                    Description = "Change version…",
                    Action = _ => MenuChangeVersion(exe, name)
                };
            }
            // --- GitHub-binary + not-installed only: adopt an existing install ----------------------
            // GitHub-binary ports may already be on disk (old launcher / manual download). "Adopt" points
            // yabo at that folder so the card flips to Installed AND GitHub-release update tracking turns on.
            // This is meaningless for IA-only cards (no GitHub releases to track) and for already-installed
            // ones, so it's gated to: has a `repository` in the catalog entry AND is not currently installed.
            else if (IsGitHubBinaryCard(game.GameId))
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Yabo",
                    Description = "Adopt existing install…",
                    Action = _ => MenuAdoptInstall(exe, name)
                };
                yield return new GameMenuItem
                {
                    MenuSection = "Yabo",
                    Description = "Pick executable…",
                    Action = _ => MenuPickExe(exe, name)
                };
            }

            // --- Harbour-Masters / libultraship only: per-game enhancement config --------------------
            if (IsHarbourMastersFamily(game.GameId))
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Yabo",
                    Description = "Configure enhancements…",
                    Action = _ => MenuConfigureEnhancements(exe, name)
                };
            }
        }

        /// <summary>Run the engine in the background (off the UI thread), then optionally report the result.</summary>
        private void RunEngineAsync(string exe, string arguments, Action<int, string> onDone = null)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                int exit;
                var output = YaboCli.Run(exe, arguments, System.Threading.CancellationToken.None, out exit);
                onDone?.Invoke(exit, output ?? string.Empty);
            });
        }

        private void MenuOpenFolder(string exe, string name)
        {
            // The engine opens the OS file manager itself; surface a failure message on a non-zero exit.
            RunEngineAsync(exe, $"--open-folder {Quote(name)}", (exit, output) =>
            {
                if (exit != 0)
                {
                    PlayniteApi.Dialogs.ShowMessage(
                        "Couldn't open the install folder. The game may not be installed yet." +
                        (string.IsNullOrWhiteSpace(output) ? string.Empty : "\n\n" + output.Trim()),
                        "Yabo Launcher");
                }
            });
        }

        private void MenuCopyPath(string exe, string name)
        {
            // --copy-path prints the launchable exe path to a clean stdout; copy it to the clipboard.
            int exit;
            var output = YaboCli.Run(exe, $"--copy-path {Quote(name)}", System.Threading.CancellationToken.None, out exit);
            var path = (output ?? string.Empty).Trim();
            if (exit != 0 || string.IsNullOrWhiteSpace(path))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Couldn't resolve the install path. The game may not be installed yet.",
                    "Yabo Launcher");
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(path);
                PlayniteApi.Notifications.Add("yabo-copy-path",
                    $"Copied install path:\n{path}", NotificationType.Info);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: clipboard copy failed.");
                PlayniteApi.Dialogs.ShowMessage(path, "Install path");
            }
        }

        private void MenuUpdate(string exe, string name)
        {
            // PER-GAME update: --force-update "<name>" reinstalls the latest release for THIS game only
            // (vs the all-installed-games --update). This is the action wired to the right-click "Update" item,
            // so updating one game never touches the rest of the library.
            PlayniteApi.Notifications.Add("yabo-update-start",
                $"Updating \"{name}\"…", NotificationType.Info);
            RunEngineAsync(exe, $"--force-update {Quote(name)}", (exit, output) =>
            {
                PlayniteApi.Notifications.Add("yabo-update-done",
                    exit == 0
                        ? $"Updated \"{name}\". Run Library → Update Library to refresh its state."
                        : $"Updating \"{name}\" failed — see the extension log.",
                    exit == 0 ? NotificationType.Info : NotificationType.Error);
            });
        }

        private void MenuChangeVersion(string exe, string name)
        {
            // 1) Ask the engine for the available releases (JSON: [{tag, prerelease, latest, installed, preferred}]).
            int exit;
            var json = YaboCli.Run(exe, $"--list-versions {Quote(name)}", System.Threading.CancellationToken.None, out exit);
            if (exit != 0 || string.IsNullOrWhiteSpace(json))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Couldn't fetch the version list (no network, or the engine has no releases for this port).",
                    "Yabo Launcher");
                return;
            }

            List<GenericItemOption> options;
            List<string> tags;
            try
            {
                var arr = Newtonsoft.Json.Linq.JArray.Parse(json);
                tags = new List<string>();
                options = new List<GenericItemOption>();
                foreach (var r in arr)
                {
                    var tag = (string)r["tag"];
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        continue;
                    }
                    var flags = new List<string>();
                    if ((bool?)r["installed"] == true) { flags.Add("installed"); }
                    if ((bool?)r["latest"] == true) { flags.Add("latest"); }
                    if ((bool?)r["preferred"] == true) { flags.Add("preferred"); }
                    if ((bool?)r["prerelease"] == true) { flags.Add("prerelease"); }
                    tags.Add(tag);
                    options.Add(new GenericItemOption(tag, flags.Count > 0 ? string.Join(", ", flags) : "release"));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: failed to parse --list-versions output.");
                PlayniteApi.Dialogs.ShowMessage("Couldn't read the version list.", "Yabo Launcher");
                return;
            }

            if (options.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage("No releases are available for this port.", "Yabo Launcher");
                return;
            }

            var chosen = PlayniteApi.Dialogs.ChooseItemWithSearch(
                options,
                search => string.IsNullOrWhiteSpace(search)
                    ? options
                    : options.Where(o => o.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                string.Empty,
                $"Change version — {name}");
            if (chosen == null || string.IsNullOrWhiteSpace(chosen.Name))
            {
                return; // cancelled
            }

            PlayniteApi.Notifications.Add("yabo-installver-start",
                $"Installing \"{name}\" {chosen.Name}…", NotificationType.Info);
            RunEngineAsync(exe, $"--install-version {Quote(name)} {Quote(chosen.Name)}", (rc, output) =>
            {
                PlayniteApi.Notifications.Add("yabo-installver-done",
                    rc == 0
                        ? $"Installed {chosen.Name}."
                        : $"Installing {chosen.Name} failed — see the extension log.",
                    rc == 0 ? NotificationType.Info : NotificationType.Error);
            });
        }

        /// <summary>
        /// "Adopt existing install" for a GitHub-binary port the user already has on disk. Prompts for the
        /// install folder, then shells <c>--locate-install "&lt;name&gt;" "&lt;folder&gt;"</c> — the engine resolves the
        /// exe inside it, writes the InstallPath into apps.json, and re-checks status (no download, no move).
        /// On success the card is now Installed + GitHub-release update tracking is wired; we ask the user to
        /// Update Library so Playnite reflects the new installed state (the SDK exposes no per-game refresh).
        /// </summary>
        private void MenuAdoptInstall(string exe, string name)
        {
            var folder = PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return; // cancelled
            }

            PlayniteApi.Notifications.Add("yabo-adopt-start",
                $"Adopting \"{name}\" from {folder}…", NotificationType.Info);
            RunEngineAsync(exe, $"--locate-install {Quote(name)} {Quote(folder)}", (exit, output) =>
            {
                if (exit == 0)
                {
                    PlayniteApi.Notifications.Add("yabo-adopt-done",
                        $"Adopted \"{name}\". Run Library → Update Library to refresh its installed state.",
                        NotificationType.Info);
                }
                else
                {
                    Logger.Warn($"Yabo: --locate-install failed (exit {exit}). Output: {output}");
                    PlayniteApi.Notifications.Add("yabo-adopt-done",
                        $"Couldn't adopt \"{name}\" — the folder may not contain this port's files. See the extension log.",
                        NotificationType.Error);
                }
            });
        }

        /// <summary>
        /// "Pick executable" for a multi-exe GitHub-binary port. Asks the engine for the launchable exe
        /// candidates (<c>--list-exes "&lt;name&gt;"</c> → JSON <c>[{name, path, recommended}]</c>), lets the user
        /// choose one, and records it with <c>--set-exe "&lt;name&gt;" "&lt;exe path&gt;"</c> so subsequent launches use it.
        /// </summary>
        private void MenuPickExe(string exe, string name)
        {
            int exit;
            var json = YaboCli.Run(exe, $"--list-exes {Quote(name)}", System.Threading.CancellationToken.None, out exit);
            if (exit != 0 || string.IsNullOrWhiteSpace(json))
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "Couldn't list the executables. Adopt or install the game first so its folder exists.",
                    "Yabo Launcher");
                return;
            }

            var options = new List<GenericItemOption>();
            var pathByLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var arr = Newtonsoft.Json.Linq.JArray.Parse(json);
                foreach (var e in arr)
                {
                    var path = (string)e["path"];
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }
                    var exeName = (string)e["name"];
                    var label = string.IsNullOrWhiteSpace(exeName) ? path : exeName;
                    var recommended = (bool?)e["recommended"] == true;
                    // Disambiguate duplicate file names by appending the full path to the dictionary key.
                    var key = pathByLabel.ContainsKey(label) ? $"{label}  ({path})" : label;
                    pathByLabel[key] = path;
                    options.Add(new GenericItemOption(key, recommended ? "recommended" : path));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: failed to parse --list-exes output.");
                PlayniteApi.Dialogs.ShowMessage("Couldn't read the executable list.", "Yabo Launcher");
                return;
            }

            if (options.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage("No launchable executables were found in the install folder.", "Yabo Launcher");
                return;
            }

            var chosen = PlayniteApi.Dialogs.ChooseItemWithSearch(
                options,
                search => string.IsNullOrWhiteSpace(search)
                    ? options
                    : options.Where(o => o.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                string.Empty,
                $"Pick executable — {name}");
            if (chosen == null || !pathByLabel.TryGetValue(chosen.Name, out var exePath))
            {
                return; // cancelled
            }

            RunEngineAsync(exe, $"--set-exe {Quote(name)} {Quote(exePath)}", (rc, output) =>
            {
                PlayniteApi.Notifications.Add("yabo-setexe-done",
                    rc == 0
                        ? $"Set \"{name}\" to launch {System.IO.Path.GetFileName(exePath)}."
                        : "Yabo: setting the executable failed — see the extension log.",
                    rc == 0 ? NotificationType.Info : NotificationType.Error);
            });
        }

        /// <summary>
        /// "Configure enhancements" for Harbour-Masters / libultraship ports. Chosen approach (c): a simple
        /// dialog flow over the engine's curated config keys. We read the port's grouped config via
        /// <c>--get-config-json</c> (status/groups/{path,value}); the user picks a key, then types a new value,
        /// which is written in place with <c>--set-config-json "&lt;name&gt;" "&lt;dotted.path&gt;" "&lt;value&gt;"</c>.
        /// This avoids a full editor UI while still reaching every curated CVar. If the port hasn't been run yet
        /// (no JSON generated), we fall back to opening its install folder so the user can launch it once.
        /// </summary>
        private void MenuConfigureEnhancements(string exe, string name)
        {
            int exit;
            var json = YaboCli.Run(exe, $"--get-config-json {Quote(name)}", System.Threading.CancellationToken.None, out exit);
            if (exit != 0 || string.IsNullOrWhiteSpace(json))
            {
                PlayniteApi.Dialogs.ShowMessage("Couldn't read the port's config from the engine.", "Yabo Launcher");
                return;
            }

            Newtonsoft.Json.Linq.JObject root;
            try
            {
                root = Newtonsoft.Json.Linq.JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: failed to parse --get-config-json output.");
                PlayniteApi.Dialogs.ShowMessage("Couldn't read the port's config.", "Yabo Launcher");
                return;
            }

            var status = (string)root["status"];
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                // not_found / port_not_launched_yet → launch once to generate the JSON; open the folder to help.
                var res = PlayniteApi.Dialogs.ShowMessage(
                    "This port hasn't generated its config yet — run the game once, then try again.\n\n" +
                    "Open its install folder now?",
                    "Configure enhancements",
                    System.Windows.MessageBoxButton.YesNo);
                if (res == System.Windows.MessageBoxResult.Yes)
                {
                    MenuOpenFolder(exe, name);
                }
                return;
            }

            // Flatten the curated groups into "<Group> · <dotted.path>" options carrying their current value.
            var options = new List<GenericItemOption>();
            var pathByLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            var groups = root["groups"] as Newtonsoft.Json.Linq.JObject;
            if (groups != null)
            {
                foreach (var group in groups.Properties())
                {
                    if (!(group.Value is Newtonsoft.Json.Linq.JArray fields))
                    {
                        continue;
                    }
                    foreach (var f in fields)
                    {
                        var path = (string)f["path"];
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }
                        var value = f["value"];
                        var current = (value == null || value.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                            ? "(default)"
                            : value.ToString();
                        var label = $"{group.Name} · {path}";
                        pathByLabel[label] = path;
                        options.Add(new GenericItemOption(label, $"current: {current}"));
                    }
                }
            }

            if (options.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage("No editable config keys were found for this port.", "Yabo Launcher");
                return;
            }

            var chosen = PlayniteApi.Dialogs.ChooseItemWithSearch(
                options,
                search => string.IsNullOrWhiteSpace(search)
                    ? options
                    : options.Where(o => o.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                string.Empty,
                $"Configure enhancements — {name}");
            if (chosen == null || !pathByLabel.TryGetValue(chosen.Name, out var dottedPath))
            {
                return; // cancelled
            }

            var input = PlayniteApi.Dialogs.SelectString(
                $"New value for {dottedPath}\n\n({chosen.Description})\n\n" +
                "Tip: booleans are 1/0; resolutions and FOV are numbers.",
                "Configure enhancements",
                string.Empty);
            if (!input.Result || input.SelectedString == null)
            {
                return; // cancelled
            }

            var newValue = input.SelectedString.Trim();
            RunEngineAsync(exe, $"--set-config-json {Quote(name)} {Quote(dottedPath)} {Quote(newValue)}", (rc, output) =>
            {
                PlayniteApi.Notifications.Add("yabo-setconfig-done",
                    rc == 0
                        ? $"Set {dottedPath} = {newValue}."
                        : $"Setting {dottedPath} failed — see the extension log.",
                    rc == 0 ? NotificationType.Info : NotificationType.Error);
            });
        }

        // ----------------------------------------------------------------------------------
        // Import
        // ----------------------------------------------------------------------------------

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                Logger.Warn("Yabo: no engine exe configured; import yields nothing.");
                yield break;
            }

            // [feed sync] Pull the owner's RELEASED feed first, so a subscriber's library reflects the latest
            // "release" tag automatically on every Playnite library refresh. No-op/dormant when no feed is
            // configured (the owner's own install), and never blocks import on a failed/offline pull.
            YaboCli.SyncFeed(exe, args.CancelToken);

            var catalog = YaboCli.GetCatalog(exe, args.CancelToken);
            bool importHidden = SettingsViewModel?.Settings?.ImportHidden ?? false;
            foreach (var entry in catalog)
            {
                if (args.CancelToken.IsCancellationRequested)
                {
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(entry.FolderName) || string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue; // can't form a stable identity without folderName
                }

                // [yabo] Respect the engine's hidden/delisted flag: a port the user toggled out of the launcher
                // (sports, delisted IA packs) is NOT imported into Playnite — it stays out, not as a stray
                // "installable" tile. Flip the ImportHidden setting to pull them in anyway.
                if (entry.Hidden && !importHidden)
                {
                    continue;
                }

                yield return BuildMetadata(entry, exe);
            }
        }

        private GameMetadata BuildMetadata(YaboCatalogEntry entry, string exe)
        {
            var meta = new GameMetadata
            {
                // Stable identity — survives display-name renames. Playnite merges on PluginId + GameId.
                GameId = entry.FolderName,
                Name = entry.Name,
                Source = new MetadataNameProperty(SourceName),
                IsInstalled = entry.IsInstalled && !entry.IsExternal,
                GameActions = new List<GameAction>(),
                Links = new List<Link>()
            };

            // Layer 1 of cover preservation: write the curated cover directly onto the game at import.
            // MetadataFile accepts a URL (Playnite downloads it) or a local path.
            if (!string.IsNullOrWhiteSpace(entry.Cover))
            {
                meta.CoverImage = new MetadataFile(entry.Cover);
            }

            // Platform/category grouping (optional).
            if (!string.IsNullOrWhiteSpace(entry.Category))
            {
                meta.Platforms = new HashSet<MetadataProperty> { new MetadataNameProperty(entry.Category) };
            }

            // [conversion schema] Provenance LINKS — every source URL becomes a Playnite Link, so
            // right-click -> Links IS the provenance menu (GitHub repo / Internet Archive / direct download /
            // homepage). Only the WHAT is exported; the HOW (dataFiles / route / marriage) stays internal.
            // Shared with the feed-refresh path (BuildCuratedLinks) so the two can't drift. The external
            // "Get it" link is added by the IsExternal block below for import; the helper's own "Get it" is
            // only consumed by the refresh path (which merges by URL, so it never duplicates here).
            foreach (var link in BuildCuratedLinks(entry))
            {
                if (entry.IsExternal && string.Equals(link.Name, "Get it", StringComparison.Ordinal))
                {
                    continue; // added explicitly in the IsExternal block to keep import behaviour identical
                }
                meta.Links.Add(link);
            }

            // [conversion schema] Category -> Categories (the yabo shelf), alongside the existing Platforms
            // grouping. Provenance/state -> Tags so the library is filterable by source.
            if (!string.IsNullOrWhiteSpace(entry.Category))
            {
                meta.Categories = new HashSet<MetadataProperty> { new MetadataNameProperty(entry.Category) };
            }
            var tags = new HashSet<MetadataProperty>();
            if (entry.RohanLibrary) { tags.Add(new MetadataNameProperty("Rohan Pack")); }
            if (entry.Experimental) { tags.Add(new MetadataNameProperty("Experimental")); }
            // GitHub-release update tracking (GithubLauncher parity): the engine reports a waiting
            // update as status "UpdateAvailable" in --list-json; reflect it as a filterable Playnite Tag.
            if (entry.HasUpdate) { tags.Add(new MetadataNameProperty(UpdateAvailableTag)); }
            if (tags.Count > 0) { meta.Tags = tags; }

            if (entry.IsExternal)
            {
                // Link-out / dead-end port → a clean "virtual library" entry (the idea behind
                // iSplasher's Virtual Library, but catalog-driven for all dead-ends at once).
                // Mark it INSTALLED + make the URL the PLAY action so the card is full-color and
                // the primary button opens the download/store page — instead of a grayed
                // "not installed" card whose link is buried. There is still NO install/uninstall
                // controller (ResolveCliName returns null for URL actions → GetInstall/Uninstall/Play
                // all yield break), so Playnite never shows "installation implementation not available"
                // and there is no duplicate AutomaticPlayController.
                meta.GameActions.Add(new GameAction
                {
                    Name = "Open page",
                    Type = GameActionType.URL,
                    Path = entry.ExternalUrl,
                    IsPlayAction = true
                });
                meta.Links.Add(new Link("Get it", entry.ExternalUrl));
                meta.IsInstalled = true;
                return meta;
            }

            // Multi-game port (games[] length > 1): one play-action per inner game → Playnite Play dropdown.
            if (entry.Games != null && entry.Games.Count > 1)
            {
                foreach (var g in entry.Games)
                {
                    if (string.IsNullOrWhiteSpace(g.Label))
                    {
                        continue;
                    }

                    var romArg = string.IsNullOrWhiteSpace(g.Rom) ? string.Empty : $" --rom {g.Rom}";
                    meta.GameActions.Add(new GameAction
                    {
                        Name = g.Label,
                        Type = GameActionType.File,
                        Path = exe,
                        Arguments = $"--play \"{entry.Name}\"{romArg}",
                        IsPlayAction = true
                    });
                }
            }
            else if (IsSnesrevNative(entry.FolderName))
            {
                // snesrev-native single-game card: attach THREE explicit GameActions — Play plus the two
                // isnesrev editor actions. Because we add a static IsPlayAction here, GetPlayActions skips
                // its AutomaticPlayController for snesrev cards (so there's no duplicate Play button); the
                // play arguments stay identical to the controller's, so ResolveCliName still parses the
                // catalog identity out of the --play "<name>" string.
                meta.GameActions.Add(new GameAction
                {
                    Name = "Play",
                    Type = GameActionType.File,
                    Path = exe,
                    Arguments = $"--play \"{entry.Name}\"",
                    IsPlayAction = true
                });

                var isnesrev = ResolveIsnesrevPath();
                if (!string.IsNullOrWhiteSpace(isnesrev))
                {
                    // isnesrev opens straight to the requested tab for THIS game (keyed by folderName, the
                    // stable engine identity — never the renamable display name).
                    meta.GameActions.Add(new GameAction
                    {
                        Name = "Configure GLSL",
                        Type = GameActionType.File,
                        Path = isnesrev,
                        Arguments = $"--game \"{entry.FolderName}\" --tab shaders",
                        IsPlayAction = false
                    });
                    meta.GameActions.Add(new GameAction
                    {
                        Name = "Configure Controls",
                        Type = GameActionType.File,
                        Path = isnesrev,
                        Arguments = $"--game \"{entry.FolderName}\" --tab controls",
                        IsPlayAction = false
                    });
                }
            }
            else
            {
                // [dup-play fix] Single-game port: play is provided SOLELY by the AutomaticPlayController in
                // GetPlayActions (which also tracks playtime). We add NO static "Play" GameAction here — having
                // both showed TWO identical "Play" buttons in Playnite (the static one + the controller).
                // ResolveCliName falls back to game.Name, kept stable because the Name metadata source is
                // locked to "Yabo"/Store, so install/uninstall still resolve the catalog name.
            }

            return meta;
        }

        // ----------------------------------------------------------------------------------
        // Metadata refresh on feed update (#34)
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// After every library update (e.g. a feed sync via <c>--list-json</c>), re-apply the curated
        /// metadata — cover, provenance Links, Categories, Tags — to games that are ALREADY in the
        /// database, so curation changes (a swapped cover, a new IA link, a re-shelved category) propagate
        /// to existing cards instead of only landing on freshly-imported ones.
        ///
        /// Field locks: the Playnite SDK exposes no per-field lock to library plugins, so we never CLOBBER
        /// a user's manual edits. Every write is ADDITIVE / fill-only:
        ///   * Cover  — written only when the game currently has none (a user-chosen cover is left alone).
        ///   * Links  — curated links are merged in by URL; existing/user links are never removed.
        ///   * Categories / Tags — curated values are added when missing; user values are never removed.
        /// Games are matched by <c>GameId</c> (= folderName), the same identity the import path sets.
        /// All failures are swallowed so a bad feed never breaks Playnite's post-update pipeline.
        /// </summary>
        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            base.OnLibraryUpdated(args);

            try
            {
                var exe = ExePath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    return;
                }

                // Snapshot the curated catalog once and index it by folderName for O(1) lookups.
                var catalog = YaboCli.GetCatalog(exe, System.Threading.CancellationToken.None);
                if (catalog == null || catalog.Count == 0)
                {
                    return;
                }
                var byFolder = new Dictionary<string, YaboCatalogEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in catalog)
                {
                    if (!string.IsNullOrWhiteSpace(entry.FolderName) && !byFolder.ContainsKey(entry.FolderName))
                    {
                        byFolder[entry.FolderName] = entry;
                    }
                }

                int updated = 0;
                // GameIds the engine currently reports as having a waiting update — collected as we go so
                // the "N game(s) have updates" notification can be gated to once per change (see below).
                var updateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Only our own games (PluginId == Id), matched by GameId == folderName.
                var ours = PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList();
                foreach (var game in ours)
                {
                    if (string.IsNullOrWhiteSpace(game.GameId) ||
                        !byFolder.TryGetValue(game.GameId, out var entry))
                    {
                        continue;
                    }

                    if (entry.HasUpdate)
                    {
                        updateSet.Add(game.GameId);
                    }

                    if (ReapplyCuratedMetadata(game, entry))
                    {
                        PlayniteApi.Database.Games.Update(game);
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    Logger.Info($"Yabo: re-applied curated metadata to {updated} existing game(s) after feed update.");
                }

                NotifyUpdatesAvailable(updateSet);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: metadata refresh on library update failed (non-fatal).");
            }
        }

        /// <summary>
        /// Raise ONE non-blocking "N game(s) have updates" notification after a library update — but only
        /// when the set of games-with-updates actually CHANGED since the last sync (gated to once per change).
        /// The set of GameIds is persisted under the plugin user-data dir, so a sync that reports the same
        /// pending updates is silent, and the notification clears when updates are applied/disappear.
        /// All failures are swallowed (a notification is best-effort and must never break the update pipeline).
        /// </summary>
        private void NotifyUpdatesAvailable(HashSet<string> updateSet)
        {
            try
            {
                var userData = GetPluginUserDataPath();
                var previous = ReadLastUpdateSet(userData);

                // Unchanged set → nothing new to announce (don't re-nag every Update Library).
                if (updateSet.SetEquals(previous))
                {
                    return;
                }

                // Persist the new set first so a crash can't cause a re-announce of the same state.
                WriteLastUpdateSet(userData, updateSet);

                if (updateSet.Count == 0)
                {
                    // Updates were applied / cleared since last time — drop any stale notification, say nothing.
                    PlayniteApi.Notifications.Remove("yabo-updates-available");
                    return;
                }

                var body = updateSet.Count == 1
                    ? "1 game has an update available. Right-click it → Yabo → Update."
                    : $"{updateSet.Count} games have updates available. Right-click a game → Yabo → Update.";
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "yabo-updates-available",
                    body,
                    NotificationType.Info));
                Logger.Info($"Yabo: announced {updateSet.Count} game(s) with updates.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: update-available notification failed (non-fatal).");
            }
        }

        /// <summary>Read the persisted set of GameIds we last notified as having updates (newline-delimited).</summary>
        private static HashSet<string> ReadLastUpdateSet(string pluginUserDataPath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(pluginUserDataPath, LastUpdateSetFileName);
                if (!File.Exists(path))
                {
                    return set;
                }
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw?.Trim();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        set.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: failed to read last update-set marker.");
            }
            return set;
        }

        /// <summary>Persist the current set of GameIds-with-updates so the notification fires once per change.</summary>
        private static void WriteLastUpdateSet(string pluginUserDataPath, HashSet<string> updateSet)
        {
            try
            {
                Directory.CreateDirectory(pluginUserDataPath);
                var path = Path.Combine(pluginUserDataPath, LastUpdateSetFileName);
                File.WriteAllLines(path, updateSet);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: failed to persist last update-set marker.");
            }
        }

        /// <summary>
        /// Apply the curated fields from <paramref name="entry"/> onto an existing <paramref name="game"/>
        /// in an additive, non-clobbering way (see <see cref="OnLibraryUpdated"/>). Returns true when any
        /// field was actually changed (so the caller only writes back dirtied games).
        /// </summary>
        private bool ReapplyCuratedMetadata(Game game, YaboCatalogEntry entry)
        {
            var changed = false;

            // --- Cover (fill-only) -------------------------------------------------------------------
            if (string.IsNullOrWhiteSpace(game.CoverImage) && !string.IsNullOrWhiteSpace(entry.Cover))
            {
                try
                {
                    // Database.AddFile copies/downloads the source into the game's media store and returns a
                    // DB-relative path; works for both http(s) URLs and local paths.
                    var added = PlayniteApi.Database.AddFile(entry.Cover, game.Id);
                    if (!string.IsNullOrWhiteSpace(added))
                    {
                        game.CoverImage = added;
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Yabo: failed to apply cover for '{game.GameId}'.");
                }
            }

            // --- Provenance Links (merge by URL) -----------------------------------------------------
            // Build the curated link set the same way BuildMetadata does, then add any that are missing.
            var curatedLinks = BuildCuratedLinks(entry);
            if (curatedLinks.Count > 0)
            {
                if (game.Links == null)
                {
                    game.Links = new System.Collections.ObjectModel.ObservableCollection<Link>();
                }
                foreach (var link in curatedLinks)
                {
                    var exists = game.Links.Any(l =>
                        l != null && string.Equals(l.Url, link.Url, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                    {
                        game.Links.Add(link);
                        changed = true;
                    }
                }
            }

            // --- Category (additive) -----------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(entry.Category))
            {
                var cat = PlayniteApi.Database.Categories.Add(entry.Category);
                if (cat != null)
                {
                    if (game.CategoryIds == null)
                    {
                        game.CategoryIds = new List<Guid>();
                    }
                    if (!game.CategoryIds.Contains(cat.Id))
                    {
                        game.CategoryIds.Add(cat.Id);
                        changed = true;
                    }
                }
            }

            // --- Tags (additive) ---------------------------------------------------------------------
            // Same provenance/state tags the import path sets.
            var curatedTagNames = new List<string>();
            if (entry.RohanLibrary) { curatedTagNames.Add("Rohan Pack"); }
            if (entry.Experimental) { curatedTagNames.Add("Experimental"); }
            foreach (var tagName in curatedTagNames)
            {
                var tag = PlayniteApi.Database.Tags.Add(tagName);
                if (tag == null)
                {
                    continue;
                }
                if (game.TagIds == null)
                {
                    game.TagIds = new List<Guid>();
                }
                if (!game.TagIds.Contains(tag.Id))
                {
                    game.TagIds.Add(tag.Id);
                    changed = true;
                }
            }

            // --- "Update available" tag (toggle to match live status) --------------------------------
            // Unlike the provenance tags above this one is a live state: add it when the engine reports a
            // waiting update, and REMOVE it when the update is gone (e.g. after the user updated the game),
            // so the tag never sticks around stale. This is the only tag we remove, and only our own.
            if (UpdateAvailableTagToggle(game, entry.HasUpdate))
            {
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Ensure the <see cref="UpdateAvailableTag"/> is present on <paramref name="game"/> iff
        /// <paramref name="hasUpdate"/>. Returns true when the tag set actually changed. Only ever
        /// adds/removes the single "Update available" tag — never touches a user's other tags.
        /// </summary>
        private bool UpdateAvailableTagToggle(Game game, bool hasUpdate)
        {
            var tag = PlayniteApi.Database.Tags.Add(UpdateAvailableTag);
            if (tag == null)
            {
                return false;
            }

            var has = game.TagIds != null && game.TagIds.Contains(tag.Id);
            if (hasUpdate && !has)
            {
                if (game.TagIds == null)
                {
                    game.TagIds = new List<Guid>();
                }
                game.TagIds.Add(tag.Id);
                return true;
            }
            if (!hasUpdate && has)
            {
                game.TagIds.Remove(tag.Id);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Build the provenance Links for a catalog entry — the same set <see cref="BuildMetadata"/> emits
        /// at import (Source / Internet Archive / Direct download / Homepage, plus the external "Get it"
        /// link for link-out entries). Centralised so the import and refresh paths can't drift.
        /// </summary>
        private static List<Link> BuildCuratedLinks(YaboCatalogEntry entry)
        {
            var links = new List<Link>();

            if (!string.IsNullOrWhiteSpace(entry.Repository))
            {
                var repoUrl = entry.Repository.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? entry.Repository
                    : $"https://github.com/{entry.Repository}";
                links.Add(new Link("Source (GitHub)", repoUrl));
            }
            if (!string.IsNullOrWhiteSpace(entry.IaIdentifier))
            {
                links.Add(new Link("Internet Archive", $"https://archive.org/details/{entry.IaIdentifier}"));
            }
            if (!string.IsNullOrWhiteSpace(entry.ContentUrl) &&
                entry.ContentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(new Link("Direct download", entry.ContentUrl));
            }
            if (!string.IsNullOrWhiteSpace(entry.Website) &&
                entry.Website.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(new Link("Homepage", entry.Website));
            }
            if (entry.IsExternal && !string.IsNullOrWhiteSpace(entry.ExternalUrl))
            {
                links.Add(new Link("Get it", entry.ExternalUrl));
            }

            return links;
        }

        // ----------------------------------------------------------------------------------
        // Controllers (install / uninstall / play)
        // ----------------------------------------------------------------------------------

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            // External link-outs aren't installed by us.
            var name = ResolveCliName(args.Game);
            if (name == null)
            {
                yield break;
            }

            yield return new YaboInstallController(args.Game, this, name);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            var name = ResolveCliName(args.Game);
            if (name == null)
            {
                yield break;
            }

            yield return new YaboUninstallController(args.Game, this, name);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            var exe = ExePath;
            var name = ResolveCliName(args.Game);
            if (string.IsNullOrWhiteSpace(exe) || name == null)
            {
                yield break;
            }

            // Single-game ports get ONE AutomaticPlayController (play + playtime tracking) — the sole play
            // action (BuildMetadata no longer adds a static one). Multi-game ports rely on their per-game
            // GameActions (the Play dropdown) instead, because the active --rom is chosen by which dropdown
            // entry the user clicked, so they yield no controller here.
            var game = args.Game;
            var isMultiGame = (game.GameActions?.Count ?? 0) > 1;
            if (isMultiGame)
            {
                yield break;
            }

            // snesrev-native cards always carry their OWN static Play GameAction (added in BuildMetadata
            // alongside the Configure GLSL / Configure Controls actions). Adding a controller here too would
            // show a duplicate Play button — so skip it. (Hit only when isnesrev wasn't resolved, leaving the
            // card with a single static Play action; otherwise the Count > 1 check above already short-circuits.)
            if (IsSnesrevNative(game.GameId))
            {
                yield break;
            }

            yield return new AutomaticPlayController(game)
            {
                Name = "Play",
                // NOTE (owner-verify): SDK enum name. Design doc says AutomaticPlayActionType.File;
                // some SDK versions expose this as the same. If it doesn't resolve, it's the
                // AutomaticPlayActionType enum on AutomaticPlayController.Type.
                Type = AutomaticPlayActionType.File,
                TrackingMode = TrackingMode.Process,
                Path = exe,
                Arguments = $"--play \"{name}\""
            };
        }

        /// <summary>
        /// Resolve the CLI "name" (catalog identity) for a Playnite game. The catalog name is
        /// persisted at import inside every play GameAction's arguments as <c>--play "&lt;name&gt;"</c>
        /// (see <see cref="BuildMetadata"/>), so we extract it from there. This survives the user
        /// renaming the game's display name in Playnite — we never rely on <c>game.Name</c> for the
        /// CLI identity. Falls back to <c>game.Name</c> only if no parseable action exists (e.g. a
        /// game whose actions were edited away). Returns null for external link-outs (URL actions),
        /// which have no install/uninstall/play-via-CLI.
        /// </summary>
        private string ResolveCliName(Game game)
        {
            // External link-out actions are URL type — skip CLI controllers for those.
            if (game.GameActions != null)
            {
                foreach (var a in game.GameActions)
                {
                    if (a.Type == GameActionType.URL)
                    {
                        return null;
                    }
                }

                // Pull the catalog name back out of the persisted --play "<name>" argument string.
                // This is the stable CLI identity, independent of the display name.
                foreach (var a in game.GameActions)
                {
                    var fromArgs = ExtractPlayName(a?.Arguments);
                    if (fromArgs != null)
                    {
                        return fromArgs;
                    }
                }
            }

            // Last resort: at import display name == catalog name, so this only differs after a
            // rename combined with the play action having been removed/edited.
            return game.Name;
        }

        /// <summary>
        /// Extract <c>&lt;name&gt;</c> from a <c>--play "&lt;name&gt;"</c> argument string. Tolerates the
        /// trailing <c>--rom</c> token used by multi-game ports. Returns null if the string isn't a
        /// recognizable --play invocation.
        /// </summary>
        internal static string ExtractPlayName(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return null;
            }

            const string flag = "--play";
            var idx = arguments.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }

            var rest = arguments.Substring(idx + flag.Length).TrimStart();
            if (rest.Length == 0)
            {
                return null;
            }

            // Quoted form: --play "Some Name"
            if (rest[0] == '"')
            {
                var end = rest.IndexOf('"', 1);
                if (end > 1)
                {
                    return rest.Substring(1, end - 1);
                }
                return null;
            }

            // Unquoted form: --play Name (take the first whitespace-delimited token).
            var sp = rest.IndexOf(' ');
            var token = sp < 0 ? rest : rest.Substring(0, sp);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        // ----------------------------------------------------------------------------------
        // Metadata downloader (locks our curated cover as the "Store" source)
        // ----------------------------------------------------------------------------------

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return new YaboMetadataProvider(this);
        }

        // ----------------------------------------------------------------------------------
        // Settings
        // ----------------------------------------------------------------------------------

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return SettingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new YaboLibrarySettingsView();
        }
    }
}
