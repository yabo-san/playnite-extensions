using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace YaboDev
{
    /// <summary>
    /// OWNER-ONLY publish tool. Never bundled — its absence elsewhere is the wall, so no gate.
    ///   • Right-click games → toggle the `public` tag (what ships to subscribers).
    ///   • Main menu → "Publish public cards…" → collect every public-tagged Yabo card, ask for a
    ///     patch note, and shell the engine: --export-catalog "&lt;feed&gt;" --only "&lt;folders&gt;" --notes … --title …
    /// The engine does the gated, draft-scrubbing, release-note-embedding export (already built + tested).
    /// </summary>
    public class YaboDevPlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        public override Guid Id { get; } = Guid.Parse("8f2c4a16-3b9d-4e57-a1c8-2f6e9d0a7b34");

        // The shipped client's plugin Id — we only ever publish ITS games (Yabo cards), nothing else.
        private static readonly Guid YaboLibraryId = Guid.Parse("1a390433-18ff-42bd-8741-8c1778a7cab9");
        private const string ReleaseTag = "release";   // owner-only label; cosmetic (subscribers publish by folderName)

        public YaboDevPlugin(IPlayniteAPI api) : base(api) { }

        // ── config (engine + feed paths), persisted in the plugin's data dir ──────────────────────────
        private class DevConfig { public string EnginePath = ""; public string FeedPath = ""; }
        private string ConfigPath => Path.Combine(GetPluginUserDataPath(), "config.json");
        private DevConfig LoadConfig()
        {
            try { if (File.Exists(ConfigPath)) return JsonConvert.DeserializeObject<DevConfig>(File.ReadAllText(ConfigPath)) ?? new DevConfig(); }
            catch (Exception ex) { Logger.Error(ex, "YaboDev: bad config"); }
            return new DevConfig();
        }
        private void SaveConfig(DevConfig c) => File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(c, Formatting.Indented));

        // ── right-click: toggle the public tag on the selected Yabo games ─────────────────────────────
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var yaboGames = args.Games.Where(g => g.PluginId == YaboLibraryId).ToList();
            if (yaboGames.Count == 0) yield break;

            yield return new GameMenuItem
            {
                MenuSection = "Yabo Dev",
                Description = $"Toggle 'release' (publish) — {yaboGames.Count} card(s)",
                Action = _ => TogglePublic(yaboGames)
            };
        }

        private void TogglePublic(List<Game> games)
        {
            var tag = PlayniteApi.Database.Tags.Add(ReleaseTag);   // returns existing if present
            int made = 0, cleared = 0;
            foreach (var g in games)
            {
                g.TagIds = g.TagIds ?? new List<Guid>();
                if (g.TagIds.Contains(tag.Id)) { g.TagIds.Remove(tag.Id); cleared++; }
                else { g.TagIds.Add(tag.Id); made++; }
                PlayniteApi.Database.Games.Update(g);
            }
            PlayniteApi.Dialogs.ShowMessage($"Release tag: +{made} / -{cleared}.\nUse Main Menu ▸ Yabo Dev ▸ Publish to ship them.");
        }

        // ── main menu: set paths + publish ────────────────────────────────────────────────────────────
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            const string sec = "@Yabo Dev";
            yield return new MainMenuItem { MenuSection = sec, Description = "Publish release cards…", Action = _ => Publish() };
            yield return new MainMenuItem { MenuSection = sec, Description = "Set engine + feed paths…", Action = _ => ConfigurePaths() };
            yield return new MainMenuItem { MenuSection = sec, Description = "List release cards", Action = _ => ListPublic() };
        }

        private void ConfigurePaths()
        {
            var c = LoadConfig();
            var exe = PlayniteApi.Dialogs.SelectFile("yabo engine|yabo-launcher.exe;*.exe");
            if (!string.IsNullOrWhiteSpace(exe)) c.EnginePath = exe;
            var feed = PlayniteApi.Dialogs.SelectFile("feed apps.json|*.json");
            if (!string.IsNullOrWhiteSpace(feed)) c.FeedPath = feed;
            SaveConfig(c);
            PlayniteApi.Dialogs.ShowMessage($"Engine: {c.EnginePath}\nFeed:   {c.FeedPath}");
        }

        private List<Game> PublicGames()
        {
            var tag = PlayniteApi.Database.Tags.Add(ReleaseTag);
            return PlayniteApi.Database.Games
                .Where(g => g.PluginId == YaboLibraryId && g.TagIds != null && g.TagIds.Contains(tag.Id))
                .ToList();
        }

        private void ListPublic()
        {
            var games = PublicGames();
            var body = games.Count == 0 ? "(none tagged public yet)" : string.Join("\n", games.Select(g => "• " + g.Name));
            PlayniteApi.Dialogs.ShowMessage($"{games.Count} public card(s):\n\n{body}");
        }

        private void Publish()
        {
            var c = LoadConfig();
            if (string.IsNullOrWhiteSpace(c.EnginePath) || !File.Exists(c.EnginePath)) { ConfigurePaths(); c = LoadConfig(); }
            if (string.IsNullOrWhiteSpace(c.EnginePath) || !File.Exists(c.EnginePath)) { PlayniteApi.Dialogs.ShowErrorMessage("No engine path set."); return; }
            if (string.IsNullOrWhiteSpace(c.FeedPath)) { PlayniteApi.Dialogs.ShowErrorMessage("No feed path set."); return; }

            var games = PublicGames();
            if (games.Count == 0) { PlayniteApi.Dialogs.ShowMessage("Nothing tagged 'public'. Right-click games → Yabo Dev → Toggle 'public' first."); return; }

            // GameId IS the catalog folderName (set by YaboLibrary at import) — the engine's --only allow-list.
            var folders = string.Join(",", games.Select(g => g.GameId).Where(s => !string.IsNullOrWhiteSpace(s)));

            var titleRes = PlayniteApi.Dialogs.SelectString($"Publishing {games.Count} card(s). Headline for the notification:", "Yabo — Publish", "yabo catalog update");
            if (!titleRes.Result) return;
            var notesRes = PlayniteApi.Dialogs.SelectString("What's new? (the patch note subscribers see)", "Yabo — Publish", "");
            if (!notesRes.Result) return;

            try
            {
                // [capture staging covers → canon] STEP 1 — backfill the canon's artUrls from the owner's RESOLVED
                // covers (the --set-art override the owner picked, else yabo's SteamGridDB cache hit) BEFORE we
                // export. The cdn url written is a PUBLIC SteamGridDB url, so subscribers inherit the owner's exact
                // covers with NO API key. Scoped with --only to the released folders so we never touch drafts. This
                // is best-effort: a non-zero exit (e.g. no apps.json yet) just means nothing got backfilled — we
                // still publish whatever artUrls the cards already carry, so a sync hiccup never blocks a release.
                var syncOut = RunEngine(c.EnginePath, $"--sync-art --only \"{folders}\"");
                Logger.Info($"YaboDev sync-art: {syncOut}");

                // STEP 2 — export the (now cover-stamped) cards to the feed.
                var msg = RunEngine(c.EnginePath,
                    $"--export-catalog \"{c.FeedPath}\" --only \"{folders}\" --notes \"{Escape(notesRes.SelectedString)}\" --title \"{Escape(titleRes.SelectedString)}\"");
                Logger.Info($"YaboDev publish: {msg}");
                PlayniteApi.Dialogs.ShowMessage($"Published {games.Count} card(s) → feed (covers synced first).\n\n{msg}\n\nCommit/push the feed file to ship the OTA.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "YaboDev: publish failed");
                PlayniteApi.Dialogs.ShowErrorMessage($"Publish failed: {ex.Message}");
            }
        }

        // Shell the engine with the given arguments and return its combined stdout+stderr (trimmed).
        private static string RunEngine(string enginePath, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = enginePath,
                Arguments = arguments,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var outp = p.StandardOutput.ReadToEnd();
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(60000);
                return (outp + err).Trim();
            }
        }

        private static string Escape(string s) => (s ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
    }
}
