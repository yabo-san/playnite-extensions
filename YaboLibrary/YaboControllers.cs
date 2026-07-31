using System;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace YaboLibrary
{
    /// <summary>
    /// Install controller: runs <c>--download "&lt;name&gt;"</c>, then reports the install back to
    /// Playnite via <see cref="InvokeOnInstalled"/> with the resolved install directory.
    /// Playnite owns installed-state + dir after that.
    /// </summary>
    public class YaboInstallController : InstallController
    {
        private readonly YaboLibraryPlugin plugin;
        private readonly string yaboName;        // the catalog "name" (CLI identity for actions)
        private CancellationTokenSource cts;

        public YaboInstallController(Playnite.SDK.Models.Game game, YaboLibraryPlugin plugin, string yaboName)
            : base(game)
        {
            this.plugin = plugin;
            this.yaboName = yaboName;
            Name = "Install via Yabo Launcher";
        }

        public override void Install(InstallActionArgs args)
        {
            var exe = plugin.SettingsViewModel?.Settings?.ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                plugin.PlayniteApi.Notifications.Add("yabo-install-noexe",
                    "Yabo Launcher: set the engine exe path in the plugin settings before installing.",
                    NotificationType.Error);
                return;
            }

            cts = new CancellationTokenSource();
            Task.Run(() =>
            {
                YaboCli.Run(exe, $"--download \"{yaboName}\"", cts.Token, out var exit);

                // Resolve the install dir via the engine (--path prints the exe path; its directory is the install dir).
                var installDir = ResolveInstallDir(exe, cts.Token);

                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData
                {
                    InstallDirectory = installDir
                }));
            });
        }

        private string ResolveInstallDir(string exe, CancellationToken token)
        {
            // Best-effort: --path "<name>" prints the launchable exe; its folder is the install dir.
            try
            {
                var pathOut = YaboCli.Run(exe, $"--path \"{yaboName}\"", token, out var exit);
                var line = (pathOut ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(line) && System.IO.File.Exists(line))
                {
                    return System.IO.Path.GetDirectoryName(line);
                }
            }
            catch { /* fall through */ }
            return null; // Playnite tolerates a null InstallDirectory.
        }

        public override void Dispose()
        {
            cts?.Cancel();
            base.Dispose();
        }
    }

    /// <summary>
    /// Uninstall controller: runs <c>--uninstall "&lt;name&gt;"</c>, then reports it back via
    /// <see cref="InvokeOnUninstalled"/>.
    /// </summary>
    public class YaboUninstallController : UninstallController
    {
        private readonly YaboLibraryPlugin plugin;
        private readonly string yaboName;
        private CancellationTokenSource cts;

        public YaboUninstallController(Playnite.SDK.Models.Game game, YaboLibraryPlugin plugin, string yaboName)
            : base(game)
        {
            this.plugin = plugin;
            this.yaboName = yaboName;
            Name = "Uninstall via Yabo Launcher";
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var exe = plugin.SettingsViewModel?.Settings?.ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                plugin.PlayniteApi.Notifications.Add("yabo-uninstall-noexe",
                    "Yabo Launcher: set the engine exe path in the plugin settings before uninstalling.",
                    NotificationType.Error);
                return;
            }

            cts = new CancellationTokenSource();
            Task.Run(() =>
            {
                YaboCli.Run(exe, $"--uninstall \"{yaboName}\"", cts.Token, out var exit);
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            });
        }

        public override void Dispose()
        {
            cts?.Cancel();
            base.Dispose();
        }
    }
}
