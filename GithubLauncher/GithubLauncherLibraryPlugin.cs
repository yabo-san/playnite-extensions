using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GithubLauncherPlaynite
{
    /// <summary>
    /// Imports SirDiabo's GithubLauncher library into Playnite.
    ///
    /// GithubLauncher ships a documented COMMAND LINE, which is a far better
    /// integration surface than its on-disk files:
    ///
    ///     Usage: GithubLauncher [command] [game name]
    ///       -l, --list              List all available games
    ///       -d, --download &lt;name&gt;   Download and install a game
    ///       -r, --run &lt;name&gt;        Run a game (auto-updates if needed)
    ///       -u, --update            Update all installed games
    ///
    /// So enumeration is `--list` and launching is `--run &lt;name&gt;`, which also
    /// keeps the game updated — something a raw path to an .exe cannot do. This is
    /// why the plugin does not go near games.json.
    ///
    /// HONEST STATUS: the CLI itself is verified — `--list` runs and exits 1 with
    /// "ERROR: No games found in library." on an empty library. What is NOT
    /// verified is the shape of `--list` output when games ARE present, because
    /// this machine's library is empty and the source could not be read. The
    /// parsing is therefore deliberately conservative, lives in <see cref="ListParser"/>
    /// so it can be run standalone against captured output, and the RAW output is
    /// logged at Info level so the first real run reveals the true format
    /// immediately rather than failing silently. That is the one part expected to
    /// need correction.
    /// </summary>
    public class GithubLauncherLibraryPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("6c0f1a92-64f7-4d3a-9f0e-2a7f5b1d8c34");
        public override string Name => "GithubLauncher";
        public override LibraryClient Client { get; } = new GithubLauncherClient();


        public GithubLauncherLibraryPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties { HasSettings = false };
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new List<GameMetadata>();

            string exe = FindLauncher();
            if (exe == null)
            {
                logger.Warn("GithubLauncher: executable not found.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "ghl-missing",
                    "GithubLauncher: could not find GithubLauncher.exe. Expected it under " +
                    "~/.local/share/launchers/github-launcher or on PATH.",
                    NotificationType.Error));
                return games;
            }

            string output;
            try
            {
                output = RunCli(exe, "--list", 60000);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GithubLauncher: --list failed.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "ghl-list-failed", "GithubLauncher: could not list games — " + ex.Message,
                    NotificationType.Error));
                return games;
            }

            // Deliberate: this is the one unverified surface, so make the evidence
            // available the moment a real library exists.
            logger.Info("GithubLauncher: raw --list output follows:\n" + output);

            if (output.IndexOf("No games found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger.Info("GithubLauncher: library is empty.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "ghl-empty",
                    "GithubLauncher: its library is empty — add a game in GithubLauncher, " +
                    "then refresh.",
                    NotificationType.Info));
                return games;
            }

            foreach (var title in ListParser.Parse(output))
            {
                games.Add(new GameMetadata
                {
                    Name = title,
                    GameId = title,   // --run takes the NAME, so the name is the id
                    Source = new MetadataNameProperty("GithubLauncher"),
                    IsInstalled = true,
                    GameActions = new List<GameAction>
                    {
                        new GameAction
                        {
                            Type = GameActionType.File,
                            Path = exe,
                            Arguments = "--run \"" + title + "\"",
                            WorkingDir = Path.GetDirectoryName(exe),
                            IsPlayAction = true,
                            // --run auto-updates before launching, which a direct path
                            // to the game's own exe would skip.
                            Name = "Play (via GithubLauncher)",
                        }
                    }
                });
            }

            logger.Info($"GithubLauncher: imported {games.Count} games.");

            if (games.Count == 0)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "ghl-parse",
                    "GithubLauncher: its library is not empty but no games could be read " +
                    "from the output. The raw output is in the Playnite log — the list " +
                    "format needs confirming.",
                    NotificationType.Error));
            }

            return games;
        }


        private static string RunCli(string exe, string cliArgs, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = cliArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };

            using (var p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("GithubLauncher did not respond within "
                                        + (timeoutMs / 1000) + "s.");
                }

                // An empty library exits 1 with a useful message on stdout, so a
                // nonzero exit is NOT on its own an error worth throwing on.
                return string.IsNullOrWhiteSpace(so) ? se : so;
            }
        }

        internal static string FindLauncher()
        {
            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             ".local", "share", "launchers", "github-launcher", "GithubLauncher.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "GithubLauncher", "GithubLauncher.exe"),
            };

            var onPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(onPath.Split(';')
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p =>
                {
                    try { return Path.Combine(p.Trim(), "GithubLauncher.exe"); }
                    catch { return null; }
                })
                .Where(p => p != null));

            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public class GithubLauncherClient : LibraryClient
    {
        public override bool IsInstalled => GithubLauncherLibraryPlugin.FindLauncher() != null;
        public override string Icon => null;

        public override void Open()
        {
            var exe = GithubLauncherLibraryPlugin.FindLauncher();
            if (exe != null) Process.Start(exe);
        }
    }
}

