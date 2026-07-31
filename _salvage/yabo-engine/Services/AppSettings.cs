using System;
using System.IO;
using System.Text.Json;
using GitHubLauncher.Core.Models;

namespace GithubLauncher
{
    public class AppSettings
    {
        public bool FirstStartup { get; set; } = true;
        public bool IconFill { get; set; } = true;
        public bool UseGridView { get; set; } = true;
        public float IconOpacity { get; set; } = 1.0f;
        public int IconSize { get; set; } = 220;
        public int IconMargin { get; set; } = 8;
        public int SlotTextMargin { get; set; } = 112;
        public int SlotSize { get; set; } = 220;
        public bool WindowBorderRounding { get; set; } = true;
        public bool ShowOSTopBar { get; set; } = false;
        // [yabo-launcher fork] Optional SteamGridDB API key — when set, ports without a curated icon get
        // real cover art fetched + cached from steamgriddb.com (falls back to the GitHub owner avatar).
        public string SteamGridDbApiKey { get; set; } = string.Empty;
        public string PrimaryColor { get; set; } = "#18181b";
        public string SecondaryColor { get; set; } = "#404040";
        public TargetOS Platform { get; set; } = TargetOS.Auto;

        /// <summary>
        /// [yabo-launcher fork] Per-app platform overrides, keyed by app folderName.
        /// Each value is a <see cref="TargetOS"/> name ("Auto", "Windows", "MacOS",
        /// "LinuxX64", "LinuxARM64"). When an entry is present it overrides the global
        /// <see cref="Platform"/> for that app's release-asset selection. Missing/"Auto"
        /// entries fall back to the global setting. Backward compatible: absent in old
        /// settings.json files (deserializes to an empty dictionary).
        /// </summary>
        public Dictionary<string, string> PlatformOverrides { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// [yabo-launcher fork] For external (link-out) entries the user installed themselves — maps the
        /// entry's folderName to the local .exe they pointed us at. When set + the file exists, yabo can
        /// LAUNCH it even though it doesn't manage the install. Absent = link-out only (opens download page).
        /// </summary>
        public Dictionary<string, string> ExternalLaunchPaths { get; set; } = new Dictionary<string, string>();

        /// <summary>[yabo-launcher fork] Per-game cover-art overrides chosen by the user in the new UI's art
        /// picker, keyed by folderName → image URL. The resolver/--list-json prefer this over artUrl/search.</summary>
        public Dictionary<string, string> ArtOverrides { get; set; } = new Dictionary<string, string>();

        public List<string> HiddenApps { get; set; } = new List<string>();
        public List<string> ManuallyHiddenApps { get; set; } = new List<string>();
        public string AppsPath { get; set; } = string.Empty;
        public string GitHubApiToken { get; set; } = string.Empty;
        public string SortBy { get; set; } = "LastPlayed";
        public bool StartFullscreen { get; set; } = false;
        public bool CloseAfterLaunch {  get; set; } = false;
        public string BackgroundImagePath { get; set; } = string.Empty;
        public string LauncherMusicPath { get; set; } = string.Empty;
        public float MusicVolume { get; set; } = 0.2f;
        public float BackgroundOpacity { get; set; } = 0.15f;
        public bool EnableGamepadInput { get; set; } = true;
        public string LinuxWindowsLaunchCommand { get; set; } = string.Empty;
        public string LibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// [yabo-launcher fork — RomM source] OPTIONAL self-hosted RomM connection. When
        /// <see cref="RommBaseUrl"/> is set (a Tailscale MagicDNS URL like "http://nas:8083"), the engine
        /// can resolve a card's needed ROM by sha1 from the user's own RomM library instead of prompting
        /// (see <see cref="GithubLauncher.Services.RommSource"/>). Empty = the RomM source is DORMANT
        /// (default), so catalog feeding (--list-json) and existing delivery are unaffected. These creds live
        /// HERE in settings.json, NEVER in apps.json — the feed stays shareable/sellable with no personal
        /// server baked in. <see cref="RommAuthMode"/>: "basic" (Username/Password) | "bearer"/"token" |
        /// "apikey" (sends RommToken as X-Api-Key). Backward compatible: absent in old settings deserializes
        /// to empty/defaults (dormant).
        /// </summary>
        public string RommBaseUrl { get; set; } = string.Empty;
        public string RommAuthMode { get; set; } = "basic";
        public string RommUsername { get; set; } = string.Empty;
        public string RommPassword { get; set; } = string.Empty;
        public string RommToken { get; set; } = string.Empty;

        /// <summary>
        /// [yabo-launcher fork — OTA feed] The product: a private, gated CANON catalog that gets updated.
        /// When <see cref="RemoteFeedUrl"/> is set, the client pulls apps.json from it (the subscriber's
        /// <see cref="RemoteFeedToken"/> authorizes); a failed/invalid fetch keeps the last-good local file.
        /// Empty = DORMANT (default) — the engine just uses the bundled apps.json. user-apps.json + the
        /// exclusion list are SEPARATE so an OTA refresh never clobbers the user's own releases/hides.
        /// See <see cref="GithubLauncher.Services.FeedSyncService"/>.
        /// </summary>
        public string RemoteFeedUrl { get; set; } = string.Empty;
        public string RemoteFeedToken { get; set; } = string.Empty;

        /// <summary>
        /// [yabo-launcher fork — itch.io route] The user's itch.io API key (from itch.io/user/settings/api-keys).
        /// Required even for FREE itch downloads — anonymous itch API calls are rejected ("authentication
        /// required"). Used to list a game's uploads (Authorization header) and to sign the upload download URL
        /// (?api_key=). Empty = the itch route is DORMANT. One global key, not per-card. See ItchInstallService.
        /// </summary>
        public string ItchApiKey { get; set; } = string.Empty;

        /// <summary>
        /// [yabo-launcher fork] OPTIONAL Steam Rich Presence mode for launched games. One of:
        ///   "off"       — no presence puppet (default).
        ///   "shortcut"  — presence comes from a non-Steam shortcut the user added (no launch-time puppet).
        ///   "retroarch" — launch RetroArch (Steam appid 1118310) with a no-op core + a dummy content file
        ///                 named "&lt;game&gt;.zip" so Steam shows "Playing &lt;game&gt;" while our real
        ///                 standalone game runs in its own window. See <see cref="GithubLauncher.Services.RetroArchPresenceService"/>.
        /// Only "retroarch" spawns a launch-time presence puppet; "shortcut"/"off" do nothing at launch.
        /// </summary>
        public string SteamPresenceMode { get; set; } = "off";

        /// <summary>
        /// [yabo-launcher fork] Name of the currently-applied Winamp `.wsz` skin (its file base name,
        /// e.g. "Bento"). Its extracted assets live under &lt;exe dir&gt;\skins\&lt;CurrentSkin&gt;\. Empty
        /// means no skin is applied and the UI falls back to its built-in CSS skin. Set by --load-skin,
        /// cleared by --clear-skin, read back by --get-skin.
        /// </summary>
        public string CurrentSkin { get; set; } = string.Empty;

        /// <summary>
        /// Resolves the effective ROM/data Library directory. Falls back to
        /// &lt;BaseDir&gt;\Library when <see cref="LibraryPath"/> is unset.
        /// </summary>
        public string ResolveLibraryPath()
        {
            return string.IsNullOrWhiteSpace(LibraryPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Library")
                : LibraryPath;
        }

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "settings.json"
        );

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
                throw;
            }
        }
    }
}

