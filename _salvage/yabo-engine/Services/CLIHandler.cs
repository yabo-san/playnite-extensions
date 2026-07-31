using GitHubLauncher.Core.Models;
using GitHubLauncher.Core.Services;
using GithubLauncher.Models;
using GithubLauncher.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace GithubLauncher
{
    public class CLIHandler
    {
        private const ConsoleColor ColorTitle = ConsoleColor.Cyan;
        private const ConsoleColor ColorSuccess = ConsoleColor.Green;
        private const ConsoleColor ColorWarning = ConsoleColor.Yellow;
        private const ConsoleColor ColorError = ConsoleColor.Red;
        private const ConsoleColor ColorMuted = ConsoleColor.DarkGray;
        private static readonly GithubLauncherProfile Profile = GithubLauncherProfile.Instance;
        private static readonly string Repository = Profile.Repository;
        private const string VersionFileName = "version.txt";
        private const string UpdateCheckFileName = "update_check.json";
        private const int UpdaterProcessExitTimeoutSeconds = 120;

        private GameManager? _gameManager;
        private string _currentVersion = "Unknown";

        public async Task<int> Execute(string[] args)
        {
            if (args.Length == 0)
            {
                ClearTerminal();
                await PrintHeader();
                ShowHelp();
                return 0;
            }

            var command = args[0].ToLower();

            try
            {
                switch (command)
                {
                    case "--add-steam-shortcut":
                        await InitializeGameManager();
                        return AddSteamShortcutCommand(args);

                    case "--remove-steam-shortcut":
                        await InitializeGameManager();
                        return RemoveSteamShortcutCommand(args);

                    case "--list-steam-shortcuts":
                        await InitializeGameManager();
                        return ListSteamShortcutsCommand(args);

                    case "-h":
                    case "--help":
                        ClearTerminal();
                        await PrintHeader();
                        ShowHelp();
                        return 0;

                    case "-v":
                    case "--version":
                        // [yabo-launcher fork] Print the build timestamp (from build-info.json beside the exe,
                        // fallback = exe LastWriteTime) + AssemblyVersion so the owner can tell which build is
                        // running. No banner — clean stdout for piping.
                        return VersionCommand();

                    case "-l":
                    case "--list":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await ListGames();

                    case "--list-json":
                        // [yabo-launcher fork] Clean machine-readable catalog + live status for the Tauri UI.
                        // No banner/colour — pure JSON on stdout. Covers/artUrl the frontend reads from files.
                        // PLAIN `--list-json` stays GATE-FILTERED (the Playnite plugin depends on that for the
                        // staged-set wipe). `--list-json --all` (or `--list-catalog`) emits the FULL catalog with a
                        // per-card `gated` boolean so the curation CONSOLE can browse everything and toggle Active.
                        await InitializeGameManager();
                        return ListGamesJson(args.Any(a => string.Equals(a, "--all", StringComparison.OrdinalIgnoreCase)));

                    case "--list-catalog":
                        // [yabo-launcher fork] Alias for `--list-json --all` — the FULL catalog (gate-independent),
                        // each card carrying `gated`. The curation console uses this to browse all ~514 cards.
                        await InitializeGameManager();
                        return ListGamesJson(allCatalog: true);

                    case "--export-desktop-entries":
                    case "--desktop-entries":
                        // [yabo-launcher fork — Linux] Write one freedesktop .desktop shortcut per game into
                        // ~/.local/share/applications so yabo games appear in rofi / the app menu (the Linux
                        // equivalent of the Raycast/Flow-Launcher "exporter"). Installed-only by default; --all
                        // includes the whole catalog (each click install-then-plays via --play). --dry-run prints.
                        await InitializeGameManager();
                        return ExportDesktopEntriesCommand(args);

                    case "--library":
                    case "--equipped":
                        // [yabo-launcher fork] "What am I equipped to play?" — derives, from the filesystem (install
                        // dirs + the Library pool + the catalog's dataFiles), which games are READY now, which are
                        // one --provide-data away (file pooled in the Library), and which still need their data file.
                        await InitializeGameManager();
                        return LibraryCommand(args.Any(a => a == "--json"));

                    case "-r":
                    case "--run":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        {
                            // [yabo-launcher fork] Optional `--rom "<file>"` launch-time override (SMAS picker).
                            var (runName, romOverride) = SplitRomModifier(args);
                            return await RunGame(runName, romOverride);
                        }

                    case "--path":
                    case "--copy-path":
                        // No header/banner: keep stdout clean so the path is pipe/clip-friendly.
                        await InitializeGameManager();
                        return PrintExecutablePath(GetGameNameFromArgs(args));

                    case "--open-folder":
                        // [yabo-launcher fork] Open a port's install dir in the OS file manager. The sandboxed
                        // Tauri web UI can't shell.open a raw local path, so the right-click "Open folder"
                        // action routes through the engine (full OS access) instead.
                        await InitializeGameManager();
                        return OpenFolderCommand(GetGameNameFromArgs(args));

                    case "-d":
                    case "--download":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await DownloadGameCommand(GetGameNameFromArgs(args));

                    case "-u":
                    case "--update":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await UpdateAllGames();

                    case "--update-launcher":
                    case "--self-update":
                        ClearTerminal();
                        await PrintHeader();
                        return await UpdateLauncher();

                    case "--provide-data":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return ProvideDataCommand(GetGameNameFromArgs(args));

                    case "--wire":
                    case "--rewire":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return RewireCategories();

                    case "-x":
                    case "--uninstall":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await UninstallGame(GetGameNameFromArgs(args));

                    case "--import-data":
                        await InitializeGameManager();
                        return ImportDataCommand(args);

                    case "--ingest":
                        // [iSNESrev — OpenEmu-style drag-in] Identify ANY dropped ROM by sha1 across the WHOLE
                        // catalog, install its port if needed, then stage + patch it. No port name required.
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await IngestCommand(args);

                    case "--play":
                        await InitializeGameManager();
                        return await PlayCommand(args);

                    case "--list-glsl-shaders":
                        return ListGlslShaders(args.Any(a => a == "--json"));

                    case "--resolve-art":
                        await InitializeGameManager();
                        return await ResolveArtCommand();

                    // [yabo-launcher fork — capture staging covers → canon] Backfill apps.json artUrls from the
                    // owner's resolved cover (ArtOverride → cache) so subscribers inherit the owner's exact PUBLIC
                    // cdn covers with no API key. No GameManager needed — operates on apps.json + cache directly.
                    case "--sync-art":
                        return SyncArtCommand(args);

                    case "--capture-art":
                        return CaptureArtCommand(args);

                    case "--set-external-path":
                        await InitializeGameManager();
                        return SetExternalPathCommand(args);

                    case "--list-exes":
                        await InitializeGameManager();
                        return ListExesCommand(args);

                    case "--set-exe":
                        await InitializeGameManager();
                        return SetExeCommand(args);

                    case "--list-versions":
                        await InitializeGameManager();
                        return await ListVersionsCommand(args);

                    case "--install-version":
                        await InitializeGameManager();
                        return await InstallVersionCommand(args);

                    case "--force-update":
                        await InitializeGameManager();
                        return await ForceUpdateCommand(args);

                    case "--create-shortcut":
                        await InitializeGameManager();
                        return CreateShortcutCommand(args);

                    case "--read-text":
                        return ReadTextCommand(args);

                    case "--recent":
                        return RecentCommand();

                    case "--action-log":
                        // [yabo-launcher fork] Dump recent structured action-log events (config writes, shader
                        // selections, shader-bind verdicts, launches). No game manager needed — reads the JSONL file.
                        return ActionLogCommand(args);

                    case "--clear-external-path":
                        await InitializeGameManager();
                        return ClearExternalPathCommand(args);

                    case "--clear-icon-cache":
                        return ClearIconCacheCommand();

                    case "--add-app":
                        return AddAppCommand(args);

                    case "--add-game":
                        return AddGameCommand(args);

                    case "--set-art":
                        await InitializeGameManager();
                        return SetArtCommand(args);

                    case "--add-art-favorite":
                        return AddArtFavoriteCommand(args);

                    case "--set-setting":
                        return SetSettingCommand(args);

                    case "--steam-presence-mode":
                        // [yabo-launcher fork] Get/set the OPTIONAL Steam Rich Presence mode (off|shortcut|retroarch).
                        // No arg → print the current value.
                        return SteamPresenceModeCommand(args);

                    case "--get-config":
                        // [yabo-launcher fork] Parse a snesrev port's config .ini → JSON for the in-app
                        // launcher drawer (Controls/Video/Audio/Tweaks tabs). Raw UTF-8, no banner.
                        await InitializeGameManager();
                        return GetConfigCommand(args);

                    case "--set-config":
                        // [yabo-launcher fork] Rewrite one "Section.Key" in a port's config .ini in place,
                        // preserving comments/ordering (the drawer's per-control writes).
                        await InitializeGameManager();
                        return SetConfigCommand(args);

                    case "--get-config-json":
                        // [yabo-launcher fork] Harbour Masters / libultraship ports (Ship of Harkinian, Starship,
                        // Ghostship, 2Ship, SpaghettiKart) keep a JSON config beside the exe (generated on first
                        // run). Parse it → curated groups (Display/Audio/Enhancements/Controllers) for the drawer.
                        await InitializeGameManager();
                        return GetConfigJsonCommand(args);

                    case "--set-config-json":
                        // [yabo-launcher fork] Rewrite ONE dotted-path value in a libultraship JSON config IN PLACE
                        // via JsonNode (structure/format-preserving), coercing type from the existing value.
                        await InitializeGameManager();
                        return SetConfigJsonCommand(args);

                    case "--export-config":
                        // [yabo-launcher fork] Dump a port's whole config .ini to a file (or stdout) — snapshot a
                        // tuned setup as a shareable preset. Pairs with --import-config.
                        await InitializeGameManager();
                        return ExportConfigCommand(args);

                    case "--import-config":
                        // [yabo-launcher fork] Replace a port's config .ini with a custom one (backs up the old to
                        // .ini.bak). "Bring your own settings" — the config analogue of --import-data.
                        await InitializeGameManager();
                        return ImportConfigCommand(args);

                    case "--sniff-gamepad":
                        // [yabo-launcher fork] Open controller 0 via SDL2 and print the SDL button NAME of the
                        // first press, so the web UI can bind using the SAME mapping the snesrev games use
                        // (the Web Gamepad API mismaps 8BitDo vs SDL). No banner — raw name on stdout.
                        return SniffGamepadCommand();

                    // ── bucket D (web-UI feature parity) ──────────────────────────────────────────
                    case "--hide":
                        await InitializeGameManager();
                        return HideCommand(args, hide: true);

                    case "--unhide":
                        await InitializeGameManager();
                        return HideCommand(args, hide: false);

                    case "--unhide-all":
                        await InitializeGameManager();
                        return UnhideAllCommand();

                    case "--edit-app":
                        await InitializeGameManager();
                        return EditAppCommand(args);

                    case "--remove-app":
                        return RemoveAppCommand(args);

                    case "--set-platform":
                        await InitializeGameManager();
                        return SetPlatformCommand(args);

                    case "--locate-install":
                        await InitializeGameManager();
                        return await LocateInstallCommand(args);

                    case "--sync-feed":
                        return await SyncFeedCommand(args);

                    case "--set-feed":
                        return SetFeedCommand(args);

                    case "--set-itch-key":
                        return SetItchKeyCommand(args);

                    case "--check-url":
                        return await CheckUrlCommand(args);

                    case "--adopt":
                        await InitializeGameManager();
                        return await AdoptCommand(args);

                    case "--scan-installs":
                        await InitializeGameManager();
                        return await ScanInstallsCommand(args);

                    case "--set-custom-icon":
                        await InitializeGameManager();
                        return SetCustomIconCommand(args);

                    case "--remove-custom-icon":
                        await InitializeGameManager();
                        return RemoveCustomIconCommand(args);

                    case "--skip-update":
                        await InitializeGameManager();
                        return await SkipUpdateCommand(args);

                    case "--changelog":
                        await InitializeGameManager();
                        return await ChangelogCommand(args);

                    case "--export-catalog":
                        return ExportCatalogCommand(args);

                    case "--import-catalog":
                        return ImportCatalogCommand(args);

                    // ── Curation gate (allow-list over the canon) ──────────────────────────────
                    // [yabo-launcher fork] gate.json beside the exe is an active-filter on --list-json. These
                    // commands resolve a name OR folderName via the catalog, then add/remove/list/clear it.
                    case "--gate-add":
                        await InitializeGameManager();
                        return GateAddCommand(args);

                    case "--gate-remove":
                        await InitializeGameManager();
                        return GateRemoveCommand(args);

                    case "--gate-list":
                        await InitializeGameManager();
                        return GateListCommand();

                    case "--gate-clear":
                        return GateClearCommand();

                    case "--gate-status":
                        await InitializeGameManager();
                        return GateStatusCommand();

                    // ── Winamp .wsz skin loader (engine side) ──────────────────────────────────
                    case "--load-skin":
                        // [yabo-launcher fork] Unzip a Winamp .wsz, cache its sprite sheets + text
                        // configs under skins\<name>\, record CurrentSkin in settings, and emit the
                        // manifest (BMP sheets as data:image/bmp;base64 URIs) for the WebView2 UI.
                        return LoadSkinCommand(args);

                    case "--get-skin":
                        // [yabo-launcher fork] Re-emit the CURRENT skin's manifest from its cached dir
                        // (or {"name":null} when no skin is set).
                        return GetSkinCommand(args);

                    case "--clear-skin":
                        // [yabo-launcher fork] Clear CurrentSkin → the UI falls back to its CSS skin.
                        return ClearSkinCommand();

                    default:
                        ClearTerminal();
                        await PrintHeader();
                        ShowHelp();
                        return PrintError($"Unknown command: {command}");
                }
            }
            catch (Exception ex)
            {
                return PrintError($"Critical error: {ex.Message}");
            }
            finally
            {
                _gameManager?.Dispose();
            }
        }

        private static void ClearTerminal()
        {
            // [yabo-launcher fork] When invoked programmatically (the Tauri UI, Playnite, or any piped CLI) stdout
            // is REDIRECTED. Clearing the screen is meaningless there AND actively harmful: Console.Clear() throws
            // under redirection, and the old catch wrote raw ANSI clear codes (\x1b[2J\x1b[H) straight into the data
            // stream the caller parses — garbling results and causing the intermittent "command failed" the UI saw.
            // Only clear a REAL interactive terminal.
            if (Console.IsOutputRedirected) return;
            try
            {
                Console.Clear();
            }
            catch
            {
                Console.Write("\x1b[2J\x1b[H");
            }
        }

        private async Task InitializeGameManager()
        {
            if (_gameManager == null)
            {
                _gameManager = new GameManager();

                // Force load all games
                var settings = AppSettings.Load();
                var originalHiddenApps = settings.HiddenApps.ToList();
                settings.HiddenApps.Clear();

                await _gameManager.LoadGamesAsync();

                // Restore hidden games list
                settings.HiddenApps = originalHiddenApps;
            }
        }

        private string GetGameNameFromArgs(string[] args)
        {
            if (args.Length < 2) return string.Empty;
            return string.Join(" ", args.Skip(1)).Trim('"', '\'');
        }

        private static string GetExactArg(string[] args, int index)
        {
            return args.Length > index ? args[index] : string.Empty;
        }

        private void LoadVersion()
        {
            try
            {
                string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string updateCheckFilePath = Path.Combine(currentAppDirectory, "update_check.json");

                if (File.Exists(updateCheckFilePath))
                {
                    var json = File.ReadAllText(updateCheckFilePath);
                    var updateInfo = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (updateInfo != null && updateInfo.TryGetValue("CurrentVersion", out var versionElement))
                    {
                        _currentVersion = versionElement.GetString() ?? "Unknown";
                        return;
                    }
                }

                // Fallback to version.txt
                string versionFilePath = Path.Combine(currentAppDirectory, "version.txt");
                if (File.Exists(versionFilePath))
                {
                    _currentVersion = File.ReadAllText(versionFilePath).Trim();
                }
            }
            catch
            {
                _currentVersion = "Unknown";
            }
        }

        private async Task PrintHeader()
        {
            LoadVersion();

            // [yabo-launcher fork] Skip the decorative ASCII banner when stdout is REDIRECTED (Tauri UI / Playnite /
            // piped CLI). Its multi-byte art garbles the output stream those callers parse for results — the root of
            // the intermittent "command failed" behavior. Interactive terminals still get the banner.
            if (Console.IsOutputRedirected) return;

            Console.WriteLine();
            WriteColor("  __  _   _   _   _   _   _   _      ", ColorTitle);
            WriteColor(" _                                    ", ColorMuted);
            Console.WriteLine();

            WriteColor(" / _|| | | | | | | | | | | | |_|     ", ColorTitle);
            WriteColor("| |   __ _ _   _ _ __ | | ___  _   _  ", ColorMuted);
            Console.WriteLine();

            WriteColor("| |_ | |_| | | |_| |_| |_| | | | |   ", ColorTitle);
            WriteColor("| |  / _` | | | | '_ \\| |/ _ \\| | | | ", ColorMuted);
            Console.WriteLine();

            WriteColor("|  _||  _  | |  _  _  _  _  | | |_|   ", ColorTitle);
            WriteColor("| |_| (_| | |_| | | | | | (_) | |_| | ", ColorMuted);
            Console.WriteLine();

            WriteColor("| |  | | | | | | | | | | |  \\  /     ", ColorTitle);
            WriteColor("|_|\\__,_|\\__,_|_| |_|_|\\___/ \\__,_| ", ColorMuted);
            Console.WriteLine();

            WriteColor("|_|  |_| |_| |_|_| |_|_|_|   \\/      ", ColorTitle);
            WriteColor("                                       ", ColorMuted);
            Console.WriteLine();

            Console.WriteLine($"  Launcher Version: {_currentVersion}");

            await CheckForLauncherUpdates();
            PrintLine();
        }

        private async Task CheckForLauncherUpdates()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("User-Agent", Profile.CliUserAgent);

                var response = await client.GetStringAsync("https://api.github.com/repos/yabo-san/yabo-launcher/releases/latest");
                using var doc = JsonDocument.Parse(response);
                var latestTag = doc.RootElement.GetProperty("tag_name").GetString();

                if (!string.IsNullOrEmpty(latestTag) && latestTag != _currentVersion)
                {
                    WriteColor($"  [UPDATE AVAILABLE] ", ColorWarning);
                    Console.WriteLine($"New version {latestTag} is available! Use --update-launcher to upgrade.");
                    Console.WriteLine();
                }
            }
            catch
            {
                // Silently skip update check if offline or error
            }
        }

        private void ShowHelp()
        {
            Console.WriteLine("Usage: GithubLauncher [command] [game name]");
            Console.WriteLine();
            WriteColor("Commands:", ColorTitle);
            Console.WriteLine();
            PrintHelpItem("-h, --help", "Show this help screen");
            PrintHelpItem("-l, --list", "List all available games");
            PrintHelpItem("-u, --update", "Update all installed games");
            PrintHelpItem("--update-launcher", "Update the launcher itself");
            PrintHelpItem("-d, --download <name>", "Download and install a game (auto-picks the right artifact)");
            PrintHelpItem("-r, --run <name>", "Run a game (auto-updates if needed)");
            PrintHelpItem("-x, --uninstall <name>", "Uninstall a game");
            PrintHelpItem("--path <name>", "Print the exe path (for Steam Launch Options)");
            PrintHelpItem("--play <name> [--data f] [--config f.ini] [--shader preset]", "Unified Play: download+stage+config+shader+run (the Playnite/Steam-shortcut action)");
            PrintHelpItem("--library, --equipped [--json]", "What you're set up to play: ready / in-library / need-the-file");
            PrintHelpItem("--import-data <name> <file>", "Stage a ROM/data file into a port (per its dataFiles)");
            PrintHelpItem("--provide-data <name>", "Place a port's data files from the Library into its folder");
            PrintHelpItem("--wire, --rewire", "Re-apply Doom/Quake/Doomseeker wiring to installed tools");
            PrintHelpItem("--add-steam-shortcut <name>", "Add a port to Steam as a non-Steam shortcut (--play; RESTART Steam after)");
            PrintHelpItem("--remove-steam-shortcut <name>", "Remove our Steam non-Steam shortcut for a port (RESTART Steam after)");
            PrintHelpItem("--list-steam-shortcuts [--json]", "List the yabo non-Steam shortcuts in shortcuts.vdf");
            PrintHelpItem("--list-glsl-shaders [--json]", "List the bundled built-in GLSL shaders (snesrev OpenGL renderer)");
            PrintHelpItem("--export-config <name> [out.ini]", "Dump a port's config .ini (to a file, or stdout) — save a preset");
            PrintHelpItem("--import-config <name> <file.ini>", "Apply a custom config .ini to a port (backs up the old one)");
            PrintHelpItem("--get-config-json <name> [--subset <group>]", "Read a Harbour Masters / libultraship port's JSON config → curated groups (Display/Audio/Enhancements/Controllers)");
            PrintHelpItem("--set-config-json <name> <dotted.path> <value>", "Rewrite one value in a libultraship JSON config in place (e.g. CVars.gEnhancements.Widescreen 1)");
            PrintHelpItem("--action-log [--json] [--game <name>] [--tail N]", "Dump the structured action log (config writes / shader selects / launches; default 50)");
            PrintHelpItem("--sniff-gamepad", "Print the SDL button name of the next gamepad press (~6s)");
            PrintHelpItem("--load-skin <file.wsz>", "Load a Winamp .wsz skin: cache its sprites + emit a JSON manifest (data:image/bmp URIs)");
            PrintHelpItem("--get-skin [--json]", "Emit the current skin's manifest (or {\"name\":null} if none)");
            PrintHelpItem("--clear-skin", "Clear the current skin (UI falls back to its CSS skin)");
            PrintHelpItem("--steam-presence-mode [off|shortcut|retroarch]", "Get/set the optional Steam Rich Presence mode (no arg → print current)");
            PrintHelpItem("--add-game --repo <owner/repo> [--ia <id|url>] [--name <n>] [--category <c>] [--asset-pattern <re>] [--data-files <name[:sub[:optional]],...>]", "Add YOUR own port to user-apps.json (survives OTA; pass --ia + --data-files to write a binary+data collide card)");
            PrintHelpItem("--list-catalog", "Curation console: FULL catalog as JSON (gate-independent), each card with a `gated` boolean (alias: --list-json --all)");
            PrintHelpItem("--gate-add <name-or-folder>", "Curation gate: activate ONE card (creates gate.json; --list-json then shows only gated cards)");
            PrintHelpItem("--gate-remove <name-or-folder>", "Curation gate: deactivate one card");
            PrintHelpItem("--gate-list", "Curation gate: list the currently-active cards (folderName + resolved name)");
            PrintHelpItem("--gate-clear", "Curation gate: empty the gate (writes empty gate.json = WIPED library)");
            PrintHelpItem("--gate-status", "Curation gate: print \"gated: N active of M canon\" (or \"no gate (all N shown)\")");
            PrintHelpItem("--sync-art [--only <folders>] [--force]", "Backfill apps.json artUrls from your resolved covers (set-art override → SGDB cache) so subscribers inherit your exact PUBLIC cdn covers with no key (--force refreshes cache-sourced ones)");
            PrintHelpItem("--capture-art <folder-or-name> <https cdn url>", "Pin an explicit public cover cdn url onto one canon card (for covers yabo can't auto-recover, e.g. set via Playnite's own SGDB plugin)");
            PrintHelpItem("--export-catalog <path> [--gated|--only <folders>] [--notes <n>] [--title <t>] [--version <v>]", "PUBLISH a release feed: write the staged cards (--gated = exactly the curation gate) to a feed JSON, scrubbing dev fields");
            PrintHelpItem("--set-feed <url-or-path> [token]", "Subscriber: persist the feed URL (https://, file://, UNC, or a local path) into settings.json (empty = clear)");
            PrintHelpItem("--sync-feed [url-or-path]", "Subscriber: pull the feed into apps.json (CANON refresh; user-apps.json + hides untouched). Inline url overrides the saved one for this pull");
            PrintHelpItem("--update-launcher", "Update the launcher itself");
            Console.WriteLine();
            WriteColor("Examples:", ColorMuted);
            Console.WriteLine();
            Console.WriteLine("  GithubLauncher --list");
            Console.WriteLine("  GithubLauncher --download \"Super Mario 64 (Ghostship)\"");
            Console.WriteLine("  GithubLauncher --import-data \"Super Mario 64 (Ghostship)\" \"D:\\roms\\sm64.z64\"");
            Console.WriteLine("  GithubLauncher --run \"Super Mario 64 (Ghostship)\"");
            Console.WriteLine("  GithubLauncher --path \"Doom Retro\"");
            Console.WriteLine();
        }

        // ── Non-Steam-shortcut presence path ──────────────────────────────────────────────────
        // [yabo-launcher fork] Add/remove/list yabo games as Steam "non-Steam shortcuts" so Steam
        // shows "Playing <game>" (native playtime + overlay), launched through our own --play. These
        // operate directly on Steam's BINARY shortcuts.vdf via SteamShortcutService. A hidden
        // `--vdf <path>` flag overrides the target file (used by the synthetic-vdf round-trip test);
        // without it the commands target the user's real userdata\<id>\config\shortcuts.vdf.

        /// <summary>Pull a hidden `--vdf <path>` override out of the args (test seam), or return null.</summary>
        private static string? VdfOverride(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--vdf", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].Trim('"', '\'');
            return null;
        }

        /// <summary>Game name from args, ignoring the --vdf override pair and trailing flags.</summary>
        private static string SteamShortcutGameName(string[] args)
        {
            var parts = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--vdf", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
                if (string.Equals(args[i], "--json", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(args[i], "--wait-for-steam-exit", StringComparison.OrdinalIgnoreCase)) continue; // legacy worker flag
                parts.Add(args[i]);
            }
            return string.Join(" ", parts).Trim('"', '\'');
        }

        /// <summary>
        /// Resolve a LOCAL icon file path for the Steam shortcut's icon field (Steam needs a real file,
        /// not a URL). Prefers an explicit custom-icon file; falls back to the per-folder custom icon
        /// in Cache\CustomIcons; returns "" when nothing local is available (Steam shows a blank tile).
        /// </summary>
        private string ResolveLocalIconPath(GameInfo game)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(game.CustomIconPath) && File.Exists(game.CustomIconPath))
                    return game.CustomIconPath!;

                var dir = Path.Combine(AppContext.BaseDirectory, "Cache", "CustomIcons");
                if (!string.IsNullOrWhiteSpace(game.FolderName) && Directory.Exists(dir))
                {
                    foreach (var ext in new[] { ".ico", ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
                    {
                        var p = Path.Combine(dir, $"{game.FolderName}_custom{ext}");
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { /* best-effort; blank icon is fine */ }
            return string.Empty;
        }

        /// <summary>Target shortcuts.vdf path(s): the --vdf override if present, else every Steam profile's.</summary>
        private static IReadOnlyList<string> ResolveVdfTargets(string[] args)
        {
            var ovr = VdfOverride(args);
            if (!string.IsNullOrWhiteSpace(ovr))
                return new[] { ovr! };
            return SteamShortcutService.GetShortcutsVdfPaths();
        }

        private int AddSteamShortcutCommand(string[] args)
        {
            Log.Info("CLI: --add-steam-shortcut");

            string gameName = SteamShortcutGameName(args);
            if (string.IsNullOrWhiteSpace(gameName))
                return PrintError("No game name was provided for --add-steam-shortcut.");

            var game = FindGame(gameName);
            if (game == null)
                return PrintError($"Could not find a game named '{gameName}'.");

            var targets = ResolveVdfTargets(args);
            if (targets.Count == 0)
                return PrintError("Could not locate a Steam userdata profile (shortcuts.vdf). Open Steam at least once first.");

            string launcherPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(launcherPath))
                return PrintError("Could not determine launcher location.");

            string displayName = game.Name ?? gameName;
            string quotedExe = SteamShortcutService.QuoteExe(launcherPath);
            string startDir = Path.GetDirectoryName(launcherPath) ?? AppContext.BaseDirectory;
            string launchOptions = SteamShortcutService.BuildLaunchOptions(displayName);
            string iconPath = ResolveLocalIconPath(game);

            int added = 0, already = 0;
            foreach (var path in targets)
            {
                try
                {
                    var root = SteamShortcutService.Read(path);
                    bool changed = SteamShortcutService.AddShortcut(root, displayName, quotedExe, startDir, launchOptions, iconPath);
                    if (changed)
                    {
                        SteamShortcutService.Write(path, root);  // backs up to .vdf.bak first
                        added++;
                        WriteColor("Added: ", ColorSuccess);
                        Console.WriteLine($"\"{displayName}\" -> {path}");
                    }
                    else
                    {
                        already++;
                        WriteColor("Already present: ", ColorMuted);
                        Console.WriteLine($"\"{displayName}\" in {path}");
                    }
                }
                catch (Exception ex)
                {
                    return PrintError($"Failed to add Steam shortcut ({path}): {ex.Message}");
                }
            }

            if (targets.Count > 1)
                Console.WriteLine($"(targeted {targets.Count} Steam profiles)");
            Console.WriteLine($"LaunchOptions: {launchOptions}");
            WriteColor("RESTART STEAM ", ColorWarning);
            Console.WriteLine("for the change to take effect (Steam reads shortcuts.vdf at startup).");
            return added > 0 || already > 0 ? 0 : 1;
        }

        private int RemoveSteamShortcutCommand(string[] args)
        {
            Log.Info("CLI: --remove-steam-shortcut");

            string gameName = SteamShortcutGameName(args);
            if (string.IsNullOrWhiteSpace(gameName))
                return PrintError("No game name was provided for --remove-steam-shortcut.");

            // Resolve the display name through the catalog when possible (so a FolderName still removes
            // the entry written under the display name); fall back to the raw argument otherwise.
            var game = FindGame(gameName);
            string displayName = game?.Name ?? gameName;

            var targets = ResolveVdfTargets(args);
            if (targets.Count == 0)
                return PrintError("Could not locate a Steam userdata profile (shortcuts.vdf).");

            int removed = 0;
            foreach (var path in targets)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var root = SteamShortcutService.Read(path);
                    int n = SteamShortcutService.RemoveShortcut(root, displayName);
                    if (n > 0)
                    {
                        SteamShortcutService.Write(path, root);  // backs up to .vdf.bak first
                        removed += n;
                        WriteColor("Removed: ", ColorSuccess);
                        Console.WriteLine($"\"{displayName}\" ({n}) from {path}");
                    }
                }
                catch (Exception ex)
                {
                    return PrintError($"Failed to remove Steam shortcut ({path}): {ex.Message}");
                }
            }

            if (removed == 0)
            {
                WriteColor("No matching yabo shortcut found for ", ColorMuted);
                Console.WriteLine($"\"{displayName}\".");
                return 0;
            }

            WriteColor("RESTART STEAM ", ColorWarning);
            Console.WriteLine("for the change to take effect (Steam reads shortcuts.vdf at startup).");
            return 0;
        }

        private int ListSteamShortcutsCommand(string[] args)
        {
            Log.Info("CLI: --list-steam-shortcuts");
            bool json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

            var targets = ResolveVdfTargets(args);

            var rows = new List<(string Path, string Name, string LaunchOptions, string Exe)>();
            foreach (var path in targets)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var root = SteamShortcutService.Read(path);
                    foreach (var e in SteamShortcutService.ListYaboEntries(root))
                    {
                        rows.Add((
                            path,
                            SteamShortcutService.EntryName(e),
                            SteamShortcutService.EntryLaunchOptions(e),
                            e.GetString("Exe") ?? string.Empty));
                    }
                }
                catch (Exception ex)
                {
                    if (!json) WriteColor($"(skipped {path}: {ex.Message})\n", ColorMuted);
                }
            }

            if (json)
            {
                var payload = rows.Select(r => new
                {
                    appName = r.Name,
                    launchOptions = r.LaunchOptions,
                    exe = r.Exe,
                    vdf = r.Path,
                }).ToList();
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                return 0;
            }

            if (rows.Count == 0)
            {
                Console.WriteLine("No yabo shortcuts found in shortcuts.vdf.");
                return 0;
            }

            WriteColor($"yabo Steam shortcuts ({rows.Count}):\n", ColorTitle);
            foreach (var r in rows)
            {
                WriteColor("  • ", ColorSuccess);
                Console.WriteLine($"{r.Name}   [{r.LaunchOptions}]");
            }
            return 0;
        }

        // [yabo-launcher fork] Emit the catalog + live install status + resolved cover as JSON for the Tauri
        // frontend. Cover = the pinned artUrl, else a hit in the SteamGridDB cache (no network).
        private int ListGamesJson(bool allCatalog = false)
        {
            var allGames = _gameManager?.AllGames;  // FULL catalog (canon + user) — frontend does its own filtering

            // [yabo-launcher fork] CURATION GATE (allow-list). If gate.json exists beside the exe, PLAIN --list-json
            // emits ONLY the cards whose folderName is in it (an empty gate.json = [] = a WIPED library). If gate.json
            // is absent, it emits the whole catalog. Applies to BOTH canon and user cards. See GateService for the
            // full contract. The canon files (apps.json/user-apps.json) are never modified.
            //
            // `allCatalog` (--list-json --all / --list-catalog) BYPASSES the gate filter entirely so the curation
            // CONSOLE can browse every card; each emitted card then carries a `gated` boolean (below) reflecting its
            // current gate membership. The Playnite plugin keeps using PLAIN --list-json (gate-filtered) for its wipe.
            var games = (!allCatalog && allGames != null && GateService.Exists())
                ? allGames.Where(g => g != null && GateService.IsAllowed(g.FolderName)).ToList()
                : allGames;

            // Snapshot the gate once so each card's `gated` flag is correct. When no gate.json exists, EVERY card is
            // effectively active (the whole catalog shows), so report gated=true to match --list-json's behaviour.
            bool gateExists = GateService.Exists();
            var gateSet = gateExists ? GateService.Load() : null;
            bool IsGated(GameInfo g) =>
                !gateExists || (!string.IsNullOrWhiteSpace(g.FolderName) && gateSet!.Contains(g.FolderName!));

            // Load the cover cache once (case-insensitive); resolve covers offline.
            var coverCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var cachePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Cache", "steamgriddb-cache.json");
                if (System.IO.File.Exists(cachePath))
                {
                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(cachePath));
                    if (dict != null) foreach (var kv in dict) coverCache[kv.Key] = kv.Value;
                }
            }
            catch { /* no cache yet — covers come back empty, frontend falls back to avatar */ }

            var _settingsForJson = AppSettings.Load();
            var userArt = _settingsForJson.ArtOverrides ?? new Dictionary<string, string>();

            // [yabo-launcher fork] bucket D: hidden-state, local custom-icon cover, and LastPlayed.
            var manuallyHidden = _settingsForJson.ManuallyHiddenApps ?? new List<string>();
            var hiddenApps = _settingsForJson.HiddenApps ?? new List<string>();
            string HiddenKey(GameInfo g) =>
                !string.IsNullOrWhiteSpace(g.FolderName) ? "folder:" + g.FolderName
                : !string.IsNullOrWhiteSpace(g.Repository) ? "repo:" + g.Repository
                : "name:" + (g.Name ?? string.Empty);
            bool IsHidden(GameInfo g)
            {
                var k = HiddenKey(g);
                return manuallyHidden.Contains(k) || hiddenApps.Contains(k)
                    || (!string.IsNullOrWhiteSpace(g.Name) && (manuallyHidden.Contains(g.Name!) || hiddenApps.Contains(g.Name!)));
            }
            // Local-file cover lives at Cache\CustomIcons\<folder>_custom.<ext> (set by --set-custom-icon).
            var customIconsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Cache", "CustomIcons");
            string? CustomIconFor(GameInfo g)
            {
                if (g.HasCustomIcon && !string.IsNullOrEmpty(g.CustomIconPath)) return g.CustomIconPath;
                if (string.IsNullOrWhiteSpace(g.FolderName) || !Directory.Exists(customIconsDir)) return null;
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" })
                {
                    var p = System.IO.Path.Combine(customIconsDir, $"{g.FolderName}_custom{ext}");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
            // LastPlayed.txt sits in the install dir (written by the launch path's UpdateLastPlayedTime).
            string? LastPlayedFor(GameInfo g)
            {
                try
                {
                    if (string.IsNullOrEmpty(_gameManager?.GamesFolder) || string.IsNullOrEmpty(g.FolderName)) return null;
                    var p = System.IO.Path.Combine(g.GetInstallPath(_gameManager.GamesFolder), "LastPlayed.txt");
                    if (!File.Exists(p)) return null;
                    var raw = File.ReadAllText(p).Trim();
                    return DateTime.TryParse(raw, out var dt) ? dt.ToString("o") : raw;
                }
                catch { return null; }
            }

            string ResolveCover(GameInfo g)
            {
                // bucket D: a local custom-icon cover (--set-custom-icon) wins over everything.
                var ci = CustomIconFor(g);
                if (!string.IsNullOrEmpty(ci)) return ci!;
                if (!string.IsNullOrWhiteSpace(g.FolderName) && userArt.TryGetValue(g.FolderName!, out var ov) && !string.IsNullOrWhiteSpace(ov)) return ov;
                if (!string.IsNullOrWhiteSpace(g.ArtUrl)) return g.ArtUrl!;
                var (port, game) = SteamGridDbService.SearchTerms(g.Name, g.ArtName);
                foreach (var term in new[] { game, port })
                {
                    var key = SteamGridDbService.CleanName(term);
                    if (!string.IsNullOrWhiteSpace(key) && coverCache.TryGetValue(key, out var url) && !string.IsNullOrEmpty(url))
                        return url;
                }
                return string.Empty;
            }

            // [yabo-launcher fork] Data readiness for the UI gray-tint: ready / missing / wired / external.
            // [OWNER RULE 2026-06-02] The launcher does NOT manage a Library. By default ONLY self-sufficient cards
            // are colored: an IA content link / collide (contentUrl), a complete GitHub release (ingest none), or an
            // already-INSTALLED game (we then assume the user has its data). Everything else that needs data is
            // "missing" → DIMMED (the user must provide the file). No Library crediting / "armable" state.
            string DataStateFor(GameInfo g)
            {
                if (g.IsExternal) return "external";
                if (string.Equals(g.Ingest, "steam-data", StringComparison.OrdinalIgnoreCase)) return "wired";
                // [OWNER RULE 2026-06-02] Tools always show color — a utility (AutoTMC, …) is a
                // standalone download with nothing to "provision", so it's never dimmed.
                if (string.Equals(g.Category, "Tools", StringComparison.OrdinalIgnoreCase)) return "ready";
                // [OWNER RULE — FINAL 2026-06-02] COLORED = installed OR we have the content URL. GRAY = no data /
                // not installed. So every IA pack + collide (has a contentUrl) is colored; a GitHub release (no
                // content URL) is GRAY until its first launch / install, then colored. Nothing is hidden — color IS
                // the signal now.
                if (!string.IsNullOrWhiteSpace(g.ContentUrl)) return "ready";                                  // repacks + collides
                if (g.Status == GameStatus.Installed || g.Status == GameStatus.UpdateAvailable) return "ready"; // installed
                if (!string.IsNullOrWhiteSpace(LastPlayedFor(g))) return "ready";                              // first launch
                return "missing";                                                                              // GitHub release, not set up yet = gray
            }

            // [yabo-launcher fork] INSTANT drawer: embed each installed config-port's PARSED ini ({iniName,sections})
            // straight into --list-json so the UI reads it from the already-loaded catalog with ZERO extra spawn.
            // Reuses ParseConfigIni (same shape --get-config emits). Omitted (null) when the port has no config ini.
            object? ConfigFor(GameInfo g)
            {
                try
                {
                    var (iniName, sections) = ParseConfigIni(g);
                    return iniName == null ? null : new { iniName, sections };
                }
                catch { return null; }   // unreadable ini — UI falls back to bridge.getConfig
            }

            var list = (games == null) ? new List<object>() : games.Where(g => g != null).Select(g => (object)new
            {
                name = g.Name,
                folderName = g.FolderName,
                repository = g.Repository,
                category = g.Category,
                external = g.IsExternal,
                experimental = g.IsExperimental,
                appComponent = g.AppComponent,
                externalUrl = g.ExternalUrl,
                dataState = DataStateFor(g),   // ready | armable | missing | wired | external (UI gray-tints non-ready)
                status = g.IsExternal ? "External" : g.Status.ToString(),
                // [yabo-launcher fork] Explicit per-game update flag for the Playnite plugin (GithubLauncher/
                // Avalonia parity): true when the engine's status — installed-version vs. latest GitHub release —
                // resolved to UpdateAvailable. The `status` string also carries it, but this boolean lets the
                // plugin gate its "Update" action without string-matching the enum name.
                updateAvailable = g.Status == GameStatus.UpdateAvailable,
                installedVersion = CleanVersion(g.InstalledVersion),
                latestVersion = CleanVersion(g.LatestVersion),
                cover = ResolveCover(g),
                website = SiteFor(g),
                // [yabo-launcher fork] IA cards have no externalUrl — expose iaIdentifier/contentUrl so the
                // frontend can show "Open on Internet Archive" and treat the entry as downloadable (never gray).
                iaIdentifier = g.IaIdentifier,
                contentUrl = g.ContentUrl,
                hidden = IsHidden(g) || g.Hidden,
                // [yabo-launcher fork] CURATION GATE membership for the console's Active toggle. true = this card is
                // in gate.json (shown by plain --list-json). When no gate.json exists, every card reports true (the
                // whole catalog is active). Only meaningful with --list-json --all / --list-catalog (which is where
                // the console reads the full set); plain gated --list-json always returns gated cards anyway.
                gated = IsGated(g),
                rohanLibrary = g.RohanLibrary,   // [yabo-launcher fork] gated behind "Show Rohan's Packs"
                favorite = g.Favorite,   // [yabo-launcher fork] ★ — UI Favorites filter + star state
                lastPlayed = LastPlayedFor(g),
                // [yabo-launcher fork] Data needs + multi-game picker for the UI. dataFiles carries `optional`
                // so the UI can show which ROMs unlock which game; games drives the per-launch picker; buildSteps
                // is informational. All omitted (null) for the ~178 single-game ports that don't declare them.
                dataFiles = (g.DataFiles != null && g.DataFiles.Count > 0)
                    ? g.DataFiles.Select(d => new { name = d.Name, sha1 = d.Sha1, optional = d.Optional }).ToList<object>()
                    : null,
                games = (g.Games != null && g.Games.Count > 0)
                    ? g.Games.Select(ga => new { label = ga.Label, rom = ga.Rom, requires = ga.Requires }).ToList<object>()
                    : null,
                // [yabo-launcher fork] Other cards this one auto-installs after itself (apps.json "companions";
                // Doom 1 + 2 -> its 3 source-port engines). Null for cards that declare none.
                companions = (g.Companions != null && g.Companions.Count > 0)
                    ? g.Companions.ToList<object>()
                    : null,
                buildSteps = (g.BuildSteps != null && g.BuildSteps.Count > 0)
                    ? g.BuildSteps.Select(b => new { step = b.Step, source = b.Source, patch = b.Patch, target = b.Target }).ToList<object>()
                    : null,
                // [yabo-launcher fork] Embedded parsed config ini for installed config-ports → INSTANT drawer,
                // no per-open --get-config cold start. null for ports with no config ini (UI falls back).
                config = ConfigFor(g),
            }).ToList();
            // [yabo-launcher fork] Envelope the catalog with the build stamp so the UI can show which build it's
            // talking to. `build` = the ISO-8601 build timestamp (from build-info.json, fallback exe time);
            // `games` is the catalog array. The bridge unwraps `.games` (and tolerates a bare legacy array).
            var payload = new
            {
                build = BuildInfo.Short(),
                rid = BuildInfo.Rid,
                games = list,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            // Write raw UTF-8 bytes straight to stdout — bypasses Console.OutputEncoding so the Tauri shell
            // always reads valid UTF-8 (names like "Viva Piñata" were getting mangled to the system codepage).
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (var stdout = Console.OpenStandardOutput())
            {
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
            return 0;
        }

        // [yabo-launcher fork — Linux launcher integration] Generate one freedesktop .desktop shortcut per game
        // into ~/.local/share/applications, so every yabo game is indexed by rofi / wofi / fuzzel / GNOME / KDE —
        // no per-launcher plugin. This is yabo playing the role FlowLauncherExporter plays on Windows: the engine
        // is the exporter, and the Linux-native "launchable unit" is a .desktop file pointing back at our --play.
        //
        //   --all       include the whole catalog (default: only installed games — a clean app menu).
        //   --dry-run   compute + report, write nothing (so it's verifiable on a non-Linux dev box).
        private int ExportDesktopEntriesCommand(string[] args)
        {
            bool all    = args.Any(a => string.Equals(a, "--all",     StringComparison.OrdinalIgnoreCase));
            bool dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));

            var games = _gameManager?.AllGames ?? new List<GameInfo>();
            var settings = AppSettings.Load();
            var manuallyHidden = settings.ManuallyHiddenApps ?? new List<string>();
            var hiddenApps = settings.HiddenApps ?? new List<string>();
            string HiddenKey(GameInfo g) =>
                !string.IsNullOrWhiteSpace(g.FolderName) ? "folder:" + g.FolderName
                : !string.IsNullOrWhiteSpace(g.Repository) ? "repo:" + g.Repository
                : "name:" + (g.Name ?? string.Empty);
            bool IsHidden(GameInfo g)
            {
                var k = HiddenKey(g);
                return manuallyHidden.Contains(k) || hiddenApps.Contains(k)
                    || (!string.IsNullOrWhiteSpace(g.Name) && (manuallyHidden.Contains(g.Name!) || hiddenApps.Contains(g.Name!)));
            }

            // Icon must be a LOCAL absolute path (a URL is useless to a .desktop Icon=). Prefer the user's
            // custom icon (Cache\CustomIcons\<folder>_custom.*); else any image sitting in the install dir;
            // else leave it off and the launcher shows a generic game icon.
            var customIconsDir = Path.Combine(AppContext.BaseDirectory, "Cache", "CustomIcons");
            string? LocalIcon(GameInfo g)
            {
                if (g.HasCustomIcon && !string.IsNullOrEmpty(g.CustomIconPath) && File.Exists(g.CustomIconPath))
                    return g.CustomIconPath;
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".ico" })
                {
                    if (string.IsNullOrWhiteSpace(g.FolderName)) break;
                    var p = Path.Combine(customIconsDir, $"{g.FolderName}_custom{ext}");
                    if (File.Exists(p)) return p;
                }
                try
                {
                    if (!string.IsNullOrEmpty(_gameManager?.GamesFolder) && !string.IsNullOrEmpty(g.FolderName))
                    {
                        var instDir = g.GetInstallPath(_gameManager.GamesFolder);
                        if (Directory.Exists(instDir))
                            foreach (var name in new[] { "icon.png", "cover.png", "icon.jpg", "cover.jpg" })
                            {
                                var p = Path.Combine(instDir, name);
                                if (File.Exists(p)) return p;
                            }
                    }
                }
                catch { }
                return null;
            }

            bool Installed(GameInfo g) =>
                g.Status == GameStatus.Installed || g.Status == GameStatus.UpdateAvailable;

            var entries = games
                .Where(g => g != null && !g.IsExternal)            // external = open-URL, not a --play target
                .Where(g => !IsHidden(g) && !g.Hidden)
                .Where(g => all || Installed(g))
                .Where(g => !string.IsNullOrWhiteSpace(g.Name))
                .Select(g => new DesktopEntryService.Entry
                {
                    FolderName = string.IsNullOrWhiteSpace(g.FolderName) ? (g.Name ?? "game") : g.FolderName!,
                    Name       = g.Name!,
                    LaunchName = g.Name!,
                    IconPath   = LocalIcon(g),
                    Category   = g.Category,
                    Comment    = $"Play {g.Name} via yabo",
                })
                .ToList();

            // Exec target = THIS running engine binary, absolute. On Linux that's the published `yabo-launcher`.
            var execPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "yabo-launcher");

            var (written, pruned, dir) = DesktopEntryService.Sync(entries, execPath, dryRun);

            if (dryRun)
            {
                Console.WriteLine($"[dry-run] would write {written} .desktop entr{(written == 1 ? "y" : "ies")} to {dir}");
                foreach (var e in entries.Take(20))
                    Console.WriteLine($"  yabo-{e.FolderName}.desktop  →  Name={e.Name}  Icon={(e.IconPath ?? "(none)")}");
                if (entries.Count > 20) Console.WriteLine($"  … and {entries.Count - 20} more");
                Console.WriteLine($"Exec template: \"{execPath}\" --play \"<name>\"");
            }
            else
            {
                Console.WriteLine($"OK desktop-entries: wrote {written}, pruned {pruned} stale → {dir}");
                if (!OperatingSystem.IsLinux())
                    Console.WriteLine("  (note: not Linux — files written but +x/update-desktop-database skipped)");
            }
            return 0;
        }

        // [yabo-launcher fork] `--version` / `-v`: print the build timestamp + AssemblyVersion so the owner can
        // identify the RUNNING build. The build time comes from build-info.json (written by build.ps1 beside the
        // exe); falls back to the exe's LastWriteTime when that file is missing. No banner — clean for piping.
        private int VersionCommand()
        {
            Console.WriteLine($"yabo-launcher {BuildInfo.AssemblyVersion}");
            Console.WriteLine($"build: {BuildInfo.Short()}");
            if (!string.IsNullOrWhiteSpace(BuildInfo.Rid))
                Console.WriteLine($"rid: {BuildInfo.Rid}");
            return 0;
        }

        // [yabo-launcher fork] Persist a per-game cover-art override (the new UI's art picker).
        private int SetArtCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --set-art \"<name>\" \"<url>\"");
            var name = args[1]; var url = args[2];
            var game = _gameManager?.AllGames.FirstOrDefault(g => string.Equals(g?.Name, name, StringComparison.OrdinalIgnoreCase));
            var folder = game?.FolderName;
            if (string.IsNullOrWhiteSpace(folder)) return PrintError($"Unknown port: {name}");
            var settings = AppSettings.Load();
            settings.ArtOverrides[folder] = url;
            AppSettings.Save(settings);
            Console.WriteLine($"OK set-art {folder}");
            return 0;
        }

        // [yabo-launcher fork] Append a SteamGridDB artist to favorite-artists.json ("prefer this artist").
        private int AddArtFavoriteCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --add-art-favorite <steam64> [name]");
            var id = args[1]; var nm = args.Length > 2 ? args[2] : string.Empty;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "favorite-artists.json");
                var node = File.Exists(path) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) : new System.Text.Json.Nodes.JsonObject();
                if (node!["artists"] is not System.Text.Json.Nodes.JsonArray arr) { arr = new System.Text.Json.Nodes.JsonArray(); node["artists"] = arr; }
                if (!arr.Any(n => n?["steam64"]?.GetValue<string>() == id))
                {
                    arr.Add(new System.Text.Json.Nodes.JsonObject { ["steam64"] = id, ["name"] = nm });
                    File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                Console.WriteLine($"OK add-art-favorite {id}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"add-art-favorite failed: {ex.Message}"); }
        }

        // [yabo-launcher fork] Generic setting writer for the new UI's Settings modal (keys the engine reads).
        private int SetSettingCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --set-setting <key> <value>");
            var key = args[1].ToLowerInvariant(); var value = args[2];
            var s = AppSettings.Load();
            switch (key)
            {
                case "steamgriddbkey": s.SteamGridDbApiKey = value; break;
                case "githubtoken": s.GitHubApiToken = value; break;
                case "librarypath": s.LibraryPath = value; break;
                case "platform":
                    // [yabo-launcher fork] GLOBAL platform (Auto|Windows|MacOS|LinuxX64|LinuxARM64). Per-app
                    // overrides go through --set-platform (AppSettings.PlatformOverrides).
                    if (!Enum.TryParse<TargetOS>(value, ignoreCase: true, out var gos))
                        return PrintError("platform must be one of: Auto | Windows | MacOS | LinuxX64 | LinuxARM64");
                    s.Platform = gos;
                    break;
                default: return PrintError($"Unknown setting: {key}");
            }
            AppSettings.Save(s);
            Console.WriteLine($"OK set-setting {key}");
            return 0;
        }

        /// <summary>[yabo-launcher fork] `--steam-presence-mode [off|shortcut|retroarch]`. With no argument,
        /// prints the current mode. With an argument, validates + persists it. Only "retroarch" arms the
        /// launch-time RetroArch presence puppet (see RetroArchPresenceService); "shortcut"/"off" do nothing
        /// at launch.</summary>
        private int SteamPresenceModeCommand(string[] args)
        {
            var s = AppSettings.Load();
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                // No arg → print current.
                Console.WriteLine(string.IsNullOrWhiteSpace(s.SteamPresenceMode) ? "off" : s.SteamPresenceMode.ToLowerInvariant());
                return 0;
            }

            var mode = args[1].Trim().ToLowerInvariant();
            if (mode != "off" && mode != "shortcut" && mode != "retroarch")
                return PrintError("steam-presence-mode must be one of: off | shortcut | retroarch");

            s.SteamPresenceMode = mode;
            AppSettings.Save(s);
            Console.WriteLine(mode);
            return 0;
        }

        // [yabo-launcher fork] Locate a snesrev port's config .ini inside its install root: prefer the file
        // matching the selected/declared exe basename (smw.ini/zelda3.ini/sm.ini), else the single *.ini that
        // contains a "[KeyMap]" section. Returns null if the port has no parseable config ini.
        private string? FindConfigIni(GameInfo game)
        {
            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName)) return null;
            var dir = game.GetInstallPath(gamesFolder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            // 1) <exe-basename>.ini in the install root (the snesrev convention).
            var exeBasename = !string.IsNullOrWhiteSpace(game.ExecutableName)
                ? Path.GetFileNameWithoutExtension(game.ExecutableName)
                : null;
            if (string.IsNullOrWhiteSpace(exeBasename))
            {
                var firstExe = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (firstExe != null) exeBasename = Path.GetFileNameWithoutExtension(firstExe);
            }
            if (!string.IsNullOrWhiteSpace(exeBasename))
            {
                var byExe = Path.Combine(dir, exeBasename + ".ini");
                if (File.Exists(byExe)) return byExe;
            }

            // 2) The single *.ini in the root that has a [KeyMap] section.
            foreach (var ini in Directory.EnumerateFiles(dir, "*.ini", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.ReadLines(ini).Any(l => l.Trim().Equals("[KeyMap]", StringComparison.OrdinalIgnoreCase)))
                        return ini;
                }
                catch { /* unreadable — skip */ }
            }
            return null;
        }

        /// <summary>Parse a port's config .ini into the drawer payload {iniName, sections:{section→{key→val}}}.
        /// Returns (null, empty) if the game has no parseable config ini (not installed / no [KeyMap] file).
        /// Shared by --get-config and --list-json so both emit the IDENTICAL shape from one parser.</summary>
        private (string? iniName, Dictionary<string, Dictionary<string, string>> sections) ParseConfigIni(GameInfo? game)
        {
            string? iniName = null;
            // Preserve INI ordering so the drawer renders keys in the file's order.
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            var iniPath = game != null ? FindConfigIni(game) : null;
            if (iniPath != null)
            {
                iniName = Path.GetFileName(iniPath);
                string? curSection = null;
                foreach (var raw in File.ReadAllLines(iniPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        curSection = line[1..^1].Trim();
                        if (!sections.ContainsKey(curSection)) sections[curSection] = new Dictionary<string, string>(StringComparer.Ordinal);
                        continue;
                    }
                    var eq = line.IndexOf('=');
                    if (eq < 0 || curSection == null) continue;
                    var key = line[..eq].Trim();
                    var val = line[(eq + 1)..].Trim();
                    if (key.Length == 0) continue;
                    sections[curSection][key] = val;   // last write wins (active line over earlier ones)
                }
            }
            return (iniName, sections);
        }

        /// <summary>`--get-config "&lt;game&gt;"` — parse the port's config .ini into JSON (sections → key/value)
        /// for the in-app launcher drawer. Emits {"sections":{}} if there's no config ini. Raw UTF-8, no banner.</summary>
        private int GetConfigCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --get-config \"<game>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);

            var (iniName, sections) = ParseConfigIni(game);

            var payload = new { iniName, sections };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return 0;
        }

        /// <summary>[yabo-launcher fork] Rewrite ONE "Section.Key = value" in an INI file IN PLACE (line-based),
        /// preserving comments, ordering, indentation, and the file's newline style. Appends the section/key if
        /// missing. Reports the pre-write value via <paramref name="oldValue"/> (null if the key didn't exist).
        /// Shared by --set-config and the snesrev boot-fullscreen default.</summary>
        /// <summary>[yabo-launcher fork — Tier-1 shaderglue] Read a single Section.Key value from an .ini (active,
        /// uncommented lines only), or null if absent/unreadable. Mirrors WriteIniKeyInPlace's section/key matching.</summary>
        private static string? ReadIniValue(string iniPath, string section, string key)
        {
            try
            {
                bool inSection = false;
                foreach (var raw in File.ReadAllLines(iniPath))
                {
                    var t = raw.Trim();
                    if (t.StartsWith("[") && t.EndsWith("]"))
                    {
                        inSection = t[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection) continue;
                    if (t.StartsWith("#") || t.StartsWith(";") || t.Length == 0) continue;
                    var eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    if (t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                        return t[(eq + 1)..].Trim();
                }
            }
            catch { }
            return null;
        }

        private static void WriteIniKeyInPlace(string iniPath, string section, string key, string value, out string? oldValue)
        {
            var lines = File.ReadAllLines(iniPath).ToList();
            // Match the existing newline style so we don't churn the whole file.
            var rawText = File.ReadAllText(iniPath);
            var nl = rawText.Contains("\r\n") ? "\r\n" : "\n";

            int sectionStart = -1, sectionEnd = lines.Count; // [sectionStart .. sectionEnd) is the section body
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    var sec = t[1..^1].Trim();
                    if (sectionStart < 0 && sec.Equals(section, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionStart = i;
                    }
                    else if (sectionStart >= 0)
                    {
                        sectionEnd = i;
                        break;
                    }
                }
            }

            // [yabo-launcher fork] Capture the OLD value (before writing) for the structured action log.
            oldValue = null;
            bool replaced = false;
            if (sectionStart >= 0)
            {
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("#") || t.StartsWith(";") || t.Length == 0) continue; // keep comments/blanks
                    var eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    var k = t[..eq].Trim();
                    if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        oldValue = t[(eq + 1)..].Trim();   // pre-write value (for old→new auditing)
                        // Preserve the original key spelling + indentation; only swap the value.
                        var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                        lines[i] = $"{indent}{k} = {value}";
                        replaced = true;
                        break;
                    }
                }
            }

            if (!replaced)
            {
                if (sectionStart < 0)
                {
                    // Section missing — append it (with a leading blank for readability).
                    if (lines.Count > 0 && lines[^1].Trim().Length != 0) lines.Add("");
                    lines.Add($"[{section}]");
                    lines.Add($"{key} = {value}");
                }
                else
                {
                    // Section exists but key missing — insert at the end of the section body.
                    int insertAt = sectionEnd;
                    // Back up over trailing blank lines so the new key sits with the section's content.
                    while (insertAt - 1 > sectionStart && lines[insertAt - 1].Trim().Length == 0) insertAt--;
                    lines.Insert(insertAt, $"{key} = {value}");
                }
            }

            File.WriteAllText(iniPath, string.Join(nl, lines) + (rawText.EndsWith("\n") ? nl : ""));
        }

        /// <summary>[yabo-launcher fork] FIX: snesrev ports (zelda3/smw/sm — category SNES with a [Graphics]
        /// config) ship their .ini with Fullscreen=0 and so boot windowed. The owner wants boot-fullscreen.
        /// Default the port to DESKTOP fullscreen (1 — NOT mode-change 2) by writing [Graphics] Fullscreen=1
        /// in place, ONLY when it isn't already a non-zero (any user-chosen fullscreen value is respected) and
        /// only for SNES ports that actually have a [Graphics] section. Idempotent + non-fatal. Called on
        /// provide (so it persists for fresh installs) and applied to existing installs.</summary>
        private bool EnsureSnesrevBootFullscreen(GameInfo game)
        {
            if (game == null || !string.Equals(game.Category, "SNES", StringComparison.OrdinalIgnoreCase))
                return false;
            var iniPath = FindConfigIni(game);
            if (iniPath == null) return false;

            try
            {
                var (_, sections) = ParseConfigIni(game);
                if (!sections.TryGetValue("Graphics", out var gfx)) return false; // no [Graphics] — not a snesrev port
                // Respect any non-zero fullscreen the user already chose; only flip a windowed (0/missing) default.
                if (gfx.TryGetValue("Fullscreen", out var cur)
                    && int.TryParse(cur.Trim(), out var curVal) && curVal != 0)
                    return false;

                WriteIniKeyInPlace(iniPath, "Graphics", "Fullscreen", "1", out var oldValue);
                Log.Info($"snesrev boot-fullscreen: '{game.Name}' Graphics.Fullscreen {oldValue ?? "(unset)"} -> 1");
                ActionLog.Write("config-write", game.Name, new
                {
                    file = iniPath,
                    key = "Graphics.Fullscreen",
                    old = oldValue,
                    @new = "1",
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"EnsureSnesrevBootFullscreen failed for '{game.Name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>`--set-config "&lt;game&gt;" "&lt;Section.Key&gt;" "&lt;value&gt;"` — rewrite one key in the port's
        /// config .ini IN PLACE (line-based), preserving comments, ordering, and every other line. If the key
        /// (or its section) is missing it's appended under the section.</summary>
        private int SetConfigCommand(string[] args)
        {
            if (args.Length < 4) return PrintError("Usage: --set-config \"<game>\" \"<Section.Key>\" \"<value>\"");
            var name = args[1].Trim('"', '\'');
            var sectionDotKey = args[2].Trim('"', '\'');
            var value = args[3];   // not trimmed of quotes — the value may legitimately contain them/spaces

            var dot = sectionDotKey.IndexOf('.');
            if (dot <= 0 || dot >= sectionDotKey.Length - 1)
                return PrintError("relativeKey must be \"Section.Key\" (e.g. Graphics.WindowScale).");
            var section = sectionDotKey[..dot].Trim();
            var key = sectionDotKey[(dot + 1)..].Trim();

            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            var iniPath = FindConfigIni(game);
            if (iniPath == null) return PrintError($"No config .ini found for {game.Name}.");

            try
            {
                WriteIniKeyInPlace(iniPath, section, key, value, out var oldValue);
                Console.WriteLine($"OK set-config {section}.{key}");
                Log.Info($"CLI: set-config '{game.Name}' {section}.{key} = {value}");

                // [yabo-launcher fork] Auto-log the config write (key old→new) for debugging/regressions.
                ActionLog.Write("config-write", game.Name, new
                {
                    file = iniPath,
                    key = $"{section}.{key}",
                    old = oldValue,   // null if the key/section didn't exist before
                    @new = value,
                });
                // The built-in GLSL shader is selected by writing Graphics.Shader via --set-config — surface
                // it as a shader-select event too.
                if (string.Equals(section, "Graphics", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(key, "Shader", StringComparison.OrdinalIgnoreCase))
                {
                    var isNone = string.IsNullOrWhiteSpace(value)
                                 || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);
                    ActionLog.Write("shader-select", game.Name, new
                    {
                        kind = isNone ? "none" : "glsl",
                        value = isNone ? "" : value.Trim(),
                    });

                    // [yabo-launcher fork] FIX: the bundled GLSL shaders live in the deploy ROOT's glsl-shaders\
                    // folder, but the snesrev engine resolves Graphics.Shader RELATIVE TO THE GAME'S OWN DIR — so a
                    // relative "glsl-shaders\crt-lottes.glsl" points at a non-existent path next to the game and the
                    // shader silently never loads. When a built-in GLSL shader is selected, COPY the bundled
                    // glsl-shaders\ folder into the port's exe dir (snesrev's "shaders next to the exe" convention)
                    // so the same relative path actually resolves. No-op for "none".
                    if (!isNone)
                    {
                        try { EnsureGlslShadersBesideExe(game); }
                        catch (Exception ex) { Log.Warn($"set-config: copying glsl-shaders for '{game.Name}' failed: {ex.Message}"); }
                    }
                }
                return 0;
            }
            catch (Exception ex) { return PrintError($"set-config failed: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] Copy the bundled glsl-shaders\ folder (in the deploy root, beside the
        /// engine exe) into a snesrev port's exe directory so a relative "glsl-shaders\&lt;file&gt;" in
        /// Graphics.Shader resolves from the game's own dir (the engine resolves shader paths relative to the
        /// game, not the launcher). Idempotent: skips files already present with a matching length. Placed
        /// beside the resolved exe (ResolveExecutableDir) so it works for ports whose exe sits in a subfolder.</summary>
        private void EnsureGlslShadersBesideExe(GameInfo game)
        {
            var src = Path.Combine(AppContext.BaseDirectory, "glsl-shaders");
            if (!Directory.Exists(src)) { Log.Warn("EnsureGlslShadersBesideExe: bundled glsl-shaders\\ not found beside the engine."); return; }

            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName)) return;
            var installDir = game.GetInstallPath(gamesFolder);
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return;

            // Place next to the resolved exe (mirrors data placement) so the relative path the engine resolves
            // from the exe's dir lands on these files.
            var exeDir = game.ResolveExecutableDir(installDir);
            if (string.IsNullOrWhiteSpace(exeDir)) exeDir = installDir;

            var dest = Path.Combine(exeDir, "glsl-shaders");
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                return; // running game already sits in the deploy root (unlikely) — nothing to copy.

            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(src))
            {
                var target = Path.Combine(dest, Path.GetFileName(file));
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length) continue;
                File.Copy(file, target, overwrite: true);
            }
            Log.Info($"set-config: ensured glsl-shaders\\ beside '{game.Name}' exe at {dest}");
        }

        // ── Harbour Masters / libultraship JSON config (Ship of Harkinian, Starship, Ghostship, 2Ship, SpaghettiKart) ──
        // These ports DON'T use the snesrev .ini handled above; they keep a JSON config beside the exe (e.g.
        // shipofharkinian.json), GENERATED on first run. The settings the drawer touches live under CVars.* / Window.*.

        /// <summary>Known libultraship config filenames, by HM folderName suffix. Used when the file doesn't exist
        /// yet (port not launched) so callers can still name the expected file; the live finder below doesn't need it.</summary>
        private static readonly Dictionary<string, string> HmJsonByFolderSuffix = new(StringComparer.OrdinalIgnoreCase)
        {
            ["shipwright"] = "shipofharkinian.json",
            ["2ship2harkinian"] = "2ship2harkinian.json",
            ["starship"] = "starship.json",
            ["ghostship"] = "ghostship.json",
            ["spaghettikart"] = "spaghettikart.json",
        };

        /// <summary>Locate a libultraship port's JSON config inside its install root. Strategy: (1) the conventional
        /// name for this folder (shipofharkinian.json …) if present; (2) any *.json in the root whose top-level has a
        /// "CVars" object (the definitive libultraship signature). Returns null if the port hasn't generated one yet.</summary>
        private string? FindConfigJson(GameInfo game)
        {
            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName)) return null;
            var dir = game.GetInstallPath(gamesFolder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            // 1) Conventional name for this HM folder (folderName-based), e.g. <install>\shipofharkinian.json.
            var suffix = game.FolderName.Contains('.') ? game.FolderName[(game.FolderName.LastIndexOf('.') + 1)..] : game.FolderName;
            if (HmJsonByFolderSuffix.TryGetValue(suffix, out var known))
            {
                var byName = Path.Combine(dir, known);
                if (File.Exists(byName)) return byName;
            }

            // 2) Any *.json in the root that LOOKS like a libultraship config (top-level "CVars" object).
            foreach (var json in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(json));
                    if (node is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey("CVars")) return json;
                }
                catch { /* not valid JSON / unreadable — skip */ }
            }
            return null;
        }

        /// <summary>Read a dotted path (e.g. "CVars.gEnhancements.Widescreen") out of a JsonNode tree as a boxed
        /// scalar (bool/long/double/string), or null if any segment is missing / not an object along the way.</summary>
        private static object? ReadJsonPath(System.Text.Json.Nodes.JsonNode? root, string dottedPath)
        {
            var cur = root;
            foreach (var seg in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cur is not System.Text.Json.Nodes.JsonObject o || !o.TryGetPropertyValue(seg, out cur) || cur == null)
                    return null;
            }
            if (cur is System.Text.Json.Nodes.JsonValue val)
            {
                if (val.TryGetValue<bool>(out var b)) return b;
                if (val.TryGetValue<long>(out var l)) return l;
                if (val.TryGetValue<double>(out var d)) return d;
                if (val.TryGetValue<string>(out var s)) return s;
            }
            return null;   // object/array — the curated groups below only surface scalars
        }

        /// <summary>`--get-config-json "&lt;game&gt;" [--subset &lt;group&gt;]` — locate the port's libultraship JSON
        /// beside the exe, parse it, and emit curated groups (Display/Audio/Enhancements/Controllers) as UTF-8 JSON
        /// (no banner). If the file doesn't exist yet → {"status":"not_found","reason":"port_not_launched_yet"}.</summary>
        private int GetConfigJsonCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --get-config-json \"<game>\" [--subset <group>]");

            // Pull an optional "--subset <group>" flag out, then the remaining tokens are the game name.
            string? subset = null;
            var rest = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--subset", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                { subset = args[++i].Trim('"', '\''); continue; }
                rest.Add(args[i]);
            }
            var name = string.Join(" ", rest).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");

            var jsonPath = FindConfigJson(game);
            if (jsonPath == null)
                return WriteJsonStdout(new { status = "not_found", reason = "port_not_launched_yet" });

            System.Text.Json.Nodes.JsonNode? root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jsonPath)); }
            catch (Exception ex) { return PrintError($"get-config-json failed to parse {Path.GetFileName(jsonPath)}: {ex.Message}"); }

            // Curated groups → each is a list of {path, value} the drawer maps to a control. Paths are the FULL
            // dotted path --set-config-json expects. A path absent from the (possibly sparse) config comes back null;
            // the UI shows the control's default and writing it creates the parents.
            object Field(string path) => new { path, value = ReadJsonPath(root, path) };

            var groups = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Display"] = new[]
                {
                    Field("Window.Width"),
                    Field("Window.Height"),
                    Field("Window.Fullscreen.Enabled"),
                    Field("Window.Fullscreen.Width"),
                    Field("Window.Fullscreen.Height"),
                    Field("CVars.gSdlWindowedFullscreen"),
                },
                ["Audio"] = new[]
                {
                    Field("CVars.gVolume"),
                    Field("CVars.gAudioMuted"),
                    Field("Window.AudioBackend"),
                },
                ["Enhancements"] = new[]
                {
                    Field("CVars.gEnhancements.Widescreen"),
                    Field("CVars.gEnhancements.FOV"),
                    Field("CVars.gEnhancements.DisableBlackBars"),
                    Field("CVars.gInternalResolution"),
                    Field("CVars.gMSAAValue"),
                },
                ["Controllers"] = new[]
                {
                    Field("CVars.gRumble"),
                    Field("CVars.gRumbleStrength"),
                    Field("CVars.gSettings.Controllers.RumbleMappings.P0.HighFrequencyIntensity"),
                    Field("CVars.gSettings.Controllers.RumbleMappings.P0.LowFrequencyIntensity"),
                },
            };

            if (subset != null)
            {
                if (!groups.TryGetValue(subset, out var only))
                    return PrintError($"Unknown subset '{subset}'. Valid: {string.Join(", ", groups.Keys)}");
                return WriteJsonStdout(new { status = "ok", jsonName = Path.GetFileName(jsonPath), group = subset, fields = only });
            }

            return WriteJsonStdout(new { status = "ok", jsonName = Path.GetFileName(jsonPath), groups });
        }

        /// <summary>`--set-config-json "&lt;game&gt;" "&lt;dotted.path&gt;" "&lt;value&gt;"` — rewrite ONE value in the
        /// port's libultraship JSON IN PLACE using JsonNode (structure/format-preserving — we edit the parsed tree and
        /// re-serialize it, never round-tripping through a POCO). Drills the dotted path, creating missing parents, and
        /// coerces the new value's type from the CURRENT value (bool/int/float/string).</summary>
        private int SetConfigJsonCommand(string[] args)
        {
            if (args.Length < 4) return PrintError("Usage: --set-config-json \"<game>\" \"<dotted.path>\" \"<value>\"");
            var name = args[1].Trim('"', '\'');
            var dottedPath = args[2].Trim('"', '\'');
            var value = args[3];   // raw — coerced below against the existing node's type

            if (string.IsNullOrWhiteSpace(dottedPath) || dottedPath.StartsWith('.') || dottedPath.EndsWith('.'))
                return PrintError("dotted.path must be like CVars.gEnhancements.Widescreen");

            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            var jsonPath = FindConfigJson(game);
            if (jsonPath == null) return PrintError($"No libultraship JSON config found for {game.Name} (run it once to generate it).");

            try
            {
                var rawText = File.ReadAllText(jsonPath);
                var indented = rawText.Contains("\n    ") || rawText.Contains("\n\t");   // match the file's pretty-printing
                var root = System.Text.Json.Nodes.JsonNode.Parse(rawText) as System.Text.Json.Nodes.JsonObject
                           ?? throw new InvalidOperationException("config root is not a JSON object");

                var segs = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
                // Walk/create parent objects down to the leaf's container.
                var parent = root;
                for (int i = 0; i < segs.Length - 1; i++)
                {
                    if (parent[segs[i]] is System.Text.Json.Nodes.JsonObject child) parent = child;
                    else
                    {
                        var created = new System.Text.Json.Nodes.JsonObject();
                        parent[segs[i]] = created;   // overwrites a non-object placeholder; creates a missing one
                        parent = created;
                    }
                }
                var leaf = segs[^1];

                // Coerce the new value's type from the CURRENT value if present, else infer from the literal.
                var existing = parent[leaf];
                System.Text.Json.Nodes.JsonNode newNode = CoerceJsonValue(existing, value);
                parent[leaf] = newNode;

                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = indented,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                File.WriteAllText(jsonPath, root.ToJsonString(opts) + (rawText.EndsWith("\n") ? "\n" : ""));

                Console.WriteLine($"OK set-config-json {dottedPath} = {value}");
                Log.Info($"CLI: set-config-json '{game.Name}' {dottedPath} = {value}");
                ActionLog.Write("config-write-json", game.Name, new { file = jsonPath, path = dottedPath, @new = value });
                return 0;
            }
            catch (Exception ex) { return PrintError($"set-config-json failed: {ex.Message}"); }
        }

        /// <summary>Build a JsonValue for <paramref name="raw"/>, typed to match the existing node where one exists,
        /// else inferred from the literal (true/false → bool, integer → long, decimal → double, else string).</summary>
        private static System.Text.Json.Nodes.JsonNode CoerceJsonValue(System.Text.Json.Nodes.JsonNode? existing, string raw)
        {
            // Match the existing leaf's type so we don't silently change a bool into a string, etc.
            if (existing is System.Text.Json.Nodes.JsonValue ev)
            {
                if (ev.TryGetValue<bool>(out _))
                {
                    if (bool.TryParse(raw, out var b)) return System.Text.Json.Nodes.JsonValue.Create(b);
                    if (raw == "1") return System.Text.Json.Nodes.JsonValue.Create(true);
                    if (raw == "0") return System.Text.Json.Nodes.JsonValue.Create(false);
                }
                else if (ev.TryGetValue<long>(out _))
                {
                    if (long.TryParse(raw, out var l)) return System.Text.Json.Nodes.JsonValue.Create(l);
                    if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var d2)) return System.Text.Json.Nodes.JsonValue.Create(d2);
                }
                else if (ev.TryGetValue<double>(out _))
                {
                    if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var d)) return System.Text.Json.Nodes.JsonValue.Create(d);
                }
                else if (ev.TryGetValue<string>(out _))
                {
                    return System.Text.Json.Nodes.JsonValue.Create(raw);
                }
            }

            // No existing value to match — infer from the literal.
            if (bool.TryParse(raw, out var ib)) return System.Text.Json.Nodes.JsonValue.Create(ib);
            if (long.TryParse(raw, out var il)) return System.Text.Json.Nodes.JsonValue.Create(il);
            if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var idd)) return System.Text.Json.Nodes.JsonValue.Create(idd);
            return System.Text.Json.Nodes.JsonValue.Create(raw);
        }

        /// <summary>Serialize <paramref name="payload"/> as compact UTF-8 JSON straight to stdout (no banner), the
        /// same raw-bytes path --list-json / --get-config use so the Tauri shell always reads valid UTF-8.</summary>
        private int WriteJsonStdout(object payload)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return 0;
        }

        // ── Winamp .wsz skin loader (engine side) ──────────────────────────────────────────
        // A .wsz is a ZIP of classic Winamp 2.x skin assets (.bmp sprite sheets + pledit.txt /
        // viscolor.txt configs). The WebView2 UI renders the sprites; the engine just extracts
        // them, caches them under skins\<name>\, and emits a manifest with each sheet as a
        // data:image/bmp;base64 URI (Chromium renders BMP natively — no conversion dep needed).

        /// <summary>`--load-skin &lt;path-to-.wsz&gt;` — unzip + cache the skin, record CurrentSkin, emit its manifest.</summary>
        private int LoadSkinCommand(string[] args)
        {
            if (args.Length < 2)
                return PrintError("Usage: --load-skin <path-to-.wsz>");
            var path = string.Join(" ", args.Skip(1)).Trim('"', '\'');
            try
            {
                var manifest = WinampSkinService.LoadSkin(path);
                Log.Info($"CLI: load-skin '{manifest.name}' ({manifest.sprites.Count} sheets)");
                return WriteJsonStdout(manifest);
            }
            catch (Exception ex)
            {
                // Clean one-line error + non-zero exit for missing / invalid / non-zip input.
                return PrintError($"load-skin failed: {ex.Message}");
            }
        }

        /// <summary>`--get-skin [--json]` — emit the CURRENT skin's manifest, or {"name":null} if none set.</summary>
        private int GetSkinCommand(string[] args)
        {
            try
            {
                var manifest = WinampSkinService.GetCurrentSkin();
                if (manifest == null)
                    return WriteJsonStdout(new { name = (string?)null });
                return WriteJsonStdout(manifest);
            }
            catch (Exception ex)
            {
                return PrintError($"get-skin failed: {ex.Message}");
            }
        }

        /// <summary>`--clear-skin` — clear CurrentSkin (UI falls back to its CSS skin). Emits "OK".</summary>
        private int ClearSkinCommand()
        {
            try
            {
                WinampSkinService.ClearSkin();
                Console.WriteLine("OK");
                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"clear-skin failed: {ex.Message}");
            }
        }

        /// <summary>Resolve where a port's config .ini lives — the existing file if present, else the conventional
        /// &lt;exe-basename&gt;.ini in the install root (so --import-config can create one). Null if undeterminable.</summary>
        private string? ResolveConfigIniTarget(GameInfo game)
        {
            var existing = FindConfigIni(game);
            if (existing != null) return existing;
            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName)) return null;
            var dir = game.GetInstallPath(gamesFolder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var exeBasename = !string.IsNullOrWhiteSpace(game.ExecutableName)
                ? Path.GetFileNameWithoutExtension(game.ExecutableName)
                : Path.GetFileNameWithoutExtension(Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? "");
            return string.IsNullOrWhiteSpace(exeBasename) ? null : Path.Combine(dir, exeBasename + ".ini");
        }

        /// <summary>`--export-config "&lt;game&gt;" ["&lt;out.ini&gt;"]` — copy the port's config .ini to &lt;out.ini&gt;, or
        /// print it to stdout if no path is given. Snapshot a tuned setup as a shareable preset (pairs with
        /// --import-config). Raw bytes to stdout when no out path, so it pipes cleanly.</summary>
        private int ExportConfigCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --export-config \"<game>\" [\"<out.ini>\"]");
            // A trailing arg is the OUTPUT path only if it looks like one (ends with .ini); else it's part of the name.
            string name, outPath = "";
            if (args.Length >= 3 && args[^1].Trim('"', '\'').EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            {
                outPath = args[^1].Trim('"', '\'');
                name = string.Join(" ", args[1..^1]).Trim('"', '\'');
            }
            else name = string.Join(" ", args[1..]).Trim('"', '\'');

            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            var iniPath = FindConfigIni(game);
            if (iniPath == null) return PrintError($"No config .ini found for {game.Name}.");

            if (!string.IsNullOrEmpty(outPath))
            {
                try { File.Copy(iniPath, outPath, overwrite: true); }
                catch (Exception ex) { return PrintError($"export-config failed: {ex.Message}"); }
                Console.WriteLine($"OK export-config '{game.Name}' → {outPath}");
                Log.Info($"CLI: export-config '{game.Name}' -> '{outPath}'");
                return 0;
            }
            // No out path → raw .ini bytes to stdout (the literal file, not the JSON --get-config emits).
            var bytes = File.ReadAllBytes(iniPath);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return 0;
        }

        /// <summary>`--import-config "&lt;game&gt;" "&lt;file.ini&gt;"` — replace the port's config .ini with a user-supplied
        /// one (the existing file is backed up to .ini.bak). The config analogue of --import-data: bring your own
        /// settings or apply a shared preset. Validates the source is an INI (has at least one [Section] header).</summary>
        private int ImportConfigCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --import-config \"<game>\" \"<file.ini>\"");
            var src = args[^1].Trim('"', '\'');                          // last arg = the source .ini
            var name = string.Join(" ", args[1..^1]).Trim('"', '\'');    // everything between = the game name
            if (!File.Exists(src)) return PrintError($"Config file not found: {src}");
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");

            string[] srcLines;
            try { srcLines = File.ReadAllLines(src); }
            catch (Exception ex) { return PrintError($"Couldn't read '{Path.GetFileName(src)}': {ex.Message}"); }
            if (!srcLines.Any(l => { var t = l.Trim(); return t.StartsWith("[") && t.EndsWith("]"); }))
                return PrintError($"'{Path.GetFileName(src)}' doesn't look like a config .ini (no [Section] headers).");

            var target = ResolveConfigIniTarget(game);
            if (target == null) return PrintError($"Couldn't determine the config .ini path for {game.Name} (is it installed?).");

            try
            {
                bool hadExisting = File.Exists(target);
                if (hadExisting)
                {
                    File.Copy(target, target + ".bak", overwrite: true);
                    WriteColor($"  · backed up existing config → {Path.GetFileName(target)}.bak", ColorMuted);
                    Console.WriteLine();
                }
                File.Copy(src, target, overwrite: true);
                Console.WriteLine($"OK import-config '{game.Name}' ← {Path.GetFileName(src)}  →  {target}");
                Log.Info($"CLI: import-config '{game.Name}' from '{src}' -> '{target}'");

                // [yabo-launcher fork] Auto-log the whole-file config replacement. There's no single key here;
                // record the source/target and whether a prior config was overwritten (backed up to .bak).
                ActionLog.Write("config-write", game.Name, new
                {
                    file = target,
                    key = "<import-config>",
                    old = hadExisting ? $"{Path.GetFileName(target)}.bak" : null,
                    @new = src,
                });
                return 0;
            }
            catch (Exception ex) { return PrintError($"import-config failed: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] `--library` / `--equipped` — what you're set up to play, derived from the
        /// filesystem (no DB): a game is READY if it needs no data or all its required dataFiles are in its install
        /// dir; ARMABLE if those files are pooled in the Library (one --provide-data away); else it's MISSING the file.</summary>
        private int LibraryCommand(bool json)
        {
            var gamesFolder = _gameManager?.GamesFolder;
            var lib = new RomLibraryService(AppSettings.Load());
            var libFiles = Directory.Exists(lib.LibraryPath)
                ? new HashSet<string>(Directory.EnumerateFiles(lib.LibraryPath).Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var readySelf = new List<string>();   // needs no data
            var readyData = new List<string>();   // required data placed in install dir
            var armable = new List<(string name, string need)>();   // file is in the Library pool, not yet placed
            var missing = new List<(string name, string need)>();   // we don't have the file at all

            foreach (var g in (_gameManager?.AllGames ?? Enumerable.Empty<GameInfo>()).Where(x => x != null && !x.IsExternal && !x.AppComponent))
            {
                var req = (g.DataFiles ?? new List<DataFileNeed>()).Where(d => !d.Optional && !string.IsNullOrEmpty(d.Name)).ToList();
                if (req.Count == 0) { readySelf.Add(g.Name); continue; }
                var installDir = (!string.IsNullOrEmpty(gamesFolder) && !string.IsNullOrEmpty(g.FolderName)) ? g.GetInstallPath(gamesFolder!) : null;
                bool InInstall(DataFileNeed d) => installDir != null && File.Exists(Path.Combine(installDir, d.Name!));
                if (req.All(InInstall)) { readyData.Add(g.Name); continue; }
                var notPlaced = req.Where(d => !InInstall(d)).ToList();
                if (notPlaced.All(d => libFiles.Contains(d.Name!)))
                    armable.Add((g.Name, string.Join(", ", notPlaced.Select(d => d.Name))));
                else
                    missing.Add((g.Name, string.Join(", ", notPlaced.Where(d => !libFiles.Contains(d.Name!)).Select(d => d.Name))));
            }

            if (json)
            {
                var payload = new
                {
                    ready = readySelf.Concat(readyData).ToList(),
                    readyWithData = readyData,
                    armable = armable.Select(a => new { a.name, a.need }).ToList(),
                    missing = missing.Select(m => new { m.name, m.need }).ToList(),
                };
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload));
                return 0;
            }

            Console.WriteLine();
            WriteColor($"  Library pool: {lib.LibraryPath}  ({libFiles.Count} file(s))", ColorMuted); Console.WriteLine(); Console.WriteLine();
            WriteColor($"  > READY TO PLAY: {readySelf.Count + readyData.Count}  ({readySelf.Count} self-contained, {readyData.Count} with staged data)", ColorSuccess); Console.WriteLine();
            foreach (var n in readyData) Console.WriteLine($"      {n}");
            Console.WriteLine();
            WriteColor($"  ~ IN LIBRARY (one --provide-data to arm): {armable.Count}", ColorWarning); Console.WriteLine();
            foreach (var (name, need) in armable) Console.WriteLine($"      {name}   (need: {need})");
            Console.WriteLine();
            WriteColor($"  x NEED THE FILE: {missing.Count}", ColorError); Console.WriteLine();
            foreach (var (name, need) in missing) Console.WriteLine($"      {name}   (missing: {need})");
            Console.WriteLine();
            return 0;
        }

        private async Task<int> ListGames()
        {
            if (_gameManager?.Games == null || !_gameManager.Games.Any())
                return PrintError("No games found in library.");

            int maxNameLength = _gameManager.Games.Max(g => g?.Name?.Length ?? 10) + 4;
            if (maxNameLength < 30) maxNameLength = 30;

            Console.WriteLine();
            WriteColor("Available Apps:", ColorTitle);
            Console.WriteLine();
            Console.WriteLine();

            foreach (var game in _gameManager.Games.OrderBy(g => g?.Name))
            {
                if (game == null) continue;

                string name = game.Name ?? "Unknown";
                Console.Write($"  {name.PadRight(maxNameLength)} ");

                string version = CleanVersion(game.LatestVersion);
                string installedVer = CleanVersion(game.InstalledVersion);

                if (game.IsExternal)
                {
                    WriteColor("[EXTERNAL]        ", ColorTitle);
                    Console.WriteLine(" ↗ installed separately (link-out)");
                    continue;
                }

                switch (game.Status)
                {
                    case GameStatus.Installed:
                        WriteColor("[INSTALLED]       ", ColorSuccess);
                        Console.WriteLine($" {installedVer}");
                        break;
                    case GameStatus.UpdateAvailable:
                        WriteColor("[UPDATE AVAILABLE]", ColorWarning);
                        Console.WriteLine($" {installedVer} -> {version}");
                        break;
                    default:
                        WriteColor("[NOT INSTALLED]   ", ColorMuted);
                        Console.WriteLine($" Latest: {version}");
                        break;
                }
            }

            Console.WriteLine();
            return 0;
        }

        private async Task<int> RunGame(string gameName, string? romOverride = null)
        {
            Log.Info("CLI: --run");
            if (string.IsNullOrEmpty(gameName))
            {
                ShowHelp();
                return PrintError("App name required.");
            }

            var game = FindGame(gameName);
            if (game == null)
            {
                Console.WriteLine();
                WriteColor("Available games: ", ColorMuted);
                Console.WriteLine(string.Join(", ", _gameManager?.Games.Select(g => g.Name) ?? Array.Empty<string>()));
                Console.WriteLine();
                return PrintError($"App not found: '{gameName}'");
            }

            if (game.IsExternal) return HandleExternal(game);

            if (game.Status == GameStatus.NotInstalled)
            {
                return PrintError($"{game.Name} is not installed. Use --download first.");
            }

            // Auto-update if update is available
            if (game.Status == GameStatus.UpdateAvailable)
            {
                WriteColor($"→ Update available for {game.Name}. Updating first...", ColorWarning);
                Console.WriteLine();

                int updateResult = await UpdateOrDownloadGame(game, isUpdate: true);
                if (updateResult != 0) return updateResult;

                // Re-check status after update
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);
            }

            var settings = AppSettings.Load();
            var gamesFolder = _gameManager.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName))
            {
                return PrintError("App folder is not configured.");
            }

            // [yabo-launcher fork] `--run "<name>" --rom "<file>"` — launch-time ROM override. The catalog's
            // LaunchArgs (e.g. SMAS's "smb1.sfc") is replaced for THIS launch only (in-memory; never persisted
            // to apps.json). The file is resolved relative to the install dir; a bare filename or a path under
            // the install dir both work. This powers the SMAS game-picker (smb1.sfc / smbll.sfc / smw).
            if (!string.IsNullOrWhiteSpace(romOverride))
            {
                var installDir = game.GetInstallPath(gamesFolder);
                var romName = Path.GetFileName(romOverride);
                var resolved = Path.IsPathRooted(romOverride) ? romOverride : Path.Combine(installDir, romName);
                if (!File.Exists(resolved))
                    return PrintError($"ROM file not found in install dir: '{romName}' (looked in {installDir}). Stage it first (e.g. --import-data), then retry.");
                // The engine launches with WorkingDirectory = install dir, so pass the bare filename.
                game.LaunchArgs = romName;
                Log.Info($"CLI: --run '{game.Name}' with --rom override '{romName}'.");
            }

            // [yabo-launcher fork] Run any declared build step BEFORE we look for the executable. For
            // "tcc-compile" ports (Super Metroid / snesrev/sm — no prebuilt release, ROM-derived assets
            // baked into the exe at build time), the exe does not exist until we compile it locally with
            // the user's ROM. The executable-selection block below would otherwise bail with "No executable
            // found". RunBuildStepIfNeeded is idempotent (no-ops once the target exists), so the later call
            // in LaunchAsync is harmless. bps-patch ports are unaffected (their exe ships in the release).
            {
                var buildPath = game.GetInstallPath(gamesFolder);
                if (!string.IsNullOrEmpty(buildPath) && Directory.Exists(buildPath) && !game.RunBuildStepIfNeeded(buildPath))
                {
                    return PrintError(
                        $"Couldn't build {game.Name} — provide the ROM first " +
                        $"(--import-data \"{game.Name}\" <rom>), then run again. See log: {Log.LogPath}");
                }
            }

            // Check if need to select executable
            var storedExe = game.LoadSelectedExecutable(gamesFolder);

            if (string.IsNullOrEmpty(storedExe))
            {
                // Check if there are multiple executables
                var gamePath = game.GetInstallPath(gamesFolder);
                GameInfo.EnsureExecutableAtRoot(gamePath);

                var executables = GameInfo.GetExecutableCandidates(gamePath, SearchOption.TopDirectoryOnly, out _);
                if (executables.Count == 0)
                {
                    executables = GameInfo.GetExecutableCandidates(gamePath, SearchOption.AllDirectories, out _);
                }

                if (executables.Count == 0)
                {
                    return PrintError($"No executable found for {game.Name} in {gamePath}.\nThe game may not have installed correctly, or it's an unsupported format.");
                }
                else if (executables.Count > 1)
                {
                    // [yabo-launcher fork] If the catalog pins which exe to launch (multi-exe releases like
                    // NBlood → nblood/rednukem/pcexhumed), auto-select it so headless --run works without the
                    // old GUI executable picker.
                    var pinnedExe = !string.IsNullOrWhiteSpace(game.ExecutableName)
                        ? executables.FirstOrDefault(e => string.Equals(Path.GetFileName(e), game.ExecutableName, StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (pinnedExe != null)
                    {
                        game.SelectedExecutable = pinnedExe;
                        game.SaveSelectedExecutable(pinnedExe, gamesFolder);
                        Log.Info($"Auto-selected pinned exe '{game.ExecutableName}' for '{game.Name}'.");
                    }
                    else
                    {
                        // [yabo-launcher fork] No pinned exe — emit a machine-readable signal so the Tauri UI
                        // shows its OWN exe picker (--list-exes → --set-exe). The old Avalonia GUI picker is gone.
                        Console.Error.WriteLine("MULTIPLE_EXECUTABLES");
                        WriteColor($"Multiple executables for {game.Name} — choose one (the UI will prompt).", ColorWarning);
                        return 2;
                    }
                }
                else if (executables.Count == 1)
                {
                    game.SelectedExecutable = executables[0];
                    game.SaveSelectedExecutable(game.SelectedExecutable, gamesFolder);
                }
            }

            WriteColor($"→ Launching {game.Name}...", ColorSuccess);
            Console.WriteLine();
            Console.WriteLine();

            var launchedProcessSource = new TaskCompletionSource<Process?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnGameProcessStarted(Process? process) => launchedProcessSource.TrySetResult(process);

            // [yabo-launcher fork] OPTIONAL Steam Rich Presence puppet. When SteamPresenceMode=="retroarch",
            // spawn RetroArch (Steam appid 1118310) with a no-op core + a dummy "<game>.zip" BEFORE launching
            // the real game, so Steam shows "Playing <game>" while our standalone game runs. We kill it after
            // the game session ends. "shortcut"/"off" arm no launch-time puppet. Best-effort: a puppet failure
            // never blocks the real launch.
            Process? presencePuppet = null;
            if (string.Equals(settings.SteamPresenceMode, "retroarch", StringComparison.OrdinalIgnoreCase))
            {
                presencePuppet = GithubLauncher.Services.RetroArchPresenceService.StartPuppet(game.Name ?? "Game");
            }

            try
            {
                game.GameProcessStarted += OnGameProcessStarted;
                var launchStart = DateTime.UtcNow;
                await game.PerformActionAsync(
                    _gameManager.HttpClient,
                    gamesFolder,
                    settings);

                game.GameProcessStarted -= OnGameProcessStarted;

                if (launchedProcessSource.Task.IsCompletedSuccessfully)
                {
                    await WaitForLaunchedGameSessionAsync(launchedProcessSource.Task.Result);
                }

                // [yabo-launcher fork] Game session ended → tear down the presence puppet (if any).
                GithubLauncher.Services.RetroArchPresenceService.StopPuppet(presencePuppet);
                presencePuppet = null;

                // [yabo-launcher fork] Capture the port's own crash/log files into OUR log NOW —
                // synchronously, before this CLI process exits. (The launch-path's fire-and-forget
                // Task.Run gets killed when --run returns the instant the game session ends, which is
                // why our log never showed zelda3.log.) CapturePortLogs scans the install dir
                // recursively, so it catches the Harbour Masters family's logs/<name>.log subfolder
                // AND RadzPrower's <root>/zelda3.log.
                try
                {
                    var installDir = game.GetInstallPath(gamesFolder);
                    if (!string.IsNullOrEmpty(installDir))
                        GameInfo.CapturePortLogs(installDir, launchStart, game.Name);
                    // [yabo-launcher fork] Also flush the captured stdout/stderr + sweep external log dirs
                    // synchronously here — the launch path's fire-and-forget exit handler is killed the
                    // instant --run returns, so in headless mode the CLI must dump these itself.
                    game.FlushCapturedConsoleAndExternalLogs();
                }
                catch { /* best-effort log capture */ }

                return 0;
            }
            catch (Exception ex)
            {
                game.GameProcessStarted -= OnGameProcessStarted;
                // [yabo-launcher fork] Don't leave the presence puppet running if the real launch threw.
                GithubLauncher.Services.RetroArchPresenceService.StopPuppet(presencePuppet);
                return PrintError($"Failed to launch {game.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-applies Doom/Quake category wiring for already-installed apps (the same code that runs
        /// after a fresh install). Useful when tools were installed before a wiring fix, or to pick up
        /// newly-detected Steam content. Idempotent.
        /// </summary>
        private int RewireCategories()
        {
            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder))
                return PrintError("Game manager not initialized.");

            WriteColor("→ Re-applying Doom/Quake category wiring...", ColorTitle);
            Console.WriteLine();

            CategoryWiring.WireDoomLauncher(_gameManager, gamesFolder, null);
            CategoryWiring.WireQuakeInjector(_gameManager, gamesFolder);
            CategoryWiring.WireDoomseeker(gamesFolder); // best-effort: only if Doomseeker is present/installed

            WriteColor("✓ Done: Doom source ports + Steam IWADs into DoomLauncher; QuakeInjector engine + Steam Quake basedir; Doomseeker WAD paths (if present).", ColorSuccess);
            Console.WriteLine();
            WriteColor("  Restart DoomLauncher / QuakeInjector if they are open.", ColorMuted);
            Console.WriteLine();
            WriteColor($"  Log: {Log.LogPath}", ColorMuted);
            Console.WriteLine();
            return 0;
        }

        /// <summary>
        /// `--import-data "&lt;port&gt;" "&lt;rom file&gt;"` — stages an arbitrary ROM file into the Library
        /// under the port's expected data-file name, then places it into the port. Closes the fully-CLI
        /// rapid-test loop (download → import-data → run) and is drivable by the Playnite plugin. Only
        /// works for ports that declare dataFiles in the catalog (otherwise there's no defined placement).
        /// </summary>
        /// <summary>
        /// Handles a catalog entry that is NOT managed by yabo-launcher (has an externalUrl) — e.g. the
        /// Zandronum + Doomseeker bundle or the GTA III / Vice City community ports. We don't download,
        /// own, or update these; we just point the user to a good place to grab a port and open it in the
        /// browser. The Playnite extension reads IsExternal/ExternalUrl to hand the URL off the same way.
        /// </summary>
        private int HandleExternal(GameInfo game)
        {
            Log.Info($"CLI: external entry '{game.Name}' -> {game.ExternalUrl}");
            Console.WriteLine();
            WriteColor($"'{game.Name}' isn't managed by yabo-launcher.", ColorWarning);
            Console.WriteLine();
            Console.WriteLine("  We don't download, own, or update it — getting it + keeping it current is on you.");
            Console.WriteLine("  We're just pointing you to a good place to grab a port:");
            Console.WriteLine($"    {game.ExternalUrl}");
            Console.WriteLine("  Opening it in your browser...");
            Console.WriteLine();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(game.ExternalUrl!) { UseShellExecute = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    System.Diagnostics.Process.Start("xdg-open", game.ExternalUrl!);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    System.Diagnostics.Process.Start("open", game.ExternalUrl!);
            }
            catch (Exception ex)
            {
                Log.Error($"HandleExternal: failed to open {game.ExternalUrl}", ex);
                return PrintError("Couldn't open the browser; copy the URL above manually.");
            }
            return 0;
        }

        /// <summary>
        // [yabo-launcher fork] Pull "--flag <value>" out of args (first match); returns the value (null if absent)
        // and args with both tokens removed. Lets --play accept optional --data/--config/--shader in any order.
        private static (string? value, string[] rest) ExtractFlag(string[] args, string flag)
        {
            for (int i = 1; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return (args[i + 1].Trim('"', '\''), args.Take(i).Concat(args.Skip(i + 2)).ToArray());
            return (null, args);
        }

        /// <summary>
        /// `--play "&lt;port&gt;" [file] [--data &lt;file&gt;] [--config &lt;settings.ini&gt;] [--shader &lt;preset|none&gt;] [--rom &lt;launch-rom&gt;]`
        /// — THE unified Play action (Playnite Play / a Steam custom shortcut). One verb, everything else optional and
        /// order-independent: checks the catalog, downloads if needed, stages data, applies a config .ini, then
        /// launches. Idempotent end-to-end. External entries route to the browser. Not every flag applies
        /// to every game (most have no config .ini) — unused flags are simply skipped.
        /// </summary>
        private async Task<int> PlayCommand(string[] args)
        {
            Log.Info("CLI: --play");
            if (args.Length < 2)
                return PrintError("Usage: --play \"<port>\" [file] [--data <file>] [--config <settings.ini>] [--rom <launch-rom>]");

            // Optional flags (any order): --config <ini>, --data <file>, --rom <launch-rom>.
            var (configFile, r1) = ExtractFlag(args, "--config");
            var (dataFlag,   r3) = ExtractFlag(r1, "--data");
            var (_, romOverride) = SplitRomModifier(r3);           // optional --rom launch-time override
            var rest = StripRomModifier(r3).Skip(1).ToArray();
            // data file: explicit --data wins; else the legacy trailing-File.Exists form (unquoted names still parse).
            string? file = dataFlag;
            bool hasFile = file != null;
            string portName;
            if (hasFile) portName = string.Join(" ", rest).Trim('"', '\'');
            else
            {
                bool trailing = rest.Length >= 2 && File.Exists(rest[^1].Trim('"', '\''));
                if (trailing) { file = rest[^1].Trim('"', '\''); hasFile = true; portName = string.Join(" ", rest[..^1]).Trim('"', '\''); }
                else portName = string.Join(" ", rest).Trim('"', '\'');
            }

            var game = FindGame(portName);
            if (game == null)
                return PrintError($"App not found: '{portName}'");

            if (game.IsExternal)
                return HandleExternal(game);

            // 1) Ensure installed (download if needed), then refresh status so --run sees it.
            if (game.Status != GameStatus.Installed)
            {
                WriteColor($"→ '{game.Name}' not installed — downloading first...", ColorTitle);
                Console.WriteLine();
                int dl = await DownloadGameCommand(portName);
                if (dl != 0) return dl;
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: false);
            }

            // 2) Stage the ROM/data file (only meaningful for ports that declare dataFiles).
            if (hasFile)
            {
                if (game.DataFiles == null || game.DataFiles.Count == 0)
                {
                    WriteColor($"  (note: '{game.Name}' declares no dataFiles — ignoring '{Path.GetFileName(file!)}'.)", ColorMuted);
                    Console.WriteLine();
                }
                else
                {
                    int imp = ImportDataCommand(new[] { "--import-data", portName, file! });
                    if (imp != 0) return imp;
                }
            }

            // 2b) Apply an optional custom config .ini (e.g. the SNES port settings) before launch.
            if (!string.IsNullOrWhiteSpace(configFile))
            {
                int rc = ImportConfigCommand(new[] { "--import-config", portName, configFile! });
                if (rc != 0) return rc;
            }

            // 3) Launch (with the optional --rom launch-time override).
            return await RunGame(portName, romOverride);
        }

        private int ImportDataCommand(string[] args)
        {
            Log.Info("CLI: --import-data");
            if (args.Length < 3)
                return PrintError("Usage: --import-data \"<port>\" \"<rom file path>\"");

            var file = args[^1].Trim('"', '\'');
            var portName = string.Join(" ", args[1..^1]).Trim('"', '\'');
            if (!File.Exists(file))
                return PrintError($"ROM file not found: {file}");

            var game = FindGame(portName);
            if (game == null) return PrintError($"App not found: '{portName}'");
            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder)) return PrintError("App folder is not configured.");

            if (game.DataFiles == null || game.DataFiles.Count == 0)
                return PrintError($"{game.Name} declares no data files in the catalog — no defined place to put a ROM. (Add dataFiles to apps.json for this port.)");

            // [yabo-launcher fork] Pick the target dataFile.
            //   • Single dataFile  → it (unchanged behavior).
            //   • Multiple         → ROUTE BY SHA-1: compute the imported file's SHA-1 and slot it into the
            //     dataFile whose declared sha1 MATCHES — NOT slot 0 (the old bug that mis-routed the SMW ROM
            //     into the smas.sfc slot and failed the hash check). If nothing matches, fail with a clear
            //     error naming the expected ROMs rather than silently slotting it.
            DataFileNeed need;
            if (game.DataFiles.Count == 1)
            {
                need = game.DataFiles[0];
            }
            else
            {
                string fileSha1;
                try { fileSha1 = ComputeSha1Hex(file); }
                catch (Exception ex) { return PrintError($"Couldn't hash '{Path.GetFileName(file)}': {ex.Message}"); }

                var matched = game.DataFiles.FirstOrDefault(d =>
                    !string.IsNullOrWhiteSpace(d.Sha1) &&
                    string.Equals(NormalizeHash(d.Sha1), fileSha1, StringComparison.OrdinalIgnoreCase));

                if (matched == null)
                {
                    var expected = string.Join("\n", game.DataFiles.Select(d =>
                        $"    {d.Name}  (sha1 {d.Sha1 ?? "—"})"));
                    return PrintError(
                        $"'{Path.GetFileName(file)}' (sha1 {fileSha1}) doesn't match any ROM {game.Name} expects.\n" +
                        $"  Provide one of:\n{expected}");
                }
                need = matched;
            }

            var lib = new RomLibraryService(AppSettings.Load());
            WriteColor($"→ Importing '{Path.GetFileName(file)}' as '{need.Name}' into Library ({lib.LibraryPath})...", ColorTitle);
            Console.WriteLine();

            if (lib.ImportAs(file, need.Name!) == null)
                return PrintError("Failed to stage the ROM into the Library (see log).");

            // [yabo-launcher fork] ProvideToPort places EVERY declared dataFile. For a multi-ROM port the user
            // may have brought only one ("bring what you have"), so a miss on ANOTHER slot is expected and must
            // NOT fail this import. We only fail on: (a) the file we just routed/imported failing to place, or
            // (b) a REQUIRED (non-optional) slot failing. A missing/optional sibling is reported as a note.
            var needsByName = game.DataFiles
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .GroupBy(d => d.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(grp => grp.Key, grp => grp.First(), StringComparer.OrdinalIgnoreCase);
            int failures = 0;
            foreach (var r in lib.ProvideToPort(game, gamesFolder))
            {
                if (r.Success) { WriteColor("  ✓ ", ColorSuccess); Console.WriteLine($"{r.Name} - {r.Message}{(r.PlacedPath != null ? $" ({r.PlacedPath})" : "")}"); }
                else
                {
                    bool isRouted = string.Equals(r.Name, need.Name, StringComparison.OrdinalIgnoreCase);
                    bool isOptional = needsByName.TryGetValue(r.Name, out var nd) && nd.Optional;
                    if (isRouted || !isOptional) { failures++; WriteColor("  ✗ ", ColorError); Console.WriteLine($"{r.Name} - {r.Message}"); }
                    else { WriteColor("  · ", ColorMuted); Console.WriteLine($"{r.Name} - not provided (optional — bring it to unlock that game)."); }
                }
            }
            Console.WriteLine();
            Log.Info($"CLI: imported '{file}' -> '{game.Name}' as '{need.Name}' (failures={failures})");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// [iSNESrev — OpenEmu-style drag-in ingest] Identify a dropped ROM by its SHA-1 across the ENTIRE
        /// catalog (the reverse of --import-data, which needs a port name): hash the file, find the card whose
        /// dataFiles declare that sha1, install that port if it isn't yet, then stage + BPS-patch the ROM via the
        /// existing import path. The whole "drag a ROM → you have the game" loop, no port name required. Mirrors
        /// <summary>[OTA feed] Pull the gated CANON catalog from RemoteFeedUrl into apps.json. Dormant when
        /// no feed URL is configured; fail-safe (keeps the last-good local apps.json on any error).</summary>
        private async Task<int> SyncFeedCommand(string[] args)
        {
            var settings = AppSettings.Load();
            // [dev-loop] Optional inline feed URL: `--sync-feed "<url-or-path>"` overrides the saved RemoteFeedUrl
            // for THIS pull (without persisting it) — the one-liner the subscriber test uses. Bare `--sync-feed`
            // uses the configured feed.
            if (args.Length >= 2 && !args[1].StartsWith("--"))
                settings.RemoteFeedUrl = args[1].Trim('"', '\'');

            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            var svc = new Services.FeedSyncService(settings, appsPath);
            if (!svc.IsConfigured)
            {
                Console.WriteLine("Feed sync: no RemoteFeedUrl configured (dormant). Set one with --set-feed \"<url>\" or pass it inline: --sync-feed \"<url>\".");
                return 0;
            }
            var ok = await svc.SyncAsync().ConfigureAwait(false);
            Console.WriteLine(ok
                ? "Feed sync: apps.json updated from the remote feed."
                : "Feed sync: no update (unreachable/invalid) — kept the last-good local apps.json.");
            return ok ? 0 : 1;
        }

        /// <summary>`--set-feed "&lt;url-or-path&gt;" [token]` — persist the subscriber's feed URL (and optional bearer
        /// token) into settings.json. The URL may be an https:// endpoint, a file:// URI, a UNC share, or a plain
        /// local path. Pass an empty string to clear (go dormant).</summary>
        private int SetFeedCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --set-feed \"<url-or-path>\" [token]   (empty url clears it)");
            var settings = AppSettings.Load();
            settings.RemoteFeedUrl = args[1].Trim('"', '\'');
            if (args.Length >= 3) settings.RemoteFeedToken = args[2].Trim('"', '\'');
            AppSettings.Save(settings);
            Console.WriteLine(string.IsNullOrWhiteSpace(settings.RemoteFeedUrl)
                ? "OK set-feed: feed cleared (sync is now dormant)."
                : $"OK set-feed: RemoteFeedUrl = {settings.RemoteFeedUrl}{(string.IsNullOrEmpty(settings.RemoteFeedToken) ? "" : " (token set)")}");
            return 0;
        }

        /// <summary>[itch.io route — OWNER only] `--set-itch-key "&lt;key&gt;"` — persist the itch.io API key into
        /// settings.json (get one at itch.io/user/settings/api-keys). Required to install free itch cards (e.g.
        /// dev tools like GMEdit). Empty clears it. Not surfaced to end users; itch cards are never gated into the feed.</summary>
        private int SetItchKeyCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --set-itch-key \"<key>\"   (empty clears it). Get one at https://itch.io/user/settings/api-keys");
            var settings = AppSettings.Load();
            settings.ItchApiKey = args[1].Trim('"', '\'');
            AppSettings.Save(settings);
            Console.WriteLine(string.IsNullOrWhiteSpace(settings.ItchApiKey)
                ? "OK set-itch-key: cleared (itch route dormant)."
                : "OK set-itch-key: itch API key saved.");
            return 0;
        }

        /// <summary>[link-checker #25] HEAD (fallback 1-byte ranged GET) a URL; prints ALIVE/DEAD. The console's
        /// link sweep consumes /dead/i =&gt; dead, else alive. Never throws.</summary>
        private async Task<int> CheckUrlCommand(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.WriteLine("Usage: --check-url \"<url>\"");
                return 2;
            }
            var url = args[1].Trim().Trim('"');
            bool alive = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("yabo-launcher");
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    using var r = await http.SendAsync(head).ConfigureAwait(false);
                    alive = (int)r.StatusCode < 400;
                }
                catch { alive = false; }

                if (!alive)
                {
                    try
                    {
                        using var get = new HttpRequestMessage(HttpMethod.Get, url);
                        get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                        using var r2 = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                        alive = (int)r2.StatusCode < 400;
                    }
                    catch { alive = false; }
                }
            }
            catch { alive = false; }
            Console.WriteLine(alive ? "ALIVE" : "DEAD");
            return 0;
        }

        /// OpenEmu's hash→identify→import; our sha1-in-dataFiles is the identity DB.
        /// </summary>
        private async Task<int> IngestCommand(string[] args)
        {
            Log.Info("CLI: --ingest");
            if (args.Length < 2)
                return PrintError("Usage: --ingest \"<rom file path>\"");

            var file = string.Join(" ", args[1..]).Trim('"', '\'');
            if (!File.Exists(file))
                return PrintError($"File not found: {file}");

            string sha1;
            try { sha1 = ComputeSha1Hex(file); }
            catch (Exception ex) { return PrintError($"Couldn't hash '{Path.GetFileName(file)}': {ex.Message}"); }

            // Reverse-lookup: which catalog card declares a dataFile with this exact sha1?
            var all = _gameManager?.AllGames ?? new List<GameInfo>();
            GameInfo? match = null; DataFileNeed? matchedNeed = null;
            foreach (var g in all)
            {
                if (g.DataFiles == null) continue;
                var nd = g.DataFiles.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Sha1) &&
                    string.Equals(NormalizeHash(d.Sha1), sha1, StringComparison.OrdinalIgnoreCase));
                if (nd != null) { match = g; matchedNeed = nd; break; }
            }
            if (match == null)
                return PrintError(
                    $"Unrecognized ROM '{Path.GetFileName(file)}' (sha1 {sha1}) — no catalog port expects this file.\n" +
                    "  (Wrong region/revision, a headered copy, or just not a supported game.)");

            WriteColor($"→ Identified '{Path.GetFileName(file)}' as ", ColorTitle);
            WriteColor(match.Name, ColorSuccess);
            Console.WriteLine($"  (slot: {matchedNeed!.Name}).");
            Console.WriteLine();

            // Drag-to-install: pull the port binary first if it isn't installed yet.
            if (match.Status == GameStatus.NotInstalled)
            {
                WriteColor($"→ Installing {match.Name} (port binary)...", ColorMuted);
                Console.WriteLine();
                var dl = await DownloadGameCommand(match.Name);
                if (dl != 0) return dl;   // install failed — DownloadGameCommand already surfaced why
            }

            // Stage the ROM into the port (routes by sha1) + runs any BPS build steps → playable.
            return ImportDataCommand(new[] { "--import-data", match.Name!, file });
        }

        /// <summary>Human labels for the bundled built-in GLSL shaders (keyed by file name, case-insensitive).
        /// Anything in the glsl-shaders folder without a label here still lists with a fallback label.</summary>
        private static readonly Dictionary<string, string> GlslShaderLabels =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["crt-lottes.glsl"] = "CRT Lottes — authentic CRT curve",
                ["crt-geom.glsl"] = "CRT Geom — curved-tube geometry",
                ["scanline.glsl"] = "Scanlines — simple sine scanlines",
                ["sharp-bilinear.glsl"] = "Sharp Bilinear — crisp pixel upscale",
            };

        /// <summary>`--list-glsl-shaders [--json]` — list the built-in GLSL shaders bundled in the
        /// glsl-shaders folder beside the exe. These are single-pass libretro .glsl files the snesrev
        /// OpenGL renderer auto-wraps; the UI dropdown writes Graphics.Shader = glsl-shaders\&lt;file&gt;.</summary>
        private int ListGlslShaders(bool json = false)
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "glsl-shaders");
            var shaders = new List<(string id, string path, string label)>();
            if (System.IO.Directory.Exists(dir))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.glsl*")
                                         .Where(f => f.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase)
                                                  || f.EndsWith(".glslp", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = System.IO.Path.GetFileName(file);
                    var id = System.IO.Path.GetFileNameWithoutExtension(file);
                    var label = GlslShaderLabels.TryGetValue(name, out var l)
                        ? l
                        : id; // fallback: the bare id (e.g. a user-dropped shader)
                    // Relative path snesrev expects in Graphics.Shader (it accepts the '\' separator).
                    shaders.Add((id, "glsl-shaders\\" + name, label));
                }
            }

            if (json)
            {
                var arr = shaders.Select(s => new { id = s.id, path = s.path, label = s.label }).ToList();
                var jsonStr = System.Text.Json.JsonSerializer.Serialize(arr, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                var bytes = System.Text.Encoding.UTF8.GetBytes(jsonStr);
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
                return 0;
            }

            WriteColor("Bundled built-in GLSL shaders (set via Graphics.Shader; snesrev OpenGL renderer):", ColorTitle);
            Console.WriteLine();
            if (shaders.Count == 0)
            {
                Console.WriteLine("  (none found — expected a glsl-shaders folder beside the exe)");
                Console.WriteLine();
                return 0;
            }
            foreach (var s in shaders)
                Console.WriteLine($"  {s.path,-32} {s.label}");
            Console.WriteLine();
            return 0;
        }

        /// <summary>`--resolve-art` — fill missing covers via SteamGridDB into the cache. Respects the freeze:
        /// skips pinned (artUrl) entries and anything already cached; never overwrites approved art.</summary>
        private async Task<int> ResolveArtCommand()
        {
            var settings = AppSettings.Load();
            var sgdb = new SteamGridDbService(_gameManager!.HttpClient, settings.SteamGridDbApiKey);
            if (!sgdb.Enabled)
                return PrintError("No SteamGridDB API key set — add one in Settings → Art.");
            int total = 0, ok = 0;
            foreach (var g in _gameManager.AllGames)
            {
                if (!string.IsNullOrWhiteSpace(g.ArtUrl)) continue; // never touch a pinned cover
                total++;
                try
                {
                    var url = await sgdb.ResolveCoverAsync(g.Name, g.ArtName, true).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(url)) ok++;
                }
                catch (Exception ex) { Log.Warn($"resolve-art '{g.Name}': {ex.Message}"); }
            }
            sgdb.SaveCache();
            Console.WriteLine($"OK resolve-art: {ok}/{total} covers (cache saved).");
            Log.Info($"CLI: resolve-art {ok}/{total}");
            return 0;
        }

        /// <summary>[yabo-launcher fork — capture staging covers → canon] `--sync-art [--only "folderA,folderB"]
        /// [--force]` — BACKFILL the canon's `artUrl` from the owner's RESOLVED cover so subscribers inherit the
        /// owner's exact covers with NO API key. The cdn URL written is a PUBLIC SteamGridDB CDN url
        /// (cdn2.steamgriddb.com), so subscribers fetch it directly — they never need a SteamGridDB key.
        ///
        /// RECOVERY PATH for each card's owner-chosen cdn URL (same precedence the live UI uses in ResolveCover,
        /// minus the local-file custom icon which can't ship):
        ///   1. the owner's per-game ArtOverride (settings.ArtOverrides, set by `--set-art` / the UI art picker) —
        ///      the owner's EXPLICIT pick wins, even over an existing artUrl (that's the owner choosing a better one);
        ///   2. the existing pinned `artUrl` (already correct — kept unless an override supersedes it);
        ///   3. yabo's SteamGridDB cache (Cache/steamgriddb-cache.json), keyed by CleanName(game)/CleanName(port) —
        ///      the resolved cdn URL, INCLUDING the favorite-artist preference baked in at resolve time.
        /// Only http(s) cdn urls are written (a local custom-icon path is skipped — it isn't shippable/public).
        ///
        /// LIMITATION: a cover the owner sets via Playnite's OWN SteamGridDB metadata plugin lives in Playnite's
        /// library db, NOT in yabo's override map or cache — its url is not recoverable here. Such cards fall back
        /// to yabo's own resolution (the cache hit). To pin a specific cover that yabo can ship, set it in yabo
        /// (`--set-art`) or pass it explicitly via `--capture-art "&lt;folder&gt;" "&lt;cdn-url&gt;"`.
        ///
        /// Idempotent: by default only fills artUrl where MISSING (or where an ArtOverride differs from the pinned
        /// value); `--force` also refreshes cards whose artUrl came purely from the cache. apps.json is edited
        /// byte-faithfully (JsonNode) so untouched cards keep their exact formatting/fields.</summary>
        private int SyncArtCommand(string[] args)
        {
            var (onlyRaw, r1) = ExtractFlag(args, "--only");
            bool force = r1.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));

            HashSet<string>? only = null;
            if (!string.IsNullOrWhiteSpace(onlyRaw))
                only = new HashSet<string>(
                    onlyRaw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);

            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");

            // The owner's per-game cover overrides (set via --set-art / the UI art picker) — the explicit picks.
            var settings = AppSettings.Load();
            var userArt = settings.ArtOverrides ?? new Dictionary<string, string>();

            // yabo's resolved-cover cache (folder-independent; keyed by CleanName of the search terms).
            var coverCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var cachePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Cache", "steamgriddb-cache.json");
                if (System.IO.File.Exists(cachePath))
                {
                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(cachePath));
                    if (dict != null) foreach (var kv in dict) coverCache[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex) { Log.Warn($"sync-art: cover cache load failed: {ex.Message}"); }

            static bool IsPublicUrl(string? u) =>
                !string.IsNullOrWhiteSpace(u) &&
                (u!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            // The owner-chosen cdn url for a card: ArtOverride first, else the cache hit (game term, then port term).
            string? OwnerCover(string? name, string? artName, string? folder)
            {
                if (!string.IsNullOrWhiteSpace(folder) && userArt.TryGetValue(folder!, out var ov) && IsPublicUrl(ov))
                    return ov;
                var (port, game) = SteamGridDbService.SearchTerms(name ?? string.Empty, artName);
                foreach (var term in new[] { game, port })
                {
                    var key = SteamGridDbService.CleanName(term);
                    if (!string.IsNullOrWhiteSpace(key) && coverCache.TryGetValue(key, out var url) && IsPublicUrl(url))
                        return url;
                }
                return null;
            }

            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(appsPath))!;
                var apps = root["apps"] as System.Text.Json.Nodes.JsonArray
                           ?? throw new Exception("apps.json has no 'apps' array.");

                int filled = 0, updated = 0, skipped = 0, scanned = 0;
                foreach (var card in apps)
                {
                    if (card is not System.Text.Json.Nodes.JsonObject obj) continue;
                    var folder = (string?)obj["folderName"] ?? "";
                    if (only != null && !only.Contains(folder)) continue;
                    scanned++;

                    var name = (string?)obj["name"];
                    var artName = (string?)obj["artName"];
                    var existing = (string?)obj["artUrl"];

                    // 1) Owner's explicit override always wins (the owner picking a better cover) — even over an
                    //    existing artUrl. If it equals what's already pinned, nothing to do.
                    if (!string.IsNullOrWhiteSpace(folder) && userArt.TryGetValue(folder, out var ov) && IsPublicUrl(ov))
                    {
                        if (string.Equals(existing, ov, StringComparison.Ordinal)) { skipped++; continue; }
                        obj["artUrl"] = ov;
                        if (string.IsNullOrWhiteSpace(existing)) filled++; else updated++;
                        continue;
                    }

                    // 2) A pinned artUrl already exists — keep it unless --force asks us to refresh from the cache.
                    if (!string.IsNullOrWhiteSpace(existing) && !force) { skipped++; continue; }

                    // 3) Fall back to yabo's resolved cover (cache).
                    var cover = OwnerCover(name, artName, folder);
                    if (string.IsNullOrWhiteSpace(cover)) { skipped++; continue; }
                    if (string.Equals(existing, cover, StringComparison.Ordinal)) { skipped++; continue; }
                    obj["artUrl"] = cover;
                    if (string.IsNullOrWhiteSpace(existing)) filled++; else updated++;
                }

                if (filled == 0 && updated == 0)
                {
                    Console.WriteLine($"OK sync-art: nothing to do ({scanned} card(s) scanned, all already carry their cover).");
                    Log.Info($"CLI: sync-art noop scanned={scanned}");
                    return 0;
                }

                File.WriteAllText(appsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"OK sync-art: filled {filled}, updated {updated}, left {skipped} unchanged (of {scanned} scanned).");
                Log.Info($"CLI: sync-art filled={filled} updated={updated} skipped={skipped} scanned={scanned} force={force}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"sync-art failed: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork — capture staging covers → canon] `--capture-art "&lt;folder&gt;" "&lt;cdn-url&gt;"`
        /// — pin an EXPLICIT public cover cdn url onto a single canon card's `artUrl` (matched by folderName, or by
        /// name as a fallback). This is the escape hatch for the limitation above: a cover whose url yabo can't
        /// recover (e.g. set via Playnite's own SteamGridDB plugin) — paste its public cdn url here and it ships
        /// like any other pinned cover. Byte-faithful JsonNode edit. Idempotent (no rewrite if already set).</summary>
        private int CaptureArtCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --capture-art \"<folderName-or-name>\" \"<https cdn url>\"");
            var key = args[1].Trim('"', '\'');
            var url = args[2].Trim('"', '\'');
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return PrintError("capture-art: url must be a public http(s) cover url (subscribers fetch it with no key).");

            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(appsPath))!;
                var apps = root["apps"] as System.Text.Json.Nodes.JsonArray
                           ?? throw new Exception("apps.json has no 'apps' array.");

                System.Text.Json.Nodes.JsonObject? match = null;
                foreach (var card in apps)
                {
                    if (card is not System.Text.Json.Nodes.JsonObject obj) continue;
                    if (string.Equals((string?)obj["folderName"], key, StringComparison.OrdinalIgnoreCase))
                    { match = obj; break; }
                }
                if (match == null)
                    foreach (var card in apps)
                    {
                        if (card is not System.Text.Json.Nodes.JsonObject obj) continue;
                        if (string.Equals((string?)obj["name"], key, StringComparison.OrdinalIgnoreCase))
                        { match = obj; break; }
                    }
                if (match == null) return PrintError($"capture-art: no canon card matches '{key}' (folderName or name).");

                if (string.Equals((string?)match["artUrl"], url, StringComparison.Ordinal))
                {
                    Console.WriteLine($"OK capture-art: '{key}' already pinned to that cover (no change).");
                    return 0;
                }
                match["artUrl"] = url;
                File.WriteAllText(appsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"OK capture-art: pinned cover for '{(string?)match["folderName"] ?? key}'.");
                Log.Info($"CLI: capture-art '{key}' -> {url}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"capture-art failed: {ex.Message}"); }
        }

        /// <summary>`--set-external-path "&lt;game&gt;" "&lt;path&gt;"` — point an external/unmanaged entry at its
        /// installed exe so it becomes launchable (right-click "Point to install folder…").</summary>
        private int SetExternalPathCommand(string[] args)
        {
            if (args.Length < 3)
                return PrintError("Usage: --set-external-path \"<game>\" \"<exe-or-folder path>\"");
            var path = args[^1].Trim('"', '\'');
            var name = string.Join(" ", args[1..^1]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            if (string.IsNullOrWhiteSpace(game.FolderName)) return PrintError("App folder not configured.");
            var settings = AppSettings.Load();
            settings.ExternalLaunchPaths ??= new Dictionary<string, string>();
            settings.ExternalLaunchPaths[game.FolderName] = path;
            AppSettings.Save(settings);
            Console.WriteLine($"OK set-external-path {game.Name}");
            Log.Info($"CLI: set-external-path '{game.Name}' = {path}");
            return 0;
        }

        /// <summary>`--list-exes "&lt;game&gt;"` — JSON list of launchable .exe candidates for a multi-exe port,
        /// with the catalog-pinned one flagged `recommended`. Feeds the UI's exe picker.</summary>
        private int ListExesCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --list-exes \"<game>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            var gamesFolder = _gameManager!.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName))
                return PrintError("App folder not configured.");
            var gamePath = game.GetInstallPath(gamesFolder);
            GameInfo.EnsureExecutableAtRoot(gamePath);
            var exes = GameInfo.GetExecutableCandidates(gamePath, SearchOption.TopDirectoryOnly, out _);
            if (exes.Count == 0) exes = GameInfo.GetExecutableCandidates(gamePath, SearchOption.AllDirectories, out _);
            var arr = exes.Select(p => new
            {
                name = System.IO.Path.GetFileName(p),
                path = p,
                recommended = !string.IsNullOrWhiteSpace(game.ExecutableName)
                    && string.Equals(System.IO.Path.GetFileName(p), game.ExecutableName, StringComparison.OrdinalIgnoreCase),
            }).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(arr, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return 0;
        }

        /// <summary>`--set-exe "&lt;game&gt;" "&lt;exe path&gt;"` — record the user's chosen executable (the UI exe
        /// picker calls this); subsequent --run launches it directly.</summary>
        private int SetExeCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --set-exe \"<game>\" \"<exe path>\"");
            var path = args[^1].Trim('"', '\'');
            var name = string.Join(" ", args[1..^1]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            var gamesFolder = _gameManager!.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder)) return PrintError("Games folder not configured.");
            game.SaveSelectedExecutable(path, gamesFolder);
            Console.WriteLine($"OK set-exe {System.IO.Path.GetFileName(path)}");
            Log.Info($"CLI: set-exe '{game.Name}' = {path}");
            return 0;
        }

        /// <summary>`--recent` — newest-first, deduped list of recently-launched game names (parsed from the
        /// launch log). Feeds the Continue hero + Recently Played view. No new state — reads the log we write.</summary>
        private int RecentCommand()
        {
            var recent = new List<string>();
            try
            {
                var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "yabo-launcher.log");
                if (System.IO.File.Exists(logPath))
                {
                    var lines = System.IO.File.ReadAllLines(logPath);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var rx = new System.Text.RegularExpressions.Regex(@"Launched '(?<n>.+?)' OK");
                    for (int i = lines.Length - 1; i >= 0 && recent.Count < 12; i--)
                    {
                        var m = rx.Match(lines[i]);
                        if (m.Success && seen.Add(m.Groups["n"].Value)) recent.Add(m.Groups["n"].Value);
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"recent: {ex.Message}"); }
            var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(recent,
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length); stdout.Flush();
            return 0;
        }

        /// <summary>[yabo-launcher fork] `--action-log [--json] [--game &lt;name&gt;] [--tail N]` — dump recent events
        /// from the structured JSONL action log (config writes, shader selections, shader-bind verdicts, launches).
        /// Mirrors --recent's "read the log we write" style. Default tail 50. With --json, emits the raw JSONL lines
        /// as a JSON array (for the UI); otherwise prints a readable summary line per event.</summary>
        private int ActionLogCommand(string[] args)
        {
            bool json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
            string? gameFilter = null;
            int tail = 50;
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--game", StringComparison.OrdinalIgnoreCase))
                    gameFilter = args[i + 1].Trim('"', '\'');
                else if (string.Equals(args[i], "--tail", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(args[i + 1], out var n) && n > 0)
                    tail = n;
            }

            var lines = ActionLog.ReadRecent(tail, gameFilter);

            if (json)
            {
                // Each stored line is already a JSON object; wrap them in an array without re-parsing.
                var arrayJson = "[" + string.Join(",", lines) + "]";
                var bytes = System.Text.Encoding.UTF8.GetBytes(arrayJson);
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(bytes, 0, bytes.Length); stdout.Flush();
                return 0;
            }

            Console.WriteLine();
            WriteColor($"Action log ({ActionLog.LogPath})"
                       + (gameFilter != null ? $" — game: {gameFilter}" : "")
                       + $" — last {lines.Count} event(s):", ColorTitle);
            Console.WriteLine();
            Console.WriteLine();
            if (lines.Count == 0)
            {
                WriteColor("  (no events yet)", ColorMuted);
                Console.WriteLine();
                Console.WriteLine();
                return 0;
            }
            foreach (var line in lines)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string ts = root.TryGetProperty("ts", out var t) ? (t.GetString() ?? "") : "";
                    string action = root.TryGetProperty("action", out var a) ? (a.GetString() ?? "") : "";
                    string game = root.TryGetProperty("game", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.String ? g.GetString()! : "";
                    // Build a compact "key=value" tail of the remaining fields.
                    var extras = new List<string>();
                    foreach (var p in root.EnumerateObject())
                    {
                        if (p.Name is "ts" or "action" or "game") continue;
                        extras.Add($"{p.Name}={p.Value}");
                    }
                    Console.Write("  ");
                    WriteColor($"{ts}  ", ColorMuted);
                    WriteColor($"{action,-14} ", ColorTitle);
                    if (!string.IsNullOrEmpty(game)) Console.Write($"[{game}] ");
                    Console.WriteLine(string.Join("  ", extras));
                }
                catch
                {
                    // Malformed line — print it raw rather than dropping it.
                    Console.WriteLine($"  {line}");
                }
            }
            Console.WriteLine();
            return 0;
        }

        /// <summary>`--clear-external-path "&lt;game&gt;"` — forget the user-pointed exe for an external entry.</summary>
        private int ClearExternalPathCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --clear-external-path \"<game>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            var settings = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(game.FolderName) && settings.ExternalLaunchPaths.Remove(game.FolderName))
                AppSettings.Save(settings);
            Console.WriteLine($"OK clear-external-path {game.Name}");
            Log.Info($"CLI: clear-external-path '{game.Name}'");
            return 0;
        }

        /// <summary>`--clear-icon-cache` — wipe the cached app icons (GitHub avatars etc.) so they re-fetch.</summary>
        private int ClearIconCacheCommand()
        {
            try
            {
                var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Cache", "Icons");
                int n = 0;
                if (System.IO.Directory.Exists(dir))
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(dir)) { try { System.IO.File.Delete(f); n++; } catch { } }
                }
                Console.WriteLine($"OK clear-icon-cache ({n} files)");
                Log.Info($"CLI: clear-icon-cache ({n} files)");
                return 0;
            }
            catch (Exception ex) { return PrintError($"clear-icon-cache failed: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] Best "more info / website" URL for an entry: the external/info site,
        /// else the GitHub repo, else the host root of a direct download URL (e.g. rvgl.org, archive.org).</summary>
        private static string SiteFor(GameInfo g)
        {
            if (!string.IsNullOrWhiteSpace(g.ExternalUrl)) return g.ExternalUrl!;
            if (!string.IsNullOrWhiteSpace(g.Repository)) return $"https://github.com/{g.Repository}";
            // [yabo-launcher fork] IA cards carry no externalUrl (managed) — their item page is the
            // archive.org details page synthesized from the IaIdentifier. Insert before the bare
            // download-host-root fallback so IA `website` becomes the real item page (matches RohanKar).
            if (!string.IsNullOrWhiteSpace(g.IaIdentifier)) return $"https://archive.org/details/{g.IaIdentifier}";
            if (!string.IsNullOrWhiteSpace(g.DownloadUrlTemplate)
                && System.Uri.TryCreate(g.DownloadUrlTemplate, System.UriKind.Absolute, out var u))
                return u.GetLeftPart(System.UriPartial.Authority);
            return "";
        }

        /// <summary>`--list-versions "&lt;game&gt;"` — JSON of available releases (tag + latest/installed/preferred/prerelease flags).</summary>
        private async Task<int> ListVersionsCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --list-versions \"<game>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            var releases = await game.FetchReleasesAsync(_gameManager!.HttpClient).ConfigureAwait(false);
            var arr = releases.Select(r => new
            {
                tag = r.tag_name,
                prerelease = r.prerelease,
                latest = !string.IsNullOrWhiteSpace(game.LatestVersion) && string.Equals(r.tag_name, game.LatestVersion, StringComparison.OrdinalIgnoreCase),
                installed = !string.IsNullOrWhiteSpace(game.InstalledVersion) && string.Equals(r.tag_name, game.InstalledVersion, StringComparison.OrdinalIgnoreCase),
                preferred = !string.IsNullOrWhiteSpace(game.PreferredVersion) && string.Equals(r.tag_name, game.PreferredVersion, StringComparison.OrdinalIgnoreCase),
            }).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(arr, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return 0;
        }

        /// <summary>`--install-version "&lt;game&gt;" &lt;tag&gt;` — install a specific release ("Change Version").</summary>
        private async Task<int> InstallVersionCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --install-version \"<game>\" <tag>");
            var tag = args[^1].Trim('"', '\'');
            var name = string.Join(" ", args[1..^1]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            WriteColor($"→ Installing {game.Name} {tag}...", ColorSuccess);
            Console.WriteLine();
            try { await game.InstallVersionAsync(_gameManager!.HttpClient, _gameManager.GamesFolder, tag, AppSettings.Load()).ConfigureAwait(false); }
            catch (Exception ex) { return PrintError($"Install {tag} failed: {ex.Message}"); }
            Console.WriteLine($"OK install-version {game.Name} {tag}");
            Log.Info($"CLI: install-version '{game.Name}' {tag}");
            return 0;
        }

        /// <summary>`--force-update "&lt;game&gt;"` — reinstall the latest release.</summary>
        private async Task<int> ForceUpdateCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --force-update \"<game>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            WriteColor($"→ Force-updating {game.Name}...", ColorSuccess);
            Console.WriteLine();
            try { await game.ForceUpdateAsync(_gameManager!.HttpClient, _gameManager.GamesFolder, AppSettings.Load()).ConfigureAwait(false); }
            catch (Exception ex) { return PrintError($"Force update failed: {ex.Message}"); }
            Console.WriteLine($"OK force-update {game.Name}");
            Log.Info($"CLI: force-update '{game.Name}'");
            return 0;
        }

        /// <summary>`--create-shortcut "&lt;game&gt;"` — desktop .lnk that launches the port via the engine (--run).</summary>
        private int CreateShortcutCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --create-shortcut \"<game>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            if (!OperatingSystem.IsWindows()) return PrintError("Desktop shortcuts are Windows-only.");
            try
            {
                var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var safe = string.Join("_", game.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                var lnk = System.IO.Path.Combine(desktop, safe + ".lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return PrintError("WScript.Shell unavailable.");
                dynamic shell = Activator.CreateInstance(shellType)!;
                var sc = shell.CreateShortcut(lnk);
                sc.TargetPath = exePath;
                sc.Arguments = $"--run \"{game.Name}\"";
                sc.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
                sc.Description = $"Launch {game.Name} via yabo";
                sc.Save();
                Console.WriteLine($"OK shortcut: {lnk}");
                Log.Info($"CLI: create-shortcut '{game.Name}' -> {lnk}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"Create shortcut failed: {ex.Message}"); }
        }

        /// <summary>`--read-text "&lt;path&gt;"` — emit a UTF-8 text file to stdout (the UI loads a picked theme
        /// .css through this, no Tauri fs scope needed). Raw bytes like --list-json.</summary>
        private int ReadTextCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --read-text \"<path>\"");
            var path = string.Join(" ", args[1..]).Trim('"', '\'');
            if (!System.IO.File.Exists(path)) return PrintError($"File not found: {path}");
            var bytes = System.Text.Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(path));
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return 0;
        }

        /// <summary>`--add-app "&lt;owner/repo&gt;" "&lt;name&gt;" "&lt;category&gt;"` — append a user GitHub-release port
        /// to apps.json (Settings → Catalog "Manage apps"). Standard download/launch model applies.</summary>
        private int AddAppCommand(string[] args)
        {
            if (args.Length < 4)
                return PrintError("Usage: --add-app \"<owner/repo>\" \"<display name>\" \"<category>\"");
            var repo = args[1].Trim('"', '\'');
            var name = args[2].Trim('"', '\'');
            var category = args[3].Trim('"', '\'');
            if (!repo.Contains('/')) return PrintError("Repository must be \"owner/repo\".");
            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(appsPath))!;
                var apps = node["apps"]!.AsArray();
                var folderName = repo.Replace('/', '.');
                if (apps.Any(a => (string?)a!["repository"] == repo || (string?)a!["folderName"] == folderName))
                    return PrintError($"'{repo}' is already in the catalog.");
                apps.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = name,
                    ["repository"] = repo,
                    ["folderName"] = folderName,
                    ["category"] = string.IsNullOrWhiteSpace(category) ? "PC" : category,
                });
                System.IO.File.WriteAllText(appsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"OK add-app {name} ({repo})");
                Log.Info($"CLI: add-app {repo}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"add-app failed: {ex.Message}"); }
        }

        /// <summary>Pull a `--flag value` out of <paramref name="args"/> (case-insensitive flag match). Returns the
        /// trimmed value or null when the flag is absent / has no following value. Used by the flag-style CLI
        /// commands (--add-game) instead of positional parsing.
        private static string? GetFlag(string[] args, string flag)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    var v = args[i + 1]?.Trim().Trim('"', '\'');
                    return string.IsNullOrWhiteSpace(v) ? null : v;
                }
            }
            return null;
        }

        /// <summary>
        /// [yabo-launcher fork] `--add-game --repo "&lt;owner/repo&gt;" [--ia "&lt;iaId-or-url&gt;"] [--name "&lt;name&gt;"]
        /// [--category "&lt;cat&gt;"] [--asset-pattern "&lt;regex&gt;"] [--data-files "a.mpq[:sub],b.mpq[:sub:optional]"]`
        /// — append the USER's own port to <c>user-apps.json</c> (the user-additions layer that
        /// <see cref="GameManager.MergeUserCatalogAsync"/> merges ON TOP of canon and that survives OTA). This is the
        /// user path; <c>--add-app</c> is the canon path (writes apps.json). folderName/name are derived from the repo
        /// when omitted. Reuses the same card schema apps.json/user-apps.json share.
        ///
        /// USER-COLLIDE RECIPE: when BOTH a GitHub binary (<c>--repo</c>, optionally pinned with
        /// <c>--asset-pattern</c>) AND Internet-Archive data (<c>--ia</c> + <c>--data-files</c>) are given, the
        /// written card carries the full "marriage" shape — <c>repository</c> + <c>assetPattern</c> +
        /// <c>contentUrl</c>/<c>iaIdentifier</c> + <c>dataFiles</c> + <c>ingest: "place-file"</c> — so on install the
        /// engine downloads the binary, downloads the IA data, and overlays the data onto the binary (the same shape
        /// the Diablo/Sonic-Mania/Prey collide cards use; see <c>GameInfo.EnsureMarriedDataAsync</c>).
        ///
        /// <c>--data-files</c> is a comma-separated list; each entry is <c>name[:targetSubpath[:optional]]</c>
        /// (e.g. <c>"diabdat.mpq:devilutionx,hellfire.mpq:devilutionx:optional"</c>). Prints OK + the folderName.
        /// </summary>
        private int AddGameCommand(string[] args)
        {
            var repo = GetFlag(args, "--repo");
            if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
                return PrintError("Usage: --add-game --repo \"<owner/repo>\" [--ia \"<iaId-or-url>\"] [--name \"<name>\"] [--category \"<cat>\"] [--asset-pattern \"<regex>\"] [--data-files \"name[:subpath[:optional]],...\"]");

            var ia = GetFlag(args, "--ia");
            var name = GetFlag(args, "--name");
            var category = GetFlag(args, "--category");
            var assetPattern = GetFlag(args, "--asset-pattern");
            var dataFilesArg = GetFlag(args, "--data-files");

            // Derive folderName (owner.repo) + a display name (repo segment) from the repo when omitted.
            var folderName = repo.Replace('/', '.');
            if (string.IsNullOrWhiteSpace(name))
                name = repo.Substring(repo.IndexOf('/') + 1).Replace('-', ' ').Replace('_', ' ').Trim();

            // The IA arg may be a bare identifier OR a full archive.org URL. Store the identifier in iaIdentifier
            // and, when given a URL, also as contentUrl (the direct-download field the IA install path reads). A bare
            // identifier goes to iaIdentifier only (the catalog/UI resolves a contentUrl from it).
            string? iaIdentifier = null, contentUrl = null;
            if (!string.IsNullOrWhiteSpace(ia))
            {
                if (ia.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || ia.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    contentUrl = ia;
                    // archive.org/download/<identifier>/... → pull the identifier segment when present.
                    var marker = "/download/";
                    var idx = ia.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var rest = ia.Substring(idx + marker.Length);
                        var slash = rest.IndexOf('/');
                        iaIdentifier = (slash > 0 ? rest.Substring(0, slash) : rest);
                    }
                }
                else
                {
                    iaIdentifier = ia;
                }
            }

            var userAppsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "user-apps.json");
            try
            {
                // user-apps.json is the user's own layer — create it (with an "apps" array) if it doesn't exist yet.
                System.Text.Json.Nodes.JsonNode node;
                if (System.IO.File.Exists(userAppsPath))
                {
                    node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(userAppsPath))
                           ?? new System.Text.Json.Nodes.JsonObject { ["apps"] = new System.Text.Json.Nodes.JsonArray() };
                }
                else
                {
                    node = new System.Text.Json.Nodes.JsonObject { ["apps"] = new System.Text.Json.Nodes.JsonArray() };
                }
                if (node["apps"] is not System.Text.Json.Nodes.JsonArray apps)
                {
                    apps = new System.Text.Json.Nodes.JsonArray();
                    node["apps"] = apps;
                }

                if (apps.Any(a => (string?)a!["repository"] == repo || (string?)a!["folderName"] == folderName))
                    return PrintError($"'{repo}' is already in your user catalog (user-apps.json).");

                var card = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = name,
                    ["repository"] = repo,
                    ["folderName"] = folderName,
                    ["category"] = string.IsNullOrWhiteSpace(category) ? "PC" : category,
                };
                // [yabo-launcher fork] GitHub-binary asset pin (disambiguates a multi-asset release, e.g.
                // "(?i)devilutionx-windows-x86_64\\.zip$"). When set, the install path narrows to the matching asset.
                if (!string.IsNullOrWhiteSpace(assetPattern)) card["assetPattern"] = assetPattern;
                if (!string.IsNullOrWhiteSpace(iaIdentifier)) card["iaIdentifier"] = iaIdentifier;
                if (!string.IsNullOrWhiteSpace(contentUrl)) card["contentUrl"] = contentUrl;

                // [yabo-launcher fork] USER-COLLIDE: parse --data-files into the same dataFiles[] shape the canon
                // collide cards use (Diablo/Sonic Mania/Prey). Each entry "name[:subpath[:optional]]" → an object
                // {name, targetSubpath?, optional?}. The engine's EnsureMarriedDataAsync places each file at
                // <binaryDir>/<targetSubpath>/<name>, overlaying the IA data onto the freshly installed GitHub binary.
                var dataFiles = ParseDataFilesArg(dataFilesArg);
                if (dataFiles.Count > 0)
                {
                    var arr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var (df_name, df_sub, df_opt) in dataFiles)
                    {
                        var dfObj = new System.Text.Json.Nodes.JsonObject { ["name"] = df_name };
                        if (!string.IsNullOrEmpty(df_sub)) dfObj["targetSubpath"] = df_sub;
                        if (df_opt) dfObj["optional"] = true;
                        arr.Add(dfObj);
                    }
                    card["dataFiles"] = arr;
                }

                // [yabo-launcher fork] A binary+data collide (we have a contentUrl AND declared dataFiles) is a
                // "place-file" ingest: install the binary, fetch the IA data, overlay it. Set it so the card matches
                // the canon collide shape; bare GitHub-binary / bare-IA cards leave ingest unset (default behavior).
                if (!string.IsNullOrWhiteSpace(contentUrl) && dataFiles.Count > 0)
                    card["ingest"] = "place-file";

                apps.Add(card);

                System.IO.File.WriteAllText(userAppsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("OK");
                Console.WriteLine(folderName);
                Log.Info($"CLI: add-game {repo} -> user-apps.json (folderName={folderName}"
                         + (iaIdentifier != null ? $", iaIdentifier={iaIdentifier}" : "") + (contentUrl != null ? ", contentUrl set" : "")
                         + (assetPattern != null ? ", assetPattern set" : "") + (dataFiles.Count > 0 ? $", {dataFiles.Count} dataFile(s)" : "")
                         + ((!string.IsNullOrWhiteSpace(contentUrl) && dataFiles.Count > 0) ? ", ingest=place-file (collide)" : "") + ")");
                return 0;
            }
            catch (Exception ex) { return PrintError($"add-game failed: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] Parse the `--data-files` value into (name, targetSubpath, optional) tuples.
        /// Format: comma-separated entries, each "name[:targetSubpath[:optional]]". The 3rd colon segment is the
        /// optional flag — the literal word "optional" or "true" marks the file as not-required (the port still
        /// installs without it). Empty/whitespace entries are skipped. Mirrors the dataFiles[] objects the catalog
        /// parser (GameManager.ParseDataFiles) reads back: {name, targetSubpath?, optional?}.</summary>
        private static List<(string Name, string Subpath, bool Optional)> ParseDataFilesArg(string? arg)
        {
            var result = new List<(string, string, bool)>();
            if (string.IsNullOrWhiteSpace(arg)) return result;
            foreach (var raw in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = raw.Split(':', StringSplitOptions.TrimEntries);
                var name = parts.Length > 0 ? parts[0] : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var sub = parts.Length > 1 ? parts[1] : string.Empty;
                var optional = parts.Length > 2 &&
                    (string.Equals(parts[2], "optional", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parts[2], "true", StringComparison.OrdinalIgnoreCase));
                result.Add((name, sub, optional));
            }
            return result;
        }

        private int PrintExecutablePath(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
                return PrintPathError("App name required. Usage: --path <name>");

            var game = FindGame(gameName);
            if (game == null)
                return PrintPathError($"App not found: '{gameName}'");

            if (game.Status == GameStatus.NotInstalled)
                return PrintPathError($"{game.Name} is not installed. Use --download first.");

            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName))
                return PrintPathError("App folder is not configured.");

            // Mirror how --run resolves the executable.
            var executablePath = game.LoadSelectedExecutable(gamesFolder);

            if (string.IsNullOrEmpty(executablePath))
            {
                var gamePath = game.GetInstallPath(gamesFolder);
                GameInfo.EnsureExecutableAtRoot(gamePath);

                var executables = GameInfo.GetExecutableCandidates(gamePath, SearchOption.TopDirectoryOnly, out _);
                if (executables.Count == 0)
                {
                    executables = GameInfo.GetExecutableCandidates(gamePath, SearchOption.AllDirectories, out _);
                }

                if (executables.Count == 0)
                    return PrintPathError($"No executable found for {game.Name} in {gamePath}.");

                // Candidates are ordered shallowest-first, so [0] is the best candidate.
                // Don't prompt on multiple matches: just report the best one.
                executablePath = executables[0];
            }

            // Print only the absolute path so callers can pipe/copy it directly.
            Console.WriteLine(Path.GetFullPath(executablePath));
            return 0;
        }

        private int PrintPathError(string message)
        {
            Console.Error.WriteLine($"ERROR: {message}");
            return 1;
        }

        // [yabo-launcher fork] `--open-folder "<game>"` — open the port's install directory in the OS file
        // manager. The sandboxed Tauri web UI's shell.open can't open raw local paths, so the right-click
        // "Open folder" action calls this instead (the engine has full OS access).
        private int OpenFolderCommand(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
                return PrintError("App name required. Usage: --open-folder <name>");

            var game = FindGame(gameName);
            if (game == null)
                return PrintError($"App not found: '{gameName}'");

            if (game.Status == GameStatus.NotInstalled)
                return PrintError($"{game.Name} is not installed. Use --download first.");

            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName))
                return PrintError("App folder is not configured.");

            var dir = game.GetInstallPath(gamesFolder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return PrintError($"Install folder not found for {game.Name}: {dir}");

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", dir);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", dir);
                Console.WriteLine($"OK open-folder {dir}");
                Log.Info($"CLI: open-folder '{game.Name}' -> {dir}");
                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"Failed to open folder for {game.Name}: {ex.Message}");
            }
        }

        private static async Task WaitForLaunchedGameSessionAsync(Process? process)
        {
            if (process == null)
                return;

            int? processGroupId = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var currentProcessGroupId = await TryGetProcessGroupIdAsync(Environment.ProcessId);
                var launchedProcessGroupId = await TryGetProcessGroupIdAsync(process.Id);
                if (launchedProcessGroupId.HasValue && launchedProcessGroupId != currentProcessGroupId)
                {
                    processGroupId = launchedProcessGroupId;
                }
            }

            try
            {
                await process.WaitForExitAsync();
            }
            catch
            {
                // The process may already be gone by this point.
            }

            if (processGroupId.HasValue)
            {
                while (await HasActiveProcessGroupAsync(processGroupId.Value))
                {
                    await Task.Delay(1000);
                }
            }
        }

        private static async Task<int?> TryGetProcessGroupIdAsync(int processId)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o pgid= -p {processId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var psProcess = Process.Start(startInfo);
                if (psProcess == null)
                    return null;

                var output = await psProcess.StandardOutput.ReadToEndAsync();
                await psProcess.WaitForExitAsync();

                return psProcess.ExitCode == 0 && int.TryParse(output.Trim(), out var processGroupId)
                    ? processGroupId
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> HasActiveProcessGroupAsync(int processGroupId)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o pid= -g {processGroupId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var psProcess = Process.Start(startInfo);
                if (psProcess == null)
                    return false;

                var output = await psProcess.StandardOutput.ReadToEndAsync();
                await psProcess.WaitForExitAsync();

                return psProcess.ExitCode == 0 &&
                       output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                           .Any(line => !string.IsNullOrWhiteSpace(line));
            }
            catch
            {
                return false;
            }
        }

        private async Task<int> UpdateOrDownloadGame(GameInfo game, bool isUpdate)
        {
            try
            {
                var settings = AppSettings.Load();

                WriteColor($"→ {(isUpdate ? "Updating" : "Downloading")} {game.Name}...", isUpdate ? ColorWarning : ColorTitle);
                Console.WriteLine();
                Console.WriteLine();

                // Store initial version for comparison
                string initialVersion = game.InstalledVersion ?? "";

                // Get the latest release info
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);

                // Get platform identifier (per-app override → global → Auto)
                string platformIdentifier = GameInfo.GetPlatformIdentifier(settings, game);
                WriteColor($"→ Detected platform: {platformIdentifier}", ColorMuted);
                Console.WriteLine();

                // Check for multiple downloads first
                var cachedRelease = game.GetType().GetMethod("GetLatestRelease", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(game, null);

                if (cachedRelease != null)
                {
                    var assetsProperty = cachedRelease.GetType().GetProperty("assets");
                    if (assetsProperty != null)
                    {
                        var assets = assetsProperty.GetValue(cachedRelease) as System.Collections.IEnumerable;
                        var assetList = assets?.Cast<object>().ToList();

                        // [yabo-launcher fork] Apply the catalog's optional assetPattern (same as the
                        // GUI path) so multi-asset releases resolve to the intended asset. Skip if it
                        // matches nothing, so a bad/stale regex can't block the download.
                        if (assetList != null && !string.IsNullOrWhiteSpace(game.AssetPattern))
                        {
                            var narrowed = assetList.Where(a =>
                                GameInfo.AssetMatchesPattern(
                                    a.GetType().GetProperty("name")?.GetValue(a)?.ToString(),
                                    game.AssetPattern)).ToList();
                            if (narrowed.Count > 0)
                                assetList = narrowed;
                        }

                        if (assetList != null && assetList.Count >= 1)
                        {
                            // [yabo-launcher fork] Headless: ALWAYS pick + set SelectedDownload so the shared
                            // download flow never falls into the GUI "multiple assets — await selection" path
                            // (the GUI no longer narrows by AssetPattern). Choose the AssetPattern-narrowed
                            // (applied just above), platform-matched, 64-bit-preferred asset; fall back to first.
                            static string? NameOf(object a) => a.GetType().GetProperty("name")?.GetValue(a)?.ToString();
                            object matchingAsset = assetList
                                .Where(a => { var n = NameOf(a); return !string.IsNullOrEmpty(n) && GameInfo.MatchesPlatform(n, platformIdentifier); })
                                .OrderByDescending(a => GameInfo.ArchScore(NameOf(a) ?? string.Empty))
                                .FirstOrDefault() ?? assetList[0];

                            var assetName = NameOf(matchingAsset);
                            game.GetType().GetProperty("SelectedDownload")?.SetValue(game, matchingAsset);
                            WriteColor($"→ Selected: {assetName}", ColorSuccess);
                            Console.WriteLine();
                            Console.WriteLine();
                        }
                    }
                }

                // Start the actual download
                var downloadTask = game.PerformActionAsync(
                    _gameManager.HttpClient,
                    _gameManager.GamesFolder,
                    settings);

                // Monitor progress and version changes. [OWNER 2026-06-02 — Rohan parity] A big IA repack (e.g.
                // Sega Rally 2 ~851 MB) takes far longer than any fixed cap, so DON'T abort a download that's still
                // moving. Drive the loop off the download task itself; the stall guard only fires on a genuine hang
                // (no progress for stallTimeout secs while still < 100%). Extraction sits at 100% and just waits.
                double lastProgress = 0;
                int stallSeconds = 0;
                const int stallTimeout = 300; // abort only after 5 min of ZERO progress = a real hang, not slow-but-moving

                while (!downloadTask.IsCompleted)
                {
                    if (game.Status == GameStatus.Installed) break;
                    if (!string.IsNullOrEmpty(game.InstalledVersion) &&
                        game.InstalledVersion != initialVersion &&
                        game.InstalledVersion != "Unknown")
                        break;

                    if (game.DownloadProgress > lastProgress)
                    {
                        if (game.DownloadProgress > lastProgress + 1 || game.DownloadProgress >= 100)
                            // Newline-terminated (NOT \r) — the Tauri bridge's stdout reader only delivers a line on
                            // \n, so a \r-only update never reaches the UI until the download ends. This is what made
                            // big downloads sit at 0% then snap to done. (Rohan parity: live percent events.)
                            Console.WriteLine($"Progress: {game.DownloadProgress:F0}%");
                        lastProgress = game.DownloadProgress;
                        stallSeconds = 0; // it moved — reset the hang clock
                    }
                    else if (lastProgress < 100)
                    {
                        // only count stalls during the DOWNLOAD; once at 100% we're extracting — wait for the task.
                        if (++stallSeconds >= stallTimeout) break;
                    }

                    await Task.Delay(1000);
                }

                Console.WriteLine();
                Console.WriteLine();

                // Await the action so a thrown exception surfaces here instead of disappearing into a -1 exit code.
                try { await downloadTask; }
                catch (Exception ex) { return PrintError($"Download failed: {ex.Message}"); }

                if (lastProgress < 100 && stallSeconds >= stallTimeout && game.Status != GameStatus.Installed)
                {
                    return PrintError("Download stalled — no progress for 5 minutes (connection hang).");
                }

                // Verify installation
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);

                // [yabo-launcher fork — false-failure fix] A successful install can read back as UpdateAvailable
                // (e.g. an IA repack whose synthetic version tag differs from a GitHub release the card also names,
                // so forceUpdateCheck flags an "update"). That is STILL a successful install, not a failure — the
                // files are on disk and launchable. Accept both, so IA cards stop exiting 1 after installing fine.
                if (game.Status == GameStatus.Installed || game.Status == GameStatus.UpdateAvailable)
                {
                    WriteColor($"✓ {game.Name} ", ColorSuccess);
                    Console.WriteLine($"{(isUpdate ? "updated" : "installed")} successfully ({CleanVersion(game.InstalledVersion)})");
                    Console.WriteLine();
                    return 0;
                }
                else
                {
                    return PrintError($"{(isUpdate ? "Update" : "Download")} failed. Final status: {game.Status}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                return PrintError($"Failed to {(isUpdate ? "update" : "download")} {game.Name}: {ex.Message}");
            }
        }

        private async Task<int> DownloadGameCommand(string gameName)
        {
            Log.Info("CLI: --download");
            if (string.IsNullOrEmpty(gameName))
            {
                ShowHelp();
                return PrintError("App name required.");
            }

            var game = FindGame(gameName);
            if (game == null)
            {
                Console.WriteLine();
                WriteColor("Available games: ", ColorMuted);
                Console.WriteLine(string.Join(", ", _gameManager?.Games.Select(g => g.Name) ?? Array.Empty<string>()));
                Console.WriteLine();
                return PrintError($"App not found: '{gameName}'");
            }

            if (game.IsExternal) return HandleExternal(game);

            if (game.Status == GameStatus.Installed)
            {
                WriteColor($"✓ {game.Name} ", ColorSuccess);
                Console.WriteLine($"is already installed ({CleanVersion(game.InstalledVersion)})");
                Console.WriteLine();
                return 0;
            }

            if (game.Status == GameStatus.UpdateAvailable)
            {
                return await UpdateOrDownloadGame(game, isUpdate: true);
            }

            return await UpdateOrDownloadGame(game, isUpdate: false);
        }

        private async Task<int> UpdateAllGames()
        {
            Log.Info("CLI: --update (all)");
            if (_gameManager?.Games == null || !_gameManager.Games.Any())
                return PrintError("No games found in library.");

            var installedGames = _gameManager.Games.Where(g => g.Status == GameStatus.Installed || g.Status == GameStatus.UpdateAvailable).ToList();

            if (!installedGames.Any())
            {
                WriteColor("✓ No installed games to update.", ColorSuccess);
                Console.WriteLine();
                return 0;
            }

            Console.WriteLine($"Checking {installedGames.Count} installed game(s) for updates...");
            Console.WriteLine();

            int updatedCount = 0;
            int errorCount = 0;

            foreach (var game in installedGames)
            {
                if (game.Status == GameStatus.UpdateAvailable)
                {
                    int result = await UpdateOrDownloadGame(game, isUpdate: true);
                    if (result == 0)
                        updatedCount++;
                    else
                        errorCount++;
                }
            }

            Console.WriteLine();
            if (updatedCount > 0)
            {
                WriteColor($"✓ Updated {updatedCount} game(s)", ColorSuccess);
                Console.WriteLine();
            }
            if (errorCount > 0)
            {
                WriteColor($"⚠ {errorCount} game(s) failed to update", ColorWarning);
                Console.WriteLine();
            }
            if (updatedCount == 0 && errorCount == 0)
            {
                WriteColor("✓ All games are up to date", ColorSuccess);
                Console.WriteLine();
            }

            return errorCount > 0 ? 1 : 0;
        }

        private async Task<int> UpdateLauncher()
        {
            Log.Info("CLI: --update-launcher");
            try
            {
                string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string currentVersion = LoadCurrentLauncherVersion(currentAppDirectory);

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.Add("User-Agent", Profile.CliUserAgent);

                WriteColor("→ Checking launcher release...", ColorMuted);
                Console.WriteLine();

                string releaseJson = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
                var release = JsonSerializer.Deserialize<GitHubRelease>(releaseJson);
                if (release == null || string.IsNullOrWhiteSpace(release.tag_name))
                    return PrintError("Could not read launcher update information.");

                if (!IsNewerVersion(release.tag_name, currentVersion))
                {
                    WriteColor($"✓ Launcher is already up to date ({currentVersion})", ColorSuccess);
                    Console.WriteLine();
                    return 0;
                }

                string platformIdentifier = GetPlatformIdentifier();
                var asset = release.assets.FirstOrDefault(a =>
                    a.name.Contains(platformIdentifier, StringComparison.OrdinalIgnoreCase) &&
                    (a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)));

                if (asset == null)
                    return PrintError($"No downloadable launcher update found for this platform ({platformIdentifier}).");

                WriteColor($"→ Downloading launcher {release.tag_name}...", ColorMuted);
                Console.WriteLine();

                string tempDownloadPath = Path.Combine(Path.GetTempPath(), asset.name);
                string tempUpdateFolder = Path.Combine(Path.GetTempPath(), "GithubLauncher_temp_update");
                if (Directory.Exists(tempUpdateFolder))
                    Directory.Delete(tempUpdateFolder, true);
                Directory.CreateDirectory(tempUpdateFolder);

                await using (var downloadStream = await client.GetStreamAsync(asset.browser_download_url))
                await using (var fileStream = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await downloadStream.CopyToAsync(fileStream);
                }

                WriteColor("→ Extracting launcher update...", ColorMuted);
                Console.WriteLine();

                if (asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(tempDownloadPath, tempUpdateFolder, true);
                }
                else
                {
                    int tarResult = await ExtractTarGzAsync(tempDownloadPath, tempUpdateFolder);
                    if (tarResult != 0)
                        return PrintError("Failed to extract launcher update archive.");
                }

                if (!ValidateLauncherUpdateFiles(tempUpdateFolder))
                    return PrintError("Downloaded launcher update appears to be incomplete.");

                WriteColor("→ Starting updater. The launcher will relaunch when it finishes.", ColorWarning);
                Console.WriteLine();

                await CreateAndRunLauncherUpdaterScript(release, tempUpdateFolder, tempDownloadPath, currentAppDirectory);
                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"Failed to update launcher: {ex.Message}");
            }
        }

        private static string LoadCurrentLauncherVersion(string currentAppDirectory)
        {
            string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);
            if (File.Exists(updateCheckFilePath))
            {
                try
                {
                    var json = File.ReadAllText(updateCheckFilePath);
                    var updateInfo = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (updateInfo != null &&
                        updateInfo.TryGetValue("CurrentVersion", out var versionElement) &&
                        !string.IsNullOrWhiteSpace(versionElement.GetString()))
                    {
                        return versionElement.GetString()!;
                    }
                }
                catch
                {
                }
            }

            string versionFilePath = Path.Combine(currentAppDirectory, VersionFileName);
            return File.Exists(versionFilePath) ? File.ReadAllText(versionFilePath).Trim() : "0.0";
        }

        private static bool ValidateLauncherUpdateFiles(string updateDirectory)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                Directory.GetDirectories(updateDirectory, "*.app", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "GithubLauncher.exe"
                : "GithubLauncher";

            string executablePath = Path.Combine(updateDirectory, executableName);
            return File.Exists(executablePath) && new FileInfo(executablePath).Length > 1024;
        }

        private static async Task<int> ExtractTarGzAsync(string tarGzPath, string extractPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{tarGzPath}\" -C \"{extractPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return 1;

            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        private static async Task CreateAndRunLauncherUpdaterScript(GitHubRelease release, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory)
        {
            int currentProcessId = Environment.ProcessId;
            string applicationExecutable = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine launcher executable path.");
            string backupDir = Path.Combine(Path.GetTempPath(), "GithubLauncher_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string updaterScriptPath = Path.Combine(Path.GetTempPath(), "GithubLauncher_Updater.cmd");
                string scriptContent = $@"@echo off
echo GithubLauncher Updater - Version {release.tag_name}
echo.
echo Waiting for GithubLauncher CLI to close...
set /A waitCount=0
:wait_loop
tasklist /FI ""PID eq {currentProcessId}"" 2>NUL | find /I ""{currentProcessId}"">NUL
if ""%ERRORLEVEL%""==""0"" (
    if %waitCount% GEQ {UpdaterProcessExitTimeoutSeconds} (
        echo Launcher did not close in time. Aborting update to avoid replacing files while the app is still running.
        pause
        goto cleanup
    )
    set /A waitCount+=1
    timeout /T 1 >NUL
    goto wait_loop
)

set ""appDir={currentAppDirectory}""
set ""backupDir={backupDir}""
set ""updateDir={tempUpdateFolder}""
if not exist ""%backupDir%"" mkdir ""%backupDir%""

echo Backing up files replaced by this update...
for /F ""delims="" %%i in ('dir /B ""%updateDir%""') do (
    if exist ""%appDir%\%%i\\"" (
        xcopy ""%appDir%\%%i"" ""%backupDir%\%%i\\"" /S /E /Y /I >nul 2>&1
    ) else if exist ""%appDir%\%%i"" (
        copy /Y ""%appDir%\%%i"" ""%backupDir%\"" >nul 2>&1
    )
)

echo Applying update...
xcopy ""%updateDir%\*"" ""%appDir%"" /S /E /Y /I >nul 2>&1
if errorlevel 1 (
    echo Update failed! Restoring backup...
    xcopy ""%backupDir%\*"" ""%appDir%"" /S /E /Y /I >nul 2>&1
    pause
    goto cleanup
)

echo {{""CurrentVersion"":""{release.tag_name}"",""LastCheckTime"":""{DateTime.UtcNow:o}"",""LastKnownVersion"":""{release.tag_name}"",""ETag"":"""",""UpdateAvailable"":false}} > ""{updateCheckFilePath}""
echo Update completed successfully.
start """" ""{applicationExecutable}""

:cleanup
if exist ""%backupDir%"" rmdir /S /Q ""%backupDir%"" >nul 2>&1
if exist ""{tempDownloadPath}"" del ""{tempDownloadPath}"" >nul 2>&1
if exist ""%updateDir%"" rmdir /S /Q ""%updateDir%"" >nul 2>&1
del ""%~f0""
";
                await File.WriteAllTextAsync(updaterScriptPath, scriptContent);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C \"\"{updaterScriptPath}\"\"",
                    WindowStyle = ProcessWindowStyle.Normal,
                    CreateNoWindow = false,
                    UseShellExecute = true
                });
            }
            else
            {
                string updaterScriptPath = Path.Combine(Path.GetTempPath(), "GithubLauncher_Updater.sh");
                string scriptContent = $@"#!/bin/bash
echo ""GithubLauncher Updater - Version {release.tag_name}""
echo
echo ""Waiting for GithubLauncher CLI to close...""
waitCount=0
while kill -0 {currentProcessId} 2>/dev/null; do
    if [ ""$waitCount"" -ge {UpdaterProcessExitTimeoutSeconds} ]; then
        echo ""Launcher did not close in time. Aborting update to avoid replacing files while the app is still running.""
        exit 1
    fi
    waitCount=$((waitCount + 1))
    sleep 1
done

appDir=""{currentAppDirectory}""
backupDir=""{backupDir}""
updateDir=""{tempUpdateFolder}""
mkdir -p ""$backupDir""

echo ""Backing up files replaced by this update...""
find ""$updateDir"" -mindepth 1 -maxdepth 1 -exec basename {{}} \; | while IFS= read -r entry; do
    if [ -e ""$appDir/$entry"" ]; then
        cp -R ""$appDir/$entry"" ""$backupDir/"" 2>/dev/null || true
    fi
done

echo ""Applying update...""
if cp -r ""$updateDir""/* ""$appDir""/ 2>/dev/null; then
    echo ""Update applied successfully""
else
    echo ""Update failed! Restoring backup...""
    cp -r ""$backupDir""/* ""$appDir""/ 2>/dev/null || true
    rm -rf ""$backupDir"" ""$updateDir"" 2>/dev/null || true
    rm -f ""{tempDownloadPath}"" 2>/dev/null || true
    exit 1
fi

cat > ""{updateCheckFilePath}"" << 'EOF'
{{""CurrentVersion"":""{release.tag_name}"",""LastCheckTime"":""{DateTime.UtcNow:o}"",""LastKnownVersion"":""{release.tag_name}"",""ETag"":"""",""UpdateAvailable"":false}}
EOF

if [ -f ""$appDir/GithubLauncher"" ]; then
    chmod +x ""$appDir/GithubLauncher""
    cd ""$appDir""
    nohup ""./GithubLauncher"" > /dev/null 2>&1 &
fi

rm -rf ""$backupDir"" ""$updateDir"" 2>/dev/null || true
rm -f ""{tempDownloadPath}"" 2>/dev/null || true
rm -- ""$0""
";
                scriptContent = scriptContent.Replace("\r\n", "\n").Replace("\r", "\n");
                await File.WriteAllTextAsync(updaterScriptPath, scriptContent);

                using var chmod = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{updaterScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (chmod != null)
                    await chmod.WaitForExitAsync();

                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"\"{updaterScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }

        private static string GetPlatformIdentifier()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "macOS";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm64 => "Linux-ARM64",
                    Architecture.X64 => "Linux-X64",
                    Architecture.X86 => "Linux-X86",
                    Architecture.Arm => "Linux-ARM",
                    _ => "Linux-X64"
                };
            }

            throw new PlatformNotSupportedException("Unsupported operating system");
        }

        private static bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            try
            {
                string cleanLatest = latestVersion.TrimStart('v', 'V');
                string cleanCurrent = currentVersion.TrimStart('v', 'V');
                return Version.TryParse(cleanLatest, out var latest) &&
                    Version.TryParse(cleanCurrent, out var current) &&
                    latest > current;
            }
            catch
            {
                return !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
            }
        }

        private async Task<int> UninstallGame(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
            {
                ShowHelp();
                return PrintError("App name required.");
            }

            var game = FindGame(gameName);
            if (game == null)
            {
                Console.WriteLine();
                WriteColor("Available games: ", ColorMuted);
                Console.WriteLine(string.Join(", ", _gameManager?.Games.Select(g => g.Name) ?? Array.Empty<string>()));
                Console.WriteLine();
                return PrintError($"App not found: '{gameName}'");
            }

            if (string.IsNullOrEmpty(game.FolderName))
                return PrintError("App folder is not configured.");

            // [yabo-launcher fork] Act on the FILESYSTEM, not the detected status. Gating uninstall on game.Status
            // made it no-op whenever a data-incomplete install was misdetected as NotInstalled — the user could
            // neither uninstall NOR cleanly reinstall (Download then thinks it's installed and routes to launch, a
            // deadlock). Ground truth is the install dir on disk.
            var gamePath = game.GetInstallPath(_gameManager!.GamesFolder);
            if (!Directory.Exists(gamePath))
            {
                WriteColor($"✓ {game.Name} ", ColorMuted);
                Console.WriteLine("is not installed (no install folder).");
                Console.WriteLine();
                return 0;
            }

            WriteColor($"→ Uninstalling {game.Name}...", ColorWarning);
            Console.WriteLine();

            try
            {
                Log.Info($"User action: Uninstall '{game.Name}' (CLI) — deleting {gamePath}");
                FileOps.DeleteDirectoryResilient(gamePath);

                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);

                Log.Info($"Uninstalled '{game.Name}' ({gamePath})");
                WriteColor($"✓ {game.Name} ", ColorSuccess);
                Console.WriteLine("uninstalled successfully.");
                Console.WriteLine();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error($"Uninstall '{game.Name}' (CLI) FAILED", ex);
                return PrintError($"Failed to uninstall {game.Name}: {ex.Message}");
            }
        }

        // ROM/Data Library (#5): place the port's declared data files from the
        // Library into its install dir (hardlink/copy). Mirrors GUI ProvideToPort.
        private int ProvideDataCommand(string gameName)
        {
            Log.Info("CLI: --provide-data");
            if (string.IsNullOrEmpty(gameName))
            {
                ShowHelp();
                return PrintError("App name required. Usage: --provide-data <name>");
            }

            var game = FindGame(gameName);
            if (game == null)
                return PrintError($"App not found: '{gameName}'");

            var gamesFolder = _gameManager?.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder))
                return PrintError("App folder is not configured.");

            if (!game.HasDataNeeds)
            {
                WriteColor($"✓ {game.Name} ", ColorMuted);
                Console.WriteLine("has no declared data files.");
                Console.WriteLine();
                return 0;
            }

            var library = new RomLibraryService(AppSettings.Load());
            WriteColor($"→ Library: {library.LibraryPath}", ColorMuted);
            Console.WriteLine();
            Console.WriteLine();

            var results = library.ProvideToPort(game, gamesFolder);
            int failures = 0;
            foreach (var r in results)
            {
                if (r.Success)
                {
                    WriteColor("  ✓ ", ColorSuccess);
                    Console.WriteLine($"{r.Name} - {r.Message}{(r.PlacedPath != null ? $" ({r.PlacedPath})" : string.Empty)}");
                }
                else
                {
                    failures++;
                    WriteColor("  ✗ ", ColorError);
                    Console.WriteLine($"{r.Name} - {r.Message}");
                }
            }

            // [yabo-launcher fork] snesrev ports (zelda3/smw/sm) ship Fullscreen=0 → boot windowed. The owner
            // wants boot-fullscreen, so default DESKTOP fullscreen here as part of provide (persists for fresh
            // installs). No-op for non-SNES ports / ports with no [Graphics] section / a user-chosen value.
            try { if (EnsureSnesrevBootFullscreen(game)) Console.WriteLine("  ✓ Set [Graphics] Fullscreen = 1 (boot fullscreen)."); }
            catch { /* non-fatal */ }

            Console.WriteLine();
            Log.Info($"CLI: provide-data '{game.Name}' — {results.Count - failures}/{results.Count} placed (failures={failures}).");
            return failures > 0 ? 1 : 0;
        }

        /// <summary>
        /// [yabo-launcher fork] `--sniff-gamepad` — open game-controller 0 via the engine's existing SDL2
        /// (ppy.SDL2-CS) and block (~6s) until the first button or trigger press, then print that input's
        /// SDL button NAME to stdout (the snesrev vocabulary: A/B/X/Y/Back/Guide/Start/L3/R3/Lb/Rb/DPad*,
        /// triggers → L2/R2). The web UI's Web-Gamepad-API capture mismaps the 8BitDo's physical buttons vs
        /// the SDL mapping the snesrev games actually use; sniffing through SDL lets the UI bind via the
        /// SAME mapping the game uses. Exits cleanly (code 0, no name printed) if nothing is pressed in time
        /// or no controller is attached. Mirrors dev/_research/Zelda-3-Launcher Controller.cs GetButtonName/
        /// ConvertButtonID, but headless (no window — controller events arrive without one).
        /// </summary>
        private int SniffGamepadCommand()
        {
            Log.Info("CLI: --sniff-gamepad");
            try
            {
                // [yabo-launcher fork] Disable the RAWINPUT joystick driver BEFORE SDL_Init. RAWINPUT needs a
                // WINDOW to receive input messages, so this headless sniffer OPENS XInput/Xbox pads (e.g. an
                // 8BitDo M30, which enumerates as "Xbox 360 Controller") but receives ZERO button events →
                // timeout. Diagnosed from the log: "opened 'Xbox 360 Controller' … timed out (no input)" while
                // the HID 2C fired fine. Forcing RAWINPUT off makes SDL use plain XInput polling (no window).
                SDL2.SDL.SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
                if (SDL2.SDL.SDL_Init(SDL2.SDL.SDL_INIT_GAMECONTROLLER) < 0)
                {
                    Log.Warn($"--sniff-gamepad: SDL init failed: {SDL2.SDL.SDL_GetError()}");
                    return PrintError($"SDL init failed: {SDL2.SDL.SDL_GetError()}");
                }

                // [yabo-launcher fork] The bundled SDL 2.26.5's built-in SDL_GameController DB predates modern
                // pads (8BitDo Ultimate 2C = VID 2dc8 / PID 301d, 8BitDo M30), so SDL_IsGameController() would
                // be false and the sniffer would report NO_CONTROLLER. Load the shipped community mapping DB so
                // the rebind UI (which now routes through this sniffer) recognizes those pads. This SDL2 binding
                // exposes SDL_GameControllerAddMappingsFromFile; call it right after SDL_Init.
                try
                {
                    var sdlDb = System.IO.Path.Combine(AppContext.BaseDirectory, "gamecontrollerdb.txt");
                    if (System.IO.File.Exists(sdlDb))
                    {
                        int added = SDL2.SDL.SDL_GameControllerAddMappingsFromFile(sdlDb);
                        Log.Info(added >= 0
                            ? $"--sniff-gamepad: loaded {added} controller mappings from {sdlDb}"
                            : $"--sniff-gamepad: failed to load mapping DB ({sdlDb}): {SDL2.SDL.SDL_GetError()}");
                    }
                    else
                    {
                        Log.Warn($"--sniff-gamepad: controller mapping DB not found ({sdlDb}); modern 8BitDo pads may not be recognized.");
                    }
                }
                catch (Exception ex) { Log.Warn($"--sniff-gamepad: loading mapping DB failed (continuing): {ex.Message}"); }

                IntPtr controller = IntPtr.Zero;
                try
                {
                    // Open EVERY SDL-recognized controller (not just the first). With multiple pads attached
                    // (e.g. two 8BitDo pads, or a cached Bluetooth pairing) the pad the user presses may not be index 0, and SDL only
                    // delivers CONTROLLERBUTTONDOWN for OPENED controllers — so open them all, and log each
                    // (this also surfaces whether SDL sees a given pad at all, e.g. an XInput M30).
                    int numJoysticks = SDL2.SDL.SDL_NumJoysticks();
                    for (int i = 0; i < numJoysticks; i++)
                    {
                        if (SDL2.SDL.SDL_IsGameController(i) == SDL2.SDL.SDL_bool.SDL_TRUE)
                        {
                            var c = SDL2.SDL.SDL_GameControllerOpen(i);
                            if (c != IntPtr.Zero)
                            {
                                if (controller == IntPtr.Zero) controller = c;
                                Log.Info($"--sniff-gamepad: opened '{SDL2.SDL.SDL_GameControllerName(c)}'.");
                            }
                        }
                    }

                    if (controller == IntPtr.Zero)
                    {
                        // No controller attached — clean exit (UI treats empty stdout as "nothing detected").
                        Log.Info("--sniff-gamepad: no game controller attached.");
                        Console.Error.WriteLine("NO_CONTROLLER");
                        return 0;
                    }

                    Log.Info($"--sniff-gamepad: listening on '{SDL2.SDL.SDL_GameControllerName(controller)}' (~6s)...");

                    // Drain any stale queued events before we start watching.
                    SDL2.SDL.SDL_PumpEvents();
                    SDL2.SDL.SDL_FlushEvents(SDL2.SDL.SDL_EventType.SDL_FIRSTEVENT, SDL2.SDL.SDL_EventType.SDL_LASTEVENT);

                    bool leftTrigger = false, rightTrigger = false;
                    var stop = DateTime.Now.AddSeconds(6);

                    while (DateTime.Now < stop)
                    {
                        while (SDL2.SDL.SDL_PollEvent(out var e) == 1)
                        {
                            // A button press resolves immediately.
                            if (e.type == SDL2.SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN)
                            {
                                var name = ConvertSdlButtonId(e.cbutton.button);
                                if (name != null)
                                {
                                    Console.WriteLine(name);
                                    Log.Info($"--sniff-gamepad: detected '{name}'.");
                                    return 0;
                                }
                            }

                            // Triggers are axes — fire once the axis has been pushed and released to 0
                            // (mirrors the Zelda launcher), so a resting trigger doesn't trip it.
                            if (e.type == SDL2.SDL.SDL_EventType.SDL_CONTROLLERAXISMOTION)
                            {
                                if (e.caxis.axis == (byte)SDL2.SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERLEFT)
                                {
                                    if (e.caxis.axisValue != 0) leftTrigger = true;
                                    else if (leftTrigger) { Console.WriteLine("L2"); Log.Info("--sniff-gamepad: detected 'L2'."); return 0; }
                                }
                                else if (e.caxis.axis == (byte)SDL2.SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERRIGHT)
                                {
                                    if (e.caxis.axisValue != 0) rightTrigger = true;
                                    else if (rightTrigger) { Console.WriteLine("R2"); Log.Info("--sniff-gamepad: detected 'R2'."); return 0; }
                                }
                            }
                        }
                        System.Threading.Thread.Sleep(10);
                    }

                    // Timed out with no press — clean exit.
                    Log.Info("--sniff-gamepad: timed out (no input).");
                    Console.Error.WriteLine("TIMEOUT");
                    return 0;
                }
                finally
                {
                    if (controller != IntPtr.Zero) SDL2.SDL.SDL_GameControllerClose(controller);
                    SDL2.SDL.SDL_QuitSubSystem(SDL2.SDL.SDL_INIT_GAMECONTROLLER);
                    SDL2.SDL.SDL_Quit();
                }
            }
            catch (Exception ex)
            {
                Log.Error("--sniff-gamepad failed", ex);
                return PrintError($"sniff-gamepad failed: {ex.Message}");
            }
        }

        /// <summary>[yabo-launcher fork] Map an SDL_CONTROLLERBUTTONDOWN id to the snesrev button vocabulary
        /// (from dev/_research/Zelda-3-Launcher Controller.cs ConvertButtonID).</summary>
        private static string? ConvertSdlButtonId(byte button) => button switch
        {
            0 => "A",
            1 => "B",
            2 => "X",
            3 => "Y",
            4 => "Back",
            5 => "Guide",
            6 => "Start",
            7 => "L3",
            8 => "R3",
            9 => "Lb",
            10 => "Rb",
            11 => "DPadUp",
            12 => "DPadDown",
            13 => "DPadLeft",
            14 => "DPadRight",
            _ => null,
        };

        // ── bucket D helpers + commands ──────────────────────────────────────────────────────────

        /// <summary>[yabo-launcher fork] Returns args with any `--rom "&lt;file&gt;"` pair removed (preserves order).</summary>
        private static string[] StripRomModifier(string[] args)
        {
            var outList = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--rom", StringComparison.OrdinalIgnoreCase))
                {
                    i++; // skip the value too
                    continue;
                }
                outList.Add(args[i]);
            }
            return outList.ToArray();
        }

        /// <summary>[yabo-launcher fork] Splits `--run/--play "&lt;name&gt;" [--rom "&lt;file&gt;"]` into (gameName, romOverride).
        /// The name is everything after the verb minus the --rom pair; romOverride is null when absent.</summary>
        private static (string name, string? rom) SplitRomModifier(string[] args)
        {
            string? rom = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--rom", StringComparison.OrdinalIgnoreCase))
                {
                    rom = args[i + 1].Trim('"', '\'');
                    break;
                }
            }
            var rest = StripRomModifier(args);
            var name = rest.Length < 2 ? string.Empty : string.Join(" ", rest.Skip(1)).Trim('"', '\'');
            return (name, string.IsNullOrWhiteSpace(rom) ? null : rom);
        }

        /// <summary>`--hide "&lt;name&gt;"` / `--unhide "&lt;name&gt;"` — toggle a port's manual-hide flag
        /// (AppSettings.ManuallyHiddenApps), the same store the GUI's eye toggle used. Persisted via AppSettings.</summary>
        private int HideCommand(string[] args, bool hide)
        {
            if (args.Length < 2) return PrintError($"Usage: {(hide ? "--hide" : "--unhide")} \"<name>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");

            bool isHidden = _gameManager!.IsManuallyHidden(game);
            if (hide && !isHidden) _gameManager.ToggleUserHide(game);       // hide
            else if (!hide && isHidden) _gameManager.ToggleUserHide(game);  // unhide

            Console.WriteLine($"OK {(hide ? "hide" : "unhide")} {game.Name}");
            Log.Info($"CLI: {(hide ? "hide" : "unhide")} '{game.Name}'");
            return 0;
        }

        /// <summary>`--unhide-all` — clear every hidden flag (manual + auto), the GUI's "Show all" action.</summary>
        private int UnhideAllCommand()
        {
            var s = AppSettings.Load();
            int n = s.HiddenApps.Count + s.ManuallyHiddenApps.Count;
            s.HiddenApps.Clear();
            s.ManuallyHiddenApps.Clear();
            AppSettings.Save(s);
            Console.WriteLine($"OK unhide-all ({n} cleared)");
            Log.Info($"CLI: unhide-all ({n} cleared)");
            return 0;
        }

        /// <summary>`--edit-app "&lt;repo&gt;" "&lt;field&gt;" "&lt;value&gt;"` — change name/repository/folderName/category/icon
        /// in apps.json for the entry matched by repository (or folderName). When folderName changes, the install
        /// folder under GamesFolder is renamed. Byte-faithful JsonNode edit (does NOT route through the disabled
        /// validate/round-trip), preserving every other entry and field.</summary>
        private int EditAppCommand(string[] args)
        {
            if (args.Length < 4) return PrintError("Usage: --edit-app \"<repo>\" \"<field>\" \"<value>\"  (field: name|repository|folderName|category|icon)");
            var repo = args[1].Trim('"', '\'');
            var field = args[2].Trim('"', '\'').ToLowerInvariant();
            var value = args[3].Trim('"', '\'');

            // Map the user-facing field to the apps.json key.
            string? jsonKey = field switch
            {
                "name" => "name",
                "repository" or "repo" => "repository",
                "foldername" or "folder" => "folderName",
                "category" => "category",
                "icon" or "appiconurl" => "appIconUrl",
                "favorite" or "fav" => "favorite",
                _ => null,
            };
            if (jsonKey == null) return PrintError("field must be one of: name | repository | folderName | category | icon | favorite");

            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(appsPath))!;
                var apps = node["apps"]!.AsArray();
                var entry = apps.FirstOrDefault(a =>
                    string.Equals((string?)a!["repository"], repo, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string?)a!["folderName"], repo, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string?)a!["name"], repo, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return PrintError($"No catalog entry matches '{repo}'.");

                // folderName change: rename the install dir BEFORE we rewrite apps.json so a failure leaves both consistent.
                if (jsonKey == "folderName")
                {
                    var oldFolder = (string?)entry["folderName"];
                    var gamesFolder = _gameManager?.GamesFolder;
                    if (!string.IsNullOrWhiteSpace(oldFolder) && !string.IsNullOrWhiteSpace(value)
                        && !string.Equals(oldFolder, value, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(gamesFolder))
                    {
                        var oldDir = System.IO.Path.Combine(gamesFolder, oldFolder!);
                        var newDir = System.IO.Path.Combine(gamesFolder, value);
                        if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                        {
                            try { Directory.Move(oldDir, newDir); Log.Info($"CLI: edit-app renamed install folder '{oldFolder}' -> '{value}'."); }
                            catch (Exception ex) { return PrintError($"Couldn't rename install folder '{oldFolder}' -> '{value}': {ex.Message}"); }
                        }
                    }
                }

                // [yabo-launcher fork] favorite is a real JSON boolean (the parser reads ValueKind.True), so write a
                // bool — not the "true"/"false" string — or the ★ won't round-trip back through --list-json.
                if (jsonKey == "favorite")
                    entry["favorite"] = (value is "true" or "1" or "yes" or "on");
                else
                    entry[jsonKey] = value;
                System.IO.File.WriteAllText(appsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                Console.WriteLine($"OK edit-app {repo} {field}={value}");
                Log.Info($"CLI: edit-app '{repo}' {jsonKey}='{value}'");
                return 0;
            }
            catch (Exception ex) { return PrintError($"edit-app failed: {ex.Message}"); }
        }

        /// <summary>`--remove-app "&lt;repo&gt;"` — delete the catalog entry matched by repository (or folderName/name).
        /// Byte-faithful JsonNode edit; does NOT delete any installed files.</summary>
        private int RemoveAppCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --remove-app \"<repo>\"");
            var repo = args[1].Trim('"', '\'');
            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(appsPath))!;
                var apps = node["apps"]!.AsArray();
                int idx = -1;
                for (int i = 0; i < apps.Count; i++)
                {
                    var a = apps[i]!;
                    if (string.Equals((string?)a["repository"], repo, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals((string?)a["folderName"], repo, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals((string?)a["name"], repo, StringComparison.OrdinalIgnoreCase))
                    { idx = i; break; }
                }
                if (idx < 0) return PrintError($"No catalog entry matches '{repo}'.");
                apps.RemoveAt(idx);
                System.IO.File.WriteAllText(appsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                Console.WriteLine($"OK remove-app {repo}");
                Log.Info($"CLI: remove-app '{repo}'");
                return 0;
            }
            catch (Exception ex) { return PrintError($"remove-app failed: {ex.Message}"); }
        }

        /// <summary>`--set-platform "&lt;name&gt;" &lt;Auto|Windows|MacOS|LinuxX64|LinuxARM64&gt;` — per-app platform override
        /// (AppSettings.PlatformOverrides, keyed by folderName). "Auto" removes the override (falls back to global).</summary>
        private int SetPlatformCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --set-platform \"<name>\" <Auto|Windows|MacOS|LinuxX64|LinuxARM64>");
            var target = args[^1].Trim('"', '\'');
            var name = string.Join(" ", args[1..^1]).Trim('"', '\'');
            if (!Enum.TryParse<TargetOS>(target, ignoreCase: true, out var os))
                return PrintError("Platform must be one of: Auto | Windows | MacOS | LinuxX64 | LinuxARM64");
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            if (string.IsNullOrWhiteSpace(game.FolderName)) return PrintError("App folder is not configured.");

            var s = AppSettings.Load();
            s.PlatformOverrides ??= new Dictionary<string, string>();
            if (os == TargetOS.Auto) s.PlatformOverrides.Remove(game.FolderName);
            else s.PlatformOverrides[game.FolderName] = os.ToString();
            AppSettings.Save(s);
            Console.WriteLine($"OK set-platform {game.Name} {os}");
            Log.Info($"CLI: set-platform '{game.Name}' = {os}");
            return 0;
        }

        /// <summary>`--locate-install "&lt;name&gt;" "&lt;folder&gt;"` — point a port at an EXISTING install folder
        /// (sets InstallPath in apps.json) and re-check status. The GUI's "Locate existing install".</summary>
        private async Task<int> LocateInstallCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --locate-install \"<name>\" \"<folder>\"");
            var folder = args[^1].Trim('"', '\'');
            var name = string.Join(" ", args[1..^1]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            if (!Directory.Exists(folder)) return PrintError($"Folder not found: {folder}");

            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");
            try
            {
                var full = Path.GetFullPath(folder);
                // Persist InstallPath into apps.json (byte-faithful JsonNode edit).
                var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(appsPath))!;
                var apps = node["apps"]!.AsArray();
                var entry = apps.FirstOrDefault(a =>
                    string.Equals((string?)a!["repository"], game.Repository, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string?)a!["folderName"], game.FolderName, StringComparison.OrdinalIgnoreCase))
                    ?? apps.FirstOrDefault(a => string.Equals((string?)a!["name"], game.Name, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return PrintError($"No catalog entry matches '{game.Name}'.");
                entry["installPath"] = full;
                System.IO.File.WriteAllText(appsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));

                // Update in-memory + re-check status so the response reflects the new location.
                game.InstallPath = full;
                await game.CheckStatusAsync(_gameManager!.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: false).ConfigureAwait(false);
                Console.WriteLine($"OK locate-install {game.Name} -> {full} (status: {game.Status})");
                Log.Info($"CLI: locate-install '{game.Name}' -> {full}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"locate-install failed: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] `--adopt "&lt;folder&gt;" [gameName]` — adopt a folder the user ALREADY
        /// downloaded as an installed game (RohanKar `scan-for-games` parity). Matches the folder against the
        /// catalog (or uses the named game), points the matched card at the existing folder + detected exe, and
        /// re-checks status — with NO download and NO file move. Reuses the SAME persistence as --locate-install
        /// (InstallPath → apps.json) + --set-exe (SaveSelectedExecutable → selected_executable.txt).</summary>
        private async Task<int> AdoptCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --adopt \"<folder>\" [gameName]");

            // The FIRST positional arg is the folder; an optional trailing arg names the target game. We detect
            // the folder as the existing-directory arg so quoting/order is forgiving.
            string folder;
            string? gameName = null;
            var a1 = args[1].Trim('"', '\'');
            if (Directory.Exists(a1))
            {
                folder = a1;
                if (args.Length > 2) gameName = string.Join(" ", args[2..]).Trim('"', '\'');
            }
            else
            {
                // First arg wasn't a folder — treat the LAST arg as the folder, the middle as the name.
                folder = args[^1].Trim('"', '\'');
                if (args.Length > 2) gameName = string.Join(" ", args[1..^1]).Trim('"', '\'');
            }
            if (!Directory.Exists(folder)) return PrintError($"Folder not found: {folder}");

            var match = InstallAdoptionService.AdoptFolder(_gameManager!.AllGames, folder, gameName);
            if (match == null)
                return PrintError($"No catalog match for folder '{Path.GetFileName(folder.TrimEnd('\\', '/'))}'. Pass a game name: --adopt \"<folder>\" \"<game>\".");

            var ok = await ApplyAdoptionAsync(match).ConfigureAwait(false);
            if (!ok) return PrintError($"adopt failed to persist for {match.Game.Name}.");

            Console.WriteLine($"OK adopt {match.Game.Name} -> {match.FolderPath} "
                + $"({match.MatchedBy}, exe: {(match.ExePath != null ? Path.GetFileName(match.ExePath) : "none")}, status: {match.Game.Status})");
            Log.Info($"CLI: adopt '{match.Game.Name}' -> {match.FolderPath} ({match.MatchedBy})");
            return 0;
        }

        /// <summary>[yabo-launcher fork] `--scan-installs "&lt;parentFolder&gt;"` — scan a parent folder of game
        /// folders and adopt every immediate subfolder that matches the catalog (RohanKar `scan-for-games`).
        /// Prints a matched/adopted/skipped summary.</summary>
        private async Task<int> ScanInstallsCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --scan-installs \"<parentFolder>\"");
            var parent = string.Join(" ", args[1..]).Trim('"', '\'');
            if (!Directory.Exists(parent)) return PrintError($"Folder not found: {parent}");

            var matches = InstallAdoptionService.ScanParent(_gameManager!.AllGames, parent);
            int adopted = 0, failed = 0;
            foreach (var m in matches)
            {
                if (await ApplyAdoptionAsync(m).ConfigureAwait(false))
                {
                    adopted++;
                    Console.WriteLine($"  adopted: {m.Game.Name} <- {Path.GetFileName(m.FolderPath)} "
                        + $"({m.MatchedBy}, exe: {(m.ExePath != null ? Path.GetFileName(m.ExePath) : "none")})");
                }
                else { failed++; Console.WriteLine($"  FAILED:  {m.Game.Name} <- {Path.GetFileName(m.FolderPath)}"); }
            }

            int subCount = 0;
            try { subCount = Directory.GetDirectories(parent).Length; } catch { }
            Console.WriteLine($"OK scan-installs: {matches.Count} matched, {adopted} adopted, {failed} failed, "
                + $"{Math.Max(0, subCount - matches.Count)} skipped (of {subCount} subfolder(s)).");
            Log.Info($"CLI: scan-installs '{parent}' — {matches.Count} matched, {adopted} adopted, {failed} failed.");
            return 0;
        }

        /// <summary>[yabo-launcher fork] Persist one adoption the SAME way the GUI/CLI already register an
        /// existing install: write the folder to the catalog entry's installPath in apps.json (identical to
        /// --locate-install), record the detected exe via SaveSelectedExecutable (identical to --set-exe), then
        /// re-check status so the card flips to Installed. No download, no move. Returns false on failure.</summary>
        private async Task<bool> ApplyAdoptionAsync(InstallAdoptionService.AdoptionMatch match)
        {
            var game = match.Game;
            try
            {
                var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
                if (!System.IO.File.Exists(appsPath)) { Log.Warn("adopt: apps.json not found."); return false; }

                // Persist installPath into apps.json (byte-faithful JsonNode edit) — same as LocateInstallCommand.
                var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(appsPath))!;
                var apps = node["apps"]!.AsArray();
                var entry = apps.FirstOrDefault(a =>
                    string.Equals((string?)a!["repository"], game.Repository, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string?)a!["folderName"], game.FolderName, StringComparison.OrdinalIgnoreCase))
                    ?? apps.FirstOrDefault(a => string.Equals((string?)a!["name"], game.Name, StringComparison.OrdinalIgnoreCase));
                if (entry == null) { Log.Warn($"adopt: no catalog entry matches '{game.Name}'."); return false; }
                entry["installPath"] = match.FolderPath;
                System.IO.File.WriteAllText(appsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));

                // In-memory location + remembered exe so --run launches it directly (same as --set-exe).
                game.InstallPath = match.FolderPath;
                if (!string.IsNullOrWhiteSpace(match.ExePath))
                    game.SaveSelectedExecutable(match.ExePath!, _gameManager!.GamesFolder);

                await game.CheckStatusAsync(_gameManager!.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: false).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"adopt: persist failed for '{game.Name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>`--set-custom-icon "&lt;name&gt;" "&lt;file&gt;"` — set a LOCAL-file cover (copied into
        /// Cache\CustomIcons\&lt;folder&gt;_custom.ext). Distinct from the URL art-pin (--set-art).</summary>
        private int SetCustomIconCommand(string[] args)
        {
            if (args.Length < 3) return PrintError("Usage: --set-custom-icon \"<name>\" \"<image file>\"");
            var file = args[^1].Trim('"', '\'');
            var name = string.Join(" ", args[1..^1]).Trim('"', '\'');
            if (!File.Exists(file)) return PrintError($"Image file not found: {file}");
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            if (string.IsNullOrWhiteSpace(game.FolderName)) return PrintError("App folder is not configured.");
            try
            {
                game.SetCustomIcon(file, _gameManager!.CacheFolder);
                Console.WriteLine($"OK set-custom-icon {game.Name} -> {Path.GetFileName(game.CustomIconPath ?? file)}");
                Log.Info($"CLI: set-custom-icon '{game.Name}' = {game.CustomIconPath}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"set-custom-icon failed: {ex.Message}"); }
        }

        /// <summary>`--remove-custom-icon "&lt;name&gt;"` — clear the local-file cover and delete its cached file.</summary>
        private int RemoveCustomIconCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --remove-custom-icon \"<name>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            if (string.IsNullOrWhiteSpace(game.FolderName)) return PrintError("App folder is not configured.");
            try
            {
                game.RemoveCustomIcon();
                // [yabo-launcher fork] RemoveCustomIcon defers the file delete to a background task, so sweep the
                // cached <folder>_custom.* file(s) ourselves here to be sure they're gone before we return.
                var dir = Path.Combine(_gameManager!.CacheFolder, "CustomIcons");
                int n = 0;
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir, $"{game.FolderName}_custom.*"))
                        { try { File.Delete(f); n++; } catch { } }
                Console.WriteLine($"OK remove-custom-icon {game.Name} ({n} file(s) removed)");
                Log.Info($"CLI: remove-custom-icon '{game.Name}' ({n})");
                return 0;
            }
            catch (Exception ex) { return PrintError($"remove-custom-icon failed: {ex.Message}"); }
        }

        /// <summary>`--skip-update "&lt;name&gt;"` — pin the current install and skip the latest update
        /// (GameInfo.SkipLatestUpdate → PreferredVersion + SkippedUpdateVersion), persisted to apps.json.</summary>
        private async Task<int> SkipUpdateCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --skip-update \"<name>\"");
            var name = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(name);
            if (game == null) return PrintError($"App not found: '{name}'");
            // Ensure LatestVersion is known so SkipLatestUpdate has something to pin against.
            try { await game.CheckStatusAsync(_gameManager!.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: false).ConfigureAwait(false); } catch { }
            if (string.IsNullOrWhiteSpace(game.LatestVersion))
                return PrintError($"No latest version known for {game.Name} — can't skip.");
            game.SkipLatestUpdate();

            // Persist PreferredVersion + SkippedUpdateVersion into apps.json (byte-faithful JsonNode edit).
            try
            {
                var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
                var node = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(appsPath))!;
                var apps = node["apps"]!.AsArray();
                var entry = apps.FirstOrDefault(a =>
                    string.Equals((string?)a!["repository"], game.Repository, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string?)a!["folderName"], game.FolderName, StringComparison.OrdinalIgnoreCase))
                    ?? apps.FirstOrDefault(a => string.Equals((string?)a!["name"], game.Name, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    entry["preferredVersion"] = game.PreferredVersion;
                    entry["skippedUpdateVersion"] = game.SkippedUpdateVersion;
                    System.IO.File.WriteAllText(appsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
                }
            }
            catch (Exception ex) { return PrintError($"skip-update persist failed: {ex.Message}"); }

            Console.WriteLine($"OK skip-update {game.Name} (skipped {CleanVersion(game.SkippedUpdateVersion)})");
            Log.Info($"CLI: skip-update '{game.Name}' skipped={game.SkippedUpdateVersion} preferred={game.PreferredVersion}");
            return 0;
        }

        /// <summary>`--changelog "&lt;name&gt;" [tag]` — GitHub release notes as JSON. With a tag, returns that one
        /// release {tag,name,body,publishedAt}; without, returns an array (newest first). Honors the github token.</summary>
        private async Task<int> ChangelogCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --changelog \"<name>\" [tag]");
            // The optional trailing tag is ambiguous with a multi-word name; treat the LAST arg as a tag only
            // when the name (everything between) still resolves to a game. Try full-tail-as-name first.
            string name = string.Join(" ", args[1..]).Trim('"', '\'');
            string? tag = null;
            var game = FindGame(name);
            if (game == null && args.Length >= 3)
            {
                name = string.Join(" ", args[1..^1]).Trim('"', '\'');
                tag = args[^1].Trim('"', '\'');
                game = FindGame(name);
            }
            if (game == null) return PrintError($"App not found: '{name}'");
            if (string.IsNullOrWhiteSpace(game.Repository))
                return PrintError($"{game.Name} has no GitHub repository — no changelog available.");

            var token = AppSettings.Load().GitHubApiToken;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{game.Repository}/releases");
                req.Headers.Add("User-Agent", Profile.CliUserAgent);
                req.Headers.Add("Accept", "application/vnd.github+json");
                if (!string.IsNullOrWhiteSpace(token))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var resp = await _gameManager!.HttpClient.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(body);
                var entries = doc.RootElement.EnumerateArray().Select(r => new
                {
                    tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null,
                    name = r.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                    body = r.TryGetProperty("body", out var bd) ? bd.GetString() : null,
                    publishedAt = r.TryGetProperty("published_at", out var pa) ? pa.GetString() : null,
                    prerelease = r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True,
                }).ToList();

                object payload = tag != null
                    ? (object?)entries.FirstOrDefault(e => string.Equals(e.tag, tag, StringComparison.OrdinalIgnoreCase))
                        ?? new { tag, name = (string?)null, body = (string?)null, publishedAt = (string?)null, prerelease = false }
                    : entries;

                var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
                return 0;
            }
            catch (Exception ex) { return PrintError($"changelog failed: {ex.Message}"); }
        }

        /// <summary>`--export-catalog "&lt;path&gt;"` — write a copy of the live apps.json to &lt;path&gt; (backup).</summary>
        // [yabo-launcher fork — dev-loop publish] Write the canon to the feed path. THE SAFE PUBLISH BRICK:
        //   --only "<folderA,folderB,...>"  → export ONLY those cards (the dev plugin passes the set tagged
        //                                     `upstream` in Playnite). This is the staging gate — untagged
        //                                     drafts NEVER leave the machine.
        //   (no --only)                     → full copy, but PRINTS A LOUD WARNING that drafts are included.
        // Either way we strip dev-only bookkeeping fields so internal notes never ship.
        private int ExportCatalogCommand(string[] args)
        {
            // Dev-only fields that must never reach a subscriber's feed.
            var devOnlyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "devNote", "devNotes", "_draft", "draft", "internal", "_internal", "todo" };

            var (onlyRaw, r1)   = ExtractFlag(args, "--only");
            var (notes,   r2)   = ExtractFlag(r1,  "--notes");    // owner-authored patch note (the bell message)
            var (title,   r3)   = ExtractFlag(r2,  "--title");    // short headline for the notification
            var (version, r4)   = ExtractFlag(r3,  "--version");  // optional version stamp (else auto from date)
            // [dev-loop publish] --gated: publish EXACTLY the curation gate (gate.json) — the staged release set.
            // This is the golden path: the owner curates via --gate-add, then `--export-catalog <feed> --gated`
            // ships precisely those cards. Equivalent to passing --only with the gate's folderNames, but the
            // owner never has to retype them. Errors if there's no gate (nothing has been staged).
            bool useGate = false;
            var rest = r4;
            {
                var idx = Array.FindIndex(r4, a => string.Equals(a, "--gated", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) { useGate = true; rest = r4.Where((_, i) => i != idx).ToArray(); }
            }
            if (rest.Length < 2) return PrintError("Usage: --export-catalog \"<path>\" [--gated | --only \"folderA,folderB,...\"] [--notes \"<what's new>\"] [--title \"<headline>\"] [--version \"<v>\"]");
            if (useGate)
            {
                if (!GateService.Exists())
                    return PrintError("--gated: no gate.json found — nothing is staged. Use --gate-add to stage cards first.");
                var gated = GateService.Load();
                if (gated.Count == 0)
                    return PrintError("--gated: the gate is empty ([]) — nothing staged to publish.");
                // Fold the gate into the --only allow-list (union if the owner ALSO passed --only).
                onlyRaw = string.IsNullOrWhiteSpace(onlyRaw)
                    ? string.Join(",", gated)
                    : onlyRaw + "," + string.Join(",", gated);
            }
            var dest = string.Join(" ", rest[1..]).Trim('"', '\'');
            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");

            // Parse the --only allow-list (comma and/or whitespace separated folderNames).
            HashSet<string>? only = null;
            if (!string.IsNullOrWhiteSpace(onlyRaw))
            {
                only = new HashSet<string>(
                    onlyRaw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(dest));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(appsPath))!;
                var apps = root["apps"] as System.Text.Json.Nodes.JsonArray
                           ?? throw new Exception("apps.json has no 'apps' array.");

                var outArr = new System.Text.Json.Nodes.JsonArray();
                var keptNames = new List<string>();
                int kept = 0, skipped = 0;
                foreach (var card in apps.ToList())
                {
                    if (card == null) continue;
                    var folder = (string?)card["folderName"] ?? "";
                    if (only != null && !only.Contains(folder)) { skipped++; continue; }   // not tagged upstream → stays home

                    var clone = card.DeepClone();
                    foreach (var f in devOnlyFields)            // scrub dev bookkeeping
                        if (clone is System.Text.Json.Nodes.JsonObject obj) obj.Remove(f);
                    outArr.Add(clone);
                    keptNames.Add((string?)card["name"] ?? folder);
                    kept++;
                }

                var outRoot = new System.Text.Json.Nodes.JsonObject { ["apps"] = outArr };

                // [dev-loop patch notes] If the owner attached a note, embed a `release` block the client surfaces
                // as a single bell notification ("here's what I added") — so games never appear unprompted.
                if (!string.IsNullOrWhiteSpace(notes) || !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(version))
                {
                    var stamp = DateTime.Now.ToString("yyyy-MM-dd");
                    var added = new System.Text.Json.Nodes.JsonArray();
                    foreach (var n in keptNames) added.Add(n);
                    outRoot["release"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["version"] = string.IsNullOrWhiteSpace(version) ? stamp : version,
                        ["date"]    = stamp,
                        ["title"]   = string.IsNullOrWhiteSpace(title) ? "yabo catalog update" : title,
                        ["notes"]   = notes ?? "",
                        ["added"]   = added,   // the cards in THIS publish — context for the notification
                    };
                }

                File.WriteAllText(dest, outRoot.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                if (only == null)
                    Console.WriteLine($"WARNING: no --only filter — exported ALL {kept} cards (DRAFTS INCLUDED). Use --only for a gated publish.");
                Console.WriteLine($"OK export-catalog -> {Path.GetFullPath(dest)}  (kept {kept}{(only != null ? $", held back {skipped} untagged" : "")})");
                Log.Info($"CLI: export-catalog -> {dest} kept={kept} skipped={skipped} filtered={(only != null)}");
                return 0;
            }
            catch (Exception ex) { return PrintError($"export-catalog failed: {ex.Message}"); }
        }

        /// <summary>`--import-catalog "&lt;path&gt;"` — MERGE an apps.json-shaped file into the live catalog.
        /// Matches on repository+folderName: existing entries are updated (field-by-field), new entries added.
        /// Byte-faithful JsonNode merge; does NOT route through the disabled validate/round-trip.</summary>
        private int ImportCatalogCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --import-catalog \"<path>\"");
            var src = string.Join(" ", args[1..]).Trim('"', '\'');
            if (!System.IO.File.Exists(src)) return PrintError($"File not found: {src}");
            var appsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "apps.json");
            if (!System.IO.File.Exists(appsPath)) return PrintError("apps.json not found.");
            try
            {
                var liveNode = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(appsPath))!;
                var liveApps = liveNode["apps"]!.AsArray();

                var inNode = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(src))!;
                // Accept both {"apps":[...]} and a bare [...] array.
                System.Text.Json.Nodes.JsonArray incoming = inNode is System.Text.Json.Nodes.JsonArray bare
                    ? bare
                    : (inNode["apps"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray());

                static string Key(System.Text.Json.Nodes.JsonNode? a) =>
                    ((string?)a?["repository"] ?? "") + " " + ((string?)a?["folderName"] ?? "");

                var liveByKey = new Dictionary<string, System.Text.Json.Nodes.JsonNode>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in liveApps) if (a != null) liveByKey[Key(a)] = a;

                int added = 0, updated = 0;
                foreach (var inc in incoming.ToList())
                {
                    if (inc == null) continue;
                    var key = Key(inc);
                    if (liveByKey.TryGetValue(key, out var existing))
                    {
                        // Update existing: copy every field from the incoming entry over the live one.
                        foreach (var kv in inc.AsObject())
                            existing[kv.Key] = kv.Value?.DeepClone();
                        updated++;
                    }
                    else
                    {
                        liveApps.Add(inc.DeepClone());
                        added++;
                    }
                }

                System.IO.File.WriteAllText(appsPath, liveNode.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                Console.WriteLine($"OK import-catalog: {added} added, {updated} updated.");
                Log.Info($"CLI: import-catalog from '{src}' — {added} added, {updated} updated.");
                return 0;
            }
            catch (Exception ex) { return PrintError($"import-catalog failed: {ex.Message}"); }
        }

        // ── Curation gate (allow-list) ─────────────────────────────────────────────────────────
        // [yabo-launcher fork] gate.json beside the exe is an allow-list of folderNames. When it exists,
        // --list-json emits ONLY gated cards (empty gate = wiped library). When it's absent, --list-json
        // emits the full catalog (backward-compatible). These commands resolve a name OR folderName through
        // the catalog, never touch apps.json/user-apps.json, and are idempotent. See GateService.

        /// <summary>`--gate-add "&lt;name-or-folder&gt;"` — add one card to the gate (creates gate.json if missing).</summary>
        private int GateAddCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --gate-add \"<name-or-folder>\"");
            var query = string.Join(" ", args[1..]).Trim('"', '\'');
            var game = FindGame(query);
            if (game == null) return PrintError($"No catalog card matches '{query}' (by name or folderName).");
            if (string.IsNullOrWhiteSpace(game.FolderName))
                return PrintError($"'{game.Name}' has no folderName — it can't be gated (the gate keys on folderName).");

            var gate = GateService.Load();
            bool added = gate.Add(game.FolderName!);
            GateService.Save(gate);   // creates gate.json if it didn't exist
            Console.WriteLine(added
                ? $"OK gate-add: {game.Name}  (folder: {game.FolderName})  — gate now has {gate.Count} card(s)."
                : $"Already gated: {game.Name}  (folder: {game.FolderName})  — gate has {gate.Count} card(s).");
            Log.Info($"CLI: gate-add {game.FolderName} (added={added}, count={gate.Count})");
            return 0;
        }

        /// <summary>`--gate-remove "&lt;name-or-folder&gt;"` — remove one card from the gate (creates an empty gate.json if missing).</summary>
        private int GateRemoveCommand(string[] args)
        {
            if (args.Length < 2) return PrintError("Usage: --gate-remove \"<name-or-folder>\"");
            var query = string.Join(" ", args[1..]).Trim('"', '\'');
            // Resolve the folderName through the catalog when possible; fall back to the raw query so a stale
            // gate entry whose card has since left the catalog can still be removed.
            var game = FindGame(query);
            var folder = game?.FolderName ?? query;

            var gate = GateService.Load();
            bool removed = gate.Remove(folder);
            GateService.Save(gate);   // also writes an empty gate.json if there was none (idempotent wipe-on-remove)
            Console.WriteLine(removed
                ? $"OK gate-remove: {(game?.Name ?? folder)}  (folder: {folder})  — gate now has {gate.Count} card(s)."
                : $"Not in gate: {(game?.Name ?? folder)}  (folder: {folder})  — gate has {gate.Count} card(s).");
            Log.Info($"CLI: gate-remove {folder} (removed={removed}, count={gate.Count})");
            return 0;
        }

        /// <summary>`--gate-list` — print the current gate (folderName + resolved display name).</summary>
        private int GateListCommand()
        {
            if (!GateService.Exists())
            {
                Console.WriteLine($"No gate (gate.json absent) — the full catalog is shown ({_gameManager?.AllGames?.Count ?? 0} cards).");
                return 0;
            }
            var gate = GateService.Load();
            if (gate.Count == 0)
            {
                Console.WriteLine("Gate is empty ([]) — the library is wiped (0 cards shown).");
                return 0;
            }
            Console.WriteLine($"Gated cards ({gate.Count}):");
            foreach (var folder in gate.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = _gameManager?.FindGameByFolderName(folder)?.Name;
                Console.WriteLine(name != null ? $"  {folder}  ({name})" : $"  {folder}  (not in catalog)");
            }
            return 0;
        }

        /// <summary>`--gate-clear` — empty the gate (writes an empty gate.json = wiped library).</summary>
        private int GateClearCommand()
        {
            GateService.Save(Array.Empty<string>());
            Console.WriteLine($"OK gate-clear: gate.json is now empty ([]) — the library is wiped (0 cards shown). Use --gate-add to activate cards.");
            Log.Info("CLI: gate-clear (gate emptied)");
            return 0;
        }

        /// <summary>`--gate-status` — print "gated: N active of M canon" (or "no gate (all N shown)").</summary>
        private int GateStatusCommand()
        {
            var canon = _gameManager?.AllGames?.Count ?? 0;
            if (!GateService.Exists())
            {
                Console.WriteLine($"no gate (all {canon} shown)");
                return 0;
            }
            // Count only gated folderNames that still resolve to a real catalog card (what --list-json will emit).
            var gate = GateService.Load();
            var active = _gameManager?.AllGames?.Count(g => g != null && !string.IsNullOrWhiteSpace(g.FolderName) && gate.Contains(g.FolderName!)) ?? 0;
            Console.WriteLine($"gated: {active} active of {canon} canon");
            return 0;
        }

        private GameInfo? FindGame(string name)
        {
            // [yabo-launcher fork] Search the FULL catalog, not the filtered visible list — otherwise CLI
            // name-lookups (--download/--run/--play/--path/--set-shader/--import-data) can't find experimental
            // or hidden ports (they're absent from _gameManager.Games once the experimental filter is on).
            var all = _gameManager?.AllGames;
            if (all == null) return null;

            return all.FirstOrDefault(g =>
                (g?.Name != null && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                (g?.FolderName != null && g.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase)));
        }

        private string CleanVersion(string? version)
        {
            if (string.IsNullOrEmpty(version)) return "v0.0.0";
            if (version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "Unknown";
            return "v" + version.TrimStart('v', 'V');
        }

        private int PrintError(string message)
        {
            WriteColor("ERROR: ", ColorError);
            Console.WriteLine(message);
            Console.WriteLine();
            return 1;
        }

        private void PrintHelpItem(string command, string desc)
        {
            Console.Write("  ");
            WriteColor(command.PadRight(30), ColorSuccess);
            Console.WriteLine(desc);
        }

        /// <summary>[yabo-launcher fork] Lowercase hex SHA-1 of a file (for --import-data hash routing).</summary>
        private static string ComputeSha1Hex(string path)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        /// <summary>[yabo-launcher fork] Normalize a declared hash for comparison (strip 0x, lowercase).</summary>
        private static string NormalizeHash(string? hash) =>
            (hash ?? string.Empty).Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();

        private void PrintLine() => Console.WriteLine(new string('─', 70));

        private void WriteColor(string text, ConsoleColor color)
        {
            var old = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.Write(text);
            }
            finally
            {
                Console.ForegroundColor = old;
            }
        }
    }
}

