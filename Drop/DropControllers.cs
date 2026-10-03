using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Yabo.Shared;

namespace DropPlaynite
{
    /// <summary>
    /// Written into the install folder after a successful download, so a later
    /// library refresh can tell installed from not without asking the server, and
    /// Play knows which version's launch table to use. Deliberately NOT the desktop
    /// client's <c>.dropdata</c>, which is a Rust <c>pot</c> blob this plugin cannot
    /// read or write; the two clients keep separate installs.
    /// </summary>
    public class DropInstallMarker
    {
        public const string FileName = "drop-playnite.json";
        public string GameId { get; set; }
        public string VersionId { get; set; }
        public string Name { get; set; }
        public DateTime InstalledAt { get; set; }

        public static DropInstallMarker Read(string installDir)
        {
            try
            {
                var path = Path.Combine(installDir ?? string.Empty, FileName);
                return File.Exists(path) ? JsonConvert.DeserializeObject<DropInstallMarker>(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Write(string installDir)
        {
            File.WriteAllText(Path.Combine(installDir, FileName), JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    /// <summary>
    /// Install = the desktop client's download agent, in order (drop-app
    /// <c>games/src/downloads/download_agent.rs</c>): pick the newest Windows version,
    /// fetch its download manifest, sync depots, then for every chunk of every
    /// version in the manifest GET it from a depot and write its file slices through
    /// <see cref="DropChunkWriter"/>, which decrypts, hashes and places them. Files
    /// the manifest's fileList assigns to a different version are consumed and not
    /// written, exactly as the client does with <c>should_write</c>.
    /// </summary>
    public class DropInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DropLibraryPlugin plugin;
        private CancellationTokenSource cts;

        public DropInstallController(Game game, DropLibraryPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Install from Drop";
        }

        public override void Install(InstallActionArgs args)
        {
            var auth = plugin.ClientAuth();
            if (auth == null)
            {
                plugin.PlayniteApi.Notifications.Add("drop-install-noauth",
                    "Drop: sign in first (plugin settings, Sign in with a code).", NotificationType.Error);
                return;
            }

            cts = new CancellationTokenSource();
            var gameId = Game.GameId;
            var installDir = plugin.InstallDirFor(Game.Name);

            Task.Run(() =>
            {
                try
                {
                    var client = plugin.Api();

                    var version = client.Versions(auth, gameId)
                        .Where(v => v.Platform == "windows")
                        .OrderByDescending(v => v.Index)
                        .FirstOrDefault();
                    if (version == null)
                    {
                        throw new InvalidOperationException("this game has no Windows version on the server.");
                    }

                    var info = client.Manifest(plugin.ClientAuth(), version.VersionId);
                    var depots = client.Depots(plugin.ClientAuth());
                    if (depots.Count == 0)
                    {
                        throw new InvalidOperationException("the server lists no depots to download from.");
                    }

                    Directory.CreateDirectory(installDir);

                    var totalChunks = info.Manifests.Values.Sum(m => m.Chunks.Count);
                    var done = 0;
                    foreach (var manifest in info.Manifests.Values)
                    {
                        if (manifest.Key == null || manifest.Key.Length != 16)
                        {
                            throw new InvalidDataException("manifest for version " + manifest.VersionId + " carries no 16-byte key.");
                        }
                        foreach (var chunk in manifest.Chunks)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            DownloadChunk(client, depots, gameId, manifest, chunk, info, installDir);
                            done++;
                            if (done % 10 == 0 || done == totalChunks)
                            {
                                logger.Info($"Drop: {Game.Name}: chunk {done}/{totalChunks}");
                            }
                        }
                    }

                    new DropInstallMarker { GameId = gameId, VersionId = version.VersionId, Name = Game.Name, InstalledAt = DateTime.UtcNow }
                        .Write(installDir);

                    // Play needs the launch table; fetch it now so the card is playable
                    // without waiting for the next library refresh.
                    var actions = plugin.BuildPlayActions(gameId, version.VersionId, installDir);
                    plugin.ApplyPlayActions(Game.Id, actions);

                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = installDir }));
                    plugin.PlayniteApi.Notifications.Add("drop-install-ok-" + gameId,
                        $"Drop: installed {Game.Name} ({version.Name}).", NotificationType.Info);
                }
                catch (OperationCanceledException)
                {
                    logger.Info($"Drop: install of {Game.Name} cancelled.");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Drop: install of {Game.Name} failed.");
                    plugin.PlayniteApi.Notifications.Add("drop-install-failed-" + gameId,
                        $"Drop: could not install {Game.Name}: {DropHttp.Describe(ex)}", NotificationType.Error);
                }
            });
        }

        /// <summary>
        /// One chunk, tried on each depot in turn and up to three times per depot. A
        /// checksum mismatch counts as a failed attempt: the bytes were wrong, not the
        /// network, and a retry from another depot is the right answer.
        /// </summary>
        private void DownloadChunk(DropClient client, IReadOnlyList<string> depots, string gameId,
            DropManifest manifest, DropManifestChunk chunk, DropDownloadInfo info, string installDir)
        {
            Exception last = null;
            foreach (var depot in depots)
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var url = DropClient.ChunkUrl(depot, gameId, manifest.VersionId, chunk.Id);
                        // A fresh JWT per request: they expire after ten seconds.
                        using (var response = DropHttp.OpenStream(url, plugin.ClientAuth()))
                        using (var body = response.GetResponseStream())
                        {
                            DropChunkWriter.Write(body, chunk, manifest.Key, installDir,
                                f => info.FileList.TryGetValue(f, out var owner) && owner == manifest.VersionId);
                        }
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        logger.Warn($"Drop: chunk {chunk.Id} attempt {attempt} on {depot} failed: {DropHttp.Describe(ex)}");
                    }
                }
            }
            throw new IOException("chunk " + chunk.Id + " could not be downloaded from any depot: " + DropHttp.Describe(last ?? new Exception("no depots")));
        }

        public override void Dispose()
        {
            cts?.Cancel();
            base.Dispose();
        }
    }

    /// <summary>Uninstall = delete the folder this plugin created. Nothing on the server changes.</summary>
    public class DropUninstallController : UninstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DropLibraryPlugin plugin;

        public DropUninstallController(Game game, DropLibraryPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Uninstall";
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var dir = Game.InstallDirectory;
            if (string.IsNullOrWhiteSpace(dir) || DropInstallMarker.Read(dir) == null)
            {
                // Only ever delete a folder this plugin marked as its own.
                dir = plugin.InstallDirFor(Game.Name);
            }
            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(dir) && DropInstallMarker.Read(dir) != null)
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Drop: uninstall of {Game.Name} failed.");
                    plugin.PlayniteApi.Notifications.Add("drop-uninstall-failed-" + Game.GameId,
                        $"Drop: could not remove {Game.Name}: {ex.Message}", NotificationType.Error);
                }
                InvokeOnUninstalled(new GameUninstalledEventArgs());
            });
        }
    }
}
