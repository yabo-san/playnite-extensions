using System;
using System.IO;
using System.Linq;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using Yabo.Shared;

namespace DropPlaynite
{
    /// <summary>
    /// "Send to Drop": copy a game's install folder into the Drop library and import
    /// it, using the two admin endpoints a human would click through
    /// (<c>admin/import/game</c>, then <c>admin/import/version</c>).
    ///
    /// Drop's filesystem library (<c>library/providers/filesystem.ts</c>) is
    /// <c>&lt;root&gt;/&lt;game folder&gt;/&lt;version folder&gt;/files</c>, so that is the layout
    /// written. The game import is a server-side task; the game id it produces is not
    /// in the response, so this polls the library for the name until it appears, then
    /// imports the version against that id.
    /// </summary>
    internal static class DropSend
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static void Run(DropLibraryPlugin plugin, Game game)
        {
            var settings = plugin.Settings;
            var api = plugin.PlayniteApi;
            var source = game.InstallDirectory;
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                api.Dialogs.ShowErrorMessage("This game has no install folder to send.", "Send to Drop");
                return;
            }

            var folderName = DropLibraryPlugin.Sanitise(game.Name);
            var versionFolder = "v" + DateTime.Now.ToString("yyyyMMdd");
            var gameTarget = Path.Combine(settings.LibrarySharePath, folderName);
            var target = Path.Combine(gameTarget, versionFolder);

            if (Directory.Exists(target))
            {
                var answer = api.Dialogs.ShowMessage(
                    $"{target} already exists. Copy over it? (No files are deleted; changed files are replaced.)",
                    "Send to Drop", System.Windows.MessageBoxButton.YesNo);
                if (answer != System.Windows.MessageBoxResult.Yes) return;
            }

            var launch = GuessLaunch(game, source);

            api.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = 3;
                try
                {
                    progress.Text = $"Copying {game.Name} to the Drop library...";
                    CopyTree(source, target, progress.CancelToken);
                    progress.CurrentProgressValue = 1;

                    var admin = DropAuth.AdminHeader(settings);
                    var client = plugin.Api();

                    progress.Text = "Importing the game in Drop...";
                    var libraryId = settings.LibraryId;
                    if (string.IsNullOrWhiteSpace(libraryId))
                    {
                        var pending = client.AdminUnimported(admin);
                        libraryId = pending.FirstOrDefault(p => string.Equals(p.Path, folderName, StringComparison.OrdinalIgnoreCase))?.LibraryId
                                    ?? pending.FirstOrDefault()?.LibraryId;
                    }
                    if (string.IsNullOrWhiteSpace(libraryId))
                    {
                        throw new InvalidOperationException("Drop lists no library the folder could belong to. Set the library id in settings.");
                    }

                    // The game may already exist (a second version of something sent before).
                    var existing = FindByName(client, plugin, game.Name);
                    if (existing == null)
                    {
                        client.AdminImportGame(admin, libraryId, folderName);
                        existing = WaitForGame(client, plugin, game.Name, TimeSpan.FromSeconds(60), progress.CancelToken);
                    }
                    progress.CurrentProgressValue = 2;

                    if (existing == null)
                    {
                        RememberUpload(plugin, game.Name, null);
                        api.Notifications.Add("drop-send-partial-" + game.Id,
                            $"Drop: {game.Name} was copied and the game import was started, but the game has not appeared yet. Open Drop's admin, Library, Import to finish the version ({versionFolder}).",
                            NotificationType.Info);
                        return;
                    }

                    progress.Text = "Importing the version...";
                    client.AdminImportVersion(admin, existing.Id, versionFolder, versionFolder,
                        new[] { new DropLaunch { Platform = "windows", Name = "Play", Command = launch } });
                    progress.CurrentProgressValue = 3;

                    RememberUpload(plugin, game.Name, existing.Id);
                    api.Notifications.Add("drop-send-ok-" + game.Id,
                        $"Drop: sent {game.Name} ({versionFolder}). Launch: {launch ?? "none found, set it in Drop"}.",
                        NotificationType.Info);
                }
                catch (OperationCanceledException)
                {
                    logger.Info($"Drop: send of {game.Name} cancelled.");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Drop: send of {game.Name} failed.");
                    api.Notifications.Add("drop-send-failed-" + game.Id,
                        $"Drop: could not send {game.Name}: {DropHttp.Describe(ex)}", NotificationType.Error);
                }
            }, new GlobalProgressOptions("Send to Drop", true) { IsIndeterminate = false });
        }

        /// <summary>
        /// The command Drop will run from the version root. The game's own play action
        /// is the truth when it points inside the install folder; otherwise the first
        /// exe at the root, which is right for most portable installs and wrong for
        /// launchers with a stub, which the admin fixes in Drop's UI.
        /// </summary>
        private static string GuessLaunch(Game game, string source)
        {
            var play = game.GameActions?.FirstOrDefault(a => a.IsPlayAction && a.Type == GameActionType.File && !string.IsNullOrWhiteSpace(a.Path));
            if (play != null)
            {
                var full = Path.IsPathRooted(play.Path) ? play.Path : Path.Combine(source, play.Path);
                if (full.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    var rel = full.Substring(source.TrimEnd('\\').Length + 1);
                    return string.IsNullOrWhiteSpace(play.Arguments) ? rel : rel + " " + play.Arguments;
                }
            }
            var exe = Directory.GetFiles(source, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(n => n.IndexOf("unins", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                .ThenBy(n => n.Length)
                .FirstOrDefault();
            return exe;
        }

        private static DropGame FindByName(DropClient client, DropLibraryPlugin plugin, string name)
        {
            var auth = plugin.ClientAuth();
            if (auth == null) return null;
            return client.Library(auth).FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static DropGame WaitForGame(DropClient client, DropLibraryPlugin plugin, string name, TimeSpan timeout, CancellationToken cancel)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                cancel.ThrowIfCancellationRequested();
                Thread.Sleep(3000);
                var found = FindByName(client, plugin, name);
                if (found != null) return found;
            }
            return null;
        }

        private static void RememberUpload(DropLibraryPlugin plugin, string name, string dropId)
        {
            var list = plugin.Settings.UploadedGames;
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
            if (!string.IsNullOrWhiteSpace(dropId) && !list.Contains(dropId)) list.Add(dropId);
            plugin.SaveSettings();
        }

        /// <summary>Recursive copy, overwriting changed files, never deleting at the target.</summary>
        private static void CopyTree(string source, string target, CancellationToken cancel)
        {
            Directory.CreateDirectory(target);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                cancel.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(target, dir.Substring(source.Length).TrimStart('\\', '/')));
            }
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                cancel.ThrowIfCancellationRequested();
                var rel = file.Substring(source.Length).TrimStart('\\', '/');
                if (string.Equals(rel, DropInstallMarker.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(target, rel);
                var src = new FileInfo(file);
                var dst = new FileInfo(dest);
                if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc) continue;
                File.Copy(file, dest, true);
            }
        }
    }
}
