using System;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace RohanKarPlaynite
{
    /// <summary>
    /// Install: runs the launcher with --install &lt;id&gt; and waits. An item that needs a
    /// choice opens the launcher's window instead; the install counts as done as soon
    /// as the export shows it installed, window open or not.
    /// </summary>
    public class RohanKarInstallController : InstallController
    {
        private readonly RohanKarLibraryPlugin plugin;
        private readonly string exportId;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        public RohanKarInstallController(Game game, RohanKarLibraryPlugin plugin, string exportId) : base(game)
        {
            this.plugin = plugin;
            this.exportId = exportId;
            Name = "Install with y4bo";
        }

        public override void Install(InstallActionArgs args)
        {
            Task.Run(() =>
            {
                try
                {
                    using (var p = LauncherCli.Start(plugin.Settings.EffectiveLauncherExePath, "install", exportId))
                    {
                        var ok = LauncherCli.Wait(p, () => plugin.FindRecord(exportId)?.Installed == true, cts.Token);
                        var record = plugin.FindRecord(exportId);
                        if (ok && record?.Installed == true)
                        {
                            InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = record.InstallDir }));
                            plugin.ScheduleApply(1500);   // the Play action, once Playnite has marked it installed
                            return;
                        }
                    }
                    plugin.Notify("install-" + exportId, $"y4bo could not install {Game.Name}. Check the launcher for details.");
                }
                catch (Exception e)
                {
                    plugin.Notify("install-" + exportId, $"y4bo could not install {Game.Name}: {e.Message}");
                }
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
            });
        }

        public override void Dispose()
        {
            cts.Cancel();
            base.Dispose();
        }
    }

    /// <summary>Uninstall: runs the launcher with --uninstall &lt;id&gt;. The launcher keeps the entry.</summary>
    public class RohanKarUninstallController : UninstallController
    {
        private readonly RohanKarLibraryPlugin plugin;
        private readonly string exportId;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        public RohanKarUninstallController(Game game, RohanKarLibraryPlugin plugin, string exportId) : base(game)
        {
            this.plugin = plugin;
            this.exportId = exportId;
            Name = "Uninstall with y4bo";
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            Task.Run(() =>
            {
                try
                {
                    using (var p = LauncherCli.Start(plugin.Settings.EffectiveLauncherExePath, "uninstall", exportId))
                    {
                        if (LauncherCli.Wait(p, () => false, cts.Token))
                        {
                            InvokeOnUninstalled(new GameUninstalledEventArgs());
                            return;
                        }
                    }
                    plugin.Notify("uninstall-" + exportId, $"y4bo could not uninstall {Game.Name}. Check the launcher for details.");
                }
                catch (Exception e)
                {
                    plugin.Notify("uninstall-" + exportId, $"y4bo could not uninstall {Game.Name}: {e.Message}");
                }
                plugin.ClearUninstalling(Game.Id);
            });
        }

        public override void Dispose()
        {
            cts.Cancel();
            base.Dispose();
        }
    }

    /// <summary>
    /// Play on a game the launcher has no exe for (not installed, or no exe picked):
    /// --launch &lt;id&gt; opens the launcher on that item. Nothing is tracked.
    /// </summary>
    public class RohanKarOpenInLauncherController : PlayController
    {
        private readonly RohanKarLibraryPlugin plugin;
        private readonly string exportId;

        public RohanKarOpenInLauncherController(Game game, RohanKarLibraryPlugin plugin, string exportId) : base(game)
        {
            this.plugin = plugin;
            this.exportId = exportId;
            Name = "Open in y4bo";
        }

        public override void Play(PlayActionArgs args)
        {
            try
            {
                LauncherCli.Start(plugin.Settings.EffectiveLauncherExePath, "launch", exportId).Dispose();
            }
            catch (Exception e)
            {
                plugin.Notify("launch-" + exportId, $"y4bo could not open {Game.Name}: {e.Message}");
            }
            InvokeOnStopped(new GameStoppedEventArgs());
        }
    }
}
