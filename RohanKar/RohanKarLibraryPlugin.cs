using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace RohanKarPlaynite
{
    /// <summary>
    /// Imports RohanKar's installed games into Playnite.
    ///
    /// RohanKar keeps a SQLite library at %APPDATA%\rohankar-launcher\library.db,
    /// and the obvious design is to read it. That design does not work: on a real
    /// install every table in that database is EMPTY — games, collections,
    /// collection_games, all zero rows — while nineteen games sit installed on
    /// disk. The database only records what RohanKar itself installed, so it says
    /// nothing about a library assembled any other way.
    ///
    /// So this scans the install folder instead, which is the actual source of
    /// truth. The path comes from RohanKar's own settings.json rather than being
    /// hardcoded, so it follows the user's configuration.
    ///
    /// Folders are named "(id)Title" and hold anywhere from zero to a dozen
    /// executables; picking the right one is delegated to <see cref="ExePicker"/>,
    /// which was tuned against this real library. Runners-up are kept and exposed
    /// as secondary Playnite actions, because no heuristic gets every repack right
    /// and a wrong guess should be one right-click to fix.
    /// </summary>
    public class RohanKarLibraryPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private static readonly Regex FolderName = new Regex(@"^\((?<id>\d+)\)(?<title>.+)$",
            RegexOptions.Compiled);

        public override Guid Id { get; } = Guid.Parse("ecc7bf2f-5416-49d4-88c6-648397ed2668");
        public override string Name => "RohanKar";
        public override LibraryClient Client { get; } = new RohanKarClient();

        public RohanKarLibraryPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties { HasSettings = false };
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new List<GameMetadata>();

            string root = ResolveInstallPath();
            if (root == null || !Directory.Exists(root))
            {
                logger.Warn("RohanKar: install path not found (" + (root ?? "unset") + ")");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "rohankar-no-path",
                    "RohanKar: could not find the install folder. Set an install path in " +
                    "RohanKar, or check %APPDATA%\\rohankar-launcher\\settings.json.",
                    NotificationType.Error));
                return games;
            }

            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception ex)
            {
                logger.Error(ex, "RohanKar: cannot read " + root);
                return games;
            }

            int noExe = 0;

            foreach (var dir in dirs)
            {
                string folder = Path.GetFileName(dir);
                var m = FolderName.Match(folder);

                // Folders not matching "(id)Title" are still imported — a library the
                // user has organised by hand should not silently vanish — they just
                // key off the folder name instead of an id.
                string id = m.Success ? m.Groups["id"].Value : folder;
                string title = m.Success ? m.Groups["title"].Value.Trim() : folder;

                title = RefineTitle(dir, title);

                var ranked = ExePicker.Rank(dir, title);
                var playable = ranked.Where(c => !c.Excluded).ToList();

                var meta = new GameMetadata
                {
                    Name = title,
                    GameId = id,
                    Source = new MetadataNameProperty("RohanKar"),
                    InstallDirectory = dir,
                    IsInstalled = playable.Count > 0,
                };

                if (playable.Count > 0)
                {
                    var best = playable[0];
                    var actions = new List<GameAction>
                    {
                        new GameAction
                        {
                            Type = GameActionType.File,
                            Path = best.FullPath,
                            WorkingDir = Path.GetDirectoryName(best.FullPath),
                            IsPlayAction = true,
                            Name = "Play",
                        }
                    };

                    // Every other real executable becomes a named secondary action, so a
                    // mis-pick is fixable from the game's context menu without touching
                    // the heuristic. Capped, because a few repacks carry a dozen.
                    foreach (var alt in playable.Skip(1).Take(5))
                    {
                        actions.Add(new GameAction
                        {
                            Type = GameActionType.File,
                            Path = alt.FullPath,
                            WorkingDir = Path.GetDirectoryName(alt.FullPath),
                            IsPlayAction = false,
                            Name = Path.GetFileName(alt.FullPath),
                        });
                    }

                    meta.GameActions = actions;
                }
                else
                {
                    // Imported, but NOT installed: the folder exists and is worth seeing,
                    // and marking it installed would hand the user a dead Play button.
                    noExe++;
                    logger.Info("RohanKar: no launchable exe in " + folder);
                }

                try
                {
                    meta.InstallSize = (ulong)DirectorySize(dir);
                }
                catch
                {
                    // Size is a nicety; a permissions hiccup must not lose the game.
                }

                games.Add(meta);
            }

            logger.Info($"RohanKar: imported {games.Count} games ({noExe} with no launchable exe).");

            if (noExe > 0)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "rohankar-no-exe",
                    $"RohanKar: {noExe} of {games.Count} folders contain no launchable " +
                    "executable (empty, or only installers). They were imported as not installed.",
                    NotificationType.Info));
            }

            return games;
        }

        /// <summary>
        /// Some repack folders carry a placeholder title — one in this library is
        /// literally called "Underrated Gem" — while the folder inside names the
        /// real game. When the outer folder holds exactly one meaningful subfolder
        /// and no executable matches the outer title, the inner name is better.
        /// Deliberately narrow: it must not rewrite titles that are already right.
        /// </summary>
        private static string RefineTitle(string dir, string title)
        {
            try
            {
                // Repacks nest under a "Files" wrapper; step through it if present.
                string probe = dir;
                var wrapper = Path.Combine(dir, "Files");
                if (Directory.Exists(wrapper)) probe = wrapper;

                var subs = Directory.GetDirectories(probe);
                if (subs.Length != 1) return title;

                string inner = Path.GetFileName(subs[0]);
                if (string.IsNullOrWhiteSpace(inner)) return title;

                string a = Regex.Replace(inner.ToLowerInvariant(), "[^a-z0-9]", "");
                string b = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]", "");
                if (a == b || a.Contains(b) || b.Contains(a)) return title;

                // Only override when the outer title has no executable backing it up.
                var ranked = ExePicker.Rank(dir, title).Where(c => !c.Excluded).ToList();
                if (ranked.Count > 0) return title;

                return inner;
            }
            catch
            {
                return title;
            }
        }

        private static long DirectorySize(string dir)
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        }

        /// <summary>
        /// RohanKar's own settings.json is authoritative; the default is only a
        /// fallback for a fresh install that has not been configured yet.
        /// </summary>
        private static string ResolveInstallPath()
        {
            try
            {
                var settings = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "rohankar-launcher", "settings.json");

                if (File.Exists(settings))
                {
                    var cfg = JsonConvert.DeserializeObject<RohanKarSettings>(File.ReadAllText(settings));
                    if (!string.IsNullOrWhiteSpace(cfg?.InstallPath))
                    {
                        return cfg.InstallPath;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn("RohanKar: could not read settings.json — " + ex.Message);
            }

            return null;
        }
    }

    public class RohanKarClient : LibraryClient
    {
        public override bool IsInstalled => ExePath != null;
        public override string Icon => null;

        private static string ExePath
        {
            get
            {
                var p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "rohankar-launcher", "RohanKar Launcher.exe");
                if (File.Exists(p)) return p;

                var dir = Path.GetDirectoryName(p);
                if (Directory.Exists(dir))
                {
                    // That folder also contains "Uninstall RohanKar Launcher.exe", and
                    // GetFiles order is not guaranteed, so an unfiltered FirstOrDefault
                    // can hand Playnite the uninstaller as the client.
                    return Directory.GetFiles(dir, "*.exe")
                        .FirstOrDefault(f => Path.GetFileName(f)
                            .IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) < 0);
                }
                return null;
            }
        }

        public override void Open()
        {
            var exe = ExePath;
            if (exe != null) Process.Start(exe);
        }
    }

    public class RohanKarSettings
    {
        [JsonProperty("installPath")] public string InstallPath { get; set; }
        [JsonProperty("downloadPath")] public string DownloadPath { get; set; }
    }
}
