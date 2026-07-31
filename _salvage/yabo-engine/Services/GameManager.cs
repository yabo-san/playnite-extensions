using GitHubLauncher.Core.Models;
using GitHubLauncher.Core.Services;
using GithubLauncher.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GithubLauncher.Services
{
    public class GameManager : INotifyPropertyChanged, IDisposable
    {
        private static readonly GithubLauncherProfile Profile = GithubLauncherProfile.Instance;
        public AppSettings _settings = new();
        private readonly HttpClient _httpClient;
        private bool _disposed;
        private string _appsFolder;
        private readonly string _cacheFolder;
        private readonly string _appsConfigPath;
        private readonly string _userAppsConfigPath;
        private readonly string _legacyGamesConfigPath;

        public ObservableCollection<GameInfo> Games { get; set; } = [];
        /// <summary>[yabo-launcher fork] The FULL catalog (unfiltered) — the Tauri UI reads this via --list-json
        /// and does its own filtering (Show Experimental, category, search).</summary>
        public IReadOnlyList<GameInfo> AllGames => _allGames;

        // [yabo-launcher fork] Full loaded set (status-checked); Games is the visible view filtered by
        // hidden + the active category. Lets category switches re-filter without re-fetching from GitHub.
        private List<GameInfo> _allGames = new();
        /// <summary>Active sidebar category filter; null/empty = All.</summary>
        public string? CategoryFilter { get; set; }
        /// <summary>[yabo-launcher fork] When true, the visible list shows only installed ports — a live,
        /// reversible filter (does not persist or hide anything). Toggled from the sidebar.</summary>
        public bool InstalledOnlyFilter { get; private set; }
        /// <summary>[yabo-launcher fork] When false (default), experimental ports (machine-scouted, untested)
        /// are hidden so the catalog shows only the curated set. "Experimental" is a cross-cutting trust flag,
        /// NOT a platform category — these ports keep their real platform category and reveal under it when on.</summary>
        public bool ShowExperimentalFilter { get; private set; }
        /// <summary>Distinct categories present in the catalog (for the sidebar), sorted.</summary>
        public IReadOnlyList<string> Categories =>
            _allGames.Select(g => g?.Category).Where(c => !string.IsNullOrWhiteSpace(c))
                     .Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

        public HttpClient HttpClient => _httpClient;
        public string AppsFolder => _appsFolder;
        public string GamesFolder => _appsFolder;
        public string CacheFolder => _cacheFolder;

        private string _currentVersionString = string.Empty;
        public string CurrentVersionString
        {
            get => _currentVersionString;
            set
            {
                if (_currentVersionString != value)
                {
                    _currentVersionString = value;
                    OnPropertyChanged(nameof(CurrentVersionString));
                }
            }
        }

        public GameManager()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", Profile.UserAgent);
            _httpClient.Timeout = TimeSpan.FromMinutes(30);

            try
            {
                _settings = AppSettings.Load();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings in GameManager: {ex.Message}");
                _settings = new AppSettings();
            }

            _appsFolder = !string.IsNullOrEmpty(_settings?.AppsPath)
                ? _settings.AppsPath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Profile.DefaultInstallFolderName);

            _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");
            _appsConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps.json");
            // [canon + user backend] The user's OWN catalog (their added releases). Loaded + merged ON TOP of
            // canon apps.json, so OTA canon updates never clobber it. Absent by default (dormant).
            _userAppsConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user-apps.json");
            _legacyGamesConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "games.json");

            try
            {
                Directory.CreateDirectory(_appsFolder);
                Directory.CreateDirectory(_cacheFolder);
                GitHubApiCache.Initialize(_cacheFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create directories: {ex.Message}");
            }

            LoadVersionString();
            // NOTE: do NOT round-trip apps.json on construction. The old `ValidateAndFixAppsJsonAsync()`
            // call here rewrote the shipped catalog on every launch through SerializeApp — which strips
            // any field SerializeApp doesn't emit and re-encodes names — silently degrading the catalog
            // (it was wiping `ingest` tags and mangling non-ASCII names on first run). Legacy games.json
            // migration is already handled lazily in LoadAppsFromJsonAsync; nothing else needs a re-save.
            // The catalog is a shipped, canonical artifact — only an explicit user edit (Apps Manager)
            // should ever write it. [yabo-launcher fork]
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
                _httpClient.Dispose();

            _disposed = true;
        }

        public async Task CheckAllUpdatesAsync()
        {
            await LoadGamesAsync(forceUpdateCheck: true);
        }

        private async Task ValidateAndFixAppsJsonAsync()
        {
            try
            {
                var apps = await LoadAppsFromJsonAsync().ConfigureAwait(false);
                await SaveAppsToJsonAsync(apps).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during apps.json integrity check: {ex.Message}");
            }
        }

        private void LoadVersionString()
        {
            try
            {
                string versionFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
                CurrentVersionString = File.Exists(versionFilePath)
                    ? File.ReadAllText(versionFilePath).Trim()
                    : "Version information not found";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading version: {ex.Message}");
                CurrentVersionString = "Version loading failed";
            }
        }

        public GameInfo? GetLatestPlayedInstalledGame()
        {
            if (Games == null || string.IsNullOrEmpty(_appsFolder))
                return null;

            DateTime latestTime = DateTime.MinValue;
            GameInfo? latestGame = null;
            // [yabo-launcher fork] Iterate the FULL catalog (_allGames), not the visible/filtered Games —
            // otherwise the Continue button changes depending on the active sidebar category.
            foreach (var game in _allGames)
            {
                if (game == null || string.IsNullOrEmpty(game.FolderName))
                    continue;

                // Tools/utilities (e.g. Shmuparchify) and external link-outs aren't
                // "games" — never let them become the Continue/Resume target, even if launched once.
                if (game.IsExternal || string.Equals(game.Category, "Tools", StringComparison.OrdinalIgnoreCase))
                    continue;

                var gamePath = game.GetInstallPath(_appsFolder);
                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");
                if (File.Exists(lastPlayedPath))
                {
                    var timeString = File.ReadAllText(lastPlayedPath).Trim();
                    if (DateTime.TryParseExact(timeString, "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out DateTime lastPlayed) && lastPlayed > latestTime)
                    {
                        latestTime = lastPlayed;
                        latestGame = game;
                    }
                }
            }
            return latestGame;
        }

        private async Task<List<GameInfo>> LoadAppsFromJsonAsync()
        {
            List<GameInfo> canon;
            if (!File.Exists(_appsConfigPath))
            {
                if (File.Exists(_legacyGamesConfigPath))
                {
                    canon = await LoadAppsFromFileAsync(_legacyGamesConfigPath).ConfigureAwait(false);
                    await SaveAppsToJsonAsync(canon).ConfigureAwait(false);
                }
                else
                {
                    await SaveAppsToJsonAsync([]).ConfigureAwait(false);
                    canon = [];
                }
            }
            else
            {
                canon = await LoadAppsFromFileAsync(_appsConfigPath).ConfigureAwait(false);
            }

            return await MergeUserCatalogAsync(canon).ConfigureAwait(false);
        }

        /// <summary>
        /// [canon + user backend] Merge the user's own catalog (<c>user-apps.json</c>) ON TOP of the canon
        /// feed. Canon is OTA-managed and read-only to the user; user-apps.json is theirs and SURVIVES OTA
        /// canon updates (the editor writes there, never to canon). A user entry whose folderName matches a
        /// canon entry overrides it (user tweaks win); new folderNames are appended after canon, preserving
        /// canon order. Absent or unreadable user file =&gt; canon returned unchanged (fully dormant default).
        /// </summary>
        private async Task<List<GameInfo>> MergeUserCatalogAsync(List<GameInfo> canon)
        {
            if (string.IsNullOrEmpty(_userAppsConfigPath) || !File.Exists(_userAppsConfigPath))
            {
                return canon;
            }

            try
            {
                var userApps = await LoadAppsFromFileAsync(_userAppsConfigPath).ConfigureAwait(false);
                if (userApps == null || userApps.Count == 0)
                {
                    return canon;
                }

                var overrides = new Dictionary<string, GameInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in userApps)
                {
                    if (!string.IsNullOrWhiteSpace(u.FolderName))
                    {
                        overrides[u.FolderName!] = u;
                    }
                }

                var merged = new List<GameInfo>(canon.Count + overrides.Count);
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in canon)
                {
                    if (!string.IsNullOrWhiteSpace(c.FolderName) && overrides.TryGetValue(c.FolderName!, out var ov))
                    {
                        merged.Add(ov);
                        emitted.Add(c.FolderName!);
                    }
                    else
                    {
                        merged.Add(c);
                        if (!string.IsNullOrWhiteSpace(c.FolderName)) { emitted.Add(c.FolderName!); }
                    }
                }
                foreach (var u in userApps)
                {
                    if (!string.IsNullOrWhiteSpace(u.FolderName) && emitted.Add(u.FolderName!))
                    {
                        merged.Add(u);   // brand-new user release (no canon counterpart)
                    }
                }
                return merged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"user-apps.json merge skipped: {ex.Message}");
                return canon;   // fail-safe: a bad user file never breaks canon loading
            }
        }

        private async Task<List<GameInfo>> LoadAppsFromFileAsync(string path)
        {
            try
            {
                string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                return ParseAppsRoot(document.RootElement);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading {Path.GetFileName(path)}: {ex.Message}");
                return [];
            }
        }

        private List<GameInfo> ParseAppsRoot(JsonElement root)
        {
            var apps = new List<GameInfo>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                apps.AddRange(ParseAppArray(root));
                return apps;
            }

            if (root.TryGetProperty("apps", out var appsArray))
            {
                apps.AddRange(ParseAppArray(appsArray));
            }

            foreach (var legacySection in new[] { "standard", "experimental", "custom" })
            {
                if (root.TryGetProperty(legacySection, out var legacyArray))
                    apps.AddRange(ParseAppArray(legacyArray));
            }

            // Dedupe true duplicates, but DON'T collapse entries that have no repository (external
            // link-outs like the GTA decomps / Zandronum) — those would all share the empty key and
            // all but one would be silently dropped. [yabo-launcher fork] Key on repository+folderName,
            // not repository alone: two distinct catalog entries can legitimately share an engine repo but
            // install to different folders / run different games (e.g. "Super Mario World" and the native
            // "Super Mario All-Stars" both ship from snesrev/smw). Keying on repo alone silently dropped the
            // second. Fall back to folderName, then name, when there's no repository.
            return apps
                .GroupBy(app => !string.IsNullOrWhiteSpace(app.Repository)
                                ? "repo::" + app.Repository + "::" + (app.FolderName ?? string.Empty)
                            : !string.IsNullOrWhiteSpace(app.FolderName) ? "folder::" + app.FolderName
                            : "name::" + (app.Name ?? string.Empty),
                         StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private List<GameInfo> ParseAppArray(JsonElement appsArray)
        {
            var apps = new List<GameInfo>();

            foreach (var appElement in appsArray.EnumerateArray())
            {
                try
                {
                    var app = new GameInfo
                    {
                        Name = (appElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null) ?? string.Empty,
                        Repository = (appElement.TryGetProperty("repository", out var repoElement) ? repoElement.GetString() : null) ?? string.Empty,
                        FolderName = (appElement.TryGetProperty("folderName", out var folderElement) ? folderElement.GetString() : null) ?? string.Empty,
                        InstallPath = appElement.TryGetProperty("installPath", out var installPathElement) ? installPathElement.GetString() : null,
                        GameIconUrl = GetIconUrl(appElement),
                        PreferredVersion = appElement.TryGetProperty("preferredVersion", out var preferredVersionElement) ? preferredVersionElement.GetString() : null,
                        SkippedUpdateVersion = appElement.TryGetProperty("skippedUpdateVersion", out var skippedUpdateVersionElement) ? skippedUpdateVersionElement.GetString() : null,
                        IsExperimental = appElement.TryGetProperty("experimental", out var experimentalElement)
                                         && experimentalElement.ValueKind == JsonValueKind.True,
                        IsCustom = true,
                        GameManager = this,
                        DataFiles = ParseDataFiles(appElement),
                        Platforms = ParsePlatforms(appElement),
                        AssetPattern = appElement.TryGetProperty("assetPattern", out var assetPatternElement) ? assetPatternElement.GetString() : null,
                        DownloadUrlTemplate = appElement.TryGetProperty("downloadUrlTemplate", out var dlUrlTemplateElement) ? dlUrlTemplateElement.GetString() : null,
                        Category = appElement.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() : null,
                        ExternalUrl = appElement.TryGetProperty("externalUrl", out var externalUrlElement) ? externalUrlElement.GetString() : null,
                        Ingest = appElement.TryGetProperty("ingest", out var ingestElement) ? ingestElement.GetString() : null,
                        // [yabo-launcher fork] Per-card Steam appid for steam-data ingest (Steam-first data source).
                        SteamAppId = appElement.TryGetProperty("steamAppId", out var steamAppIdElement) && steamAppIdElement.ValueKind == JsonValueKind.Number && steamAppIdElement.TryGetInt32(out var steamAppIdVal) ? steamAppIdVal : (int?)null,
                        // [yabo-launcher fork] Internet-Archive in-app install (RohanKar parity): direct
                        // archive.org download URL of the port repack + its item identifier.
                        ContentUrl = appElement.TryGetProperty("contentUrl", out var contentUrlElement) && contentUrlElement.ValueKind == JsonValueKind.String ? contentUrlElement.GetString() : null,
                        IaIdentifier = appElement.TryGetProperty("iaIdentifier", out var iaIdentifierElement) && iaIdentifierElement.ValueKind == JsonValueKind.String ? iaIdentifierElement.GetString() : null,
                        // [yabo-launcher fork — itch.io route] owner-only tool installs (e.g. GMEdit); never gated into the public feed.
                        ItchUrl = appElement.TryGetProperty("itchUrl", out var itchUrlEl) && itchUrlEl.ValueKind == JsonValueKind.String ? itchUrlEl.GetString() : null,
                        ItchGameId = appElement.TryGetProperty("itchGameId", out var itchIdEl) && itchIdEl.ValueKind == JsonValueKind.Number && itchIdEl.TryGetInt64(out var itchIdVal) ? itchIdVal : (long?)null,
                        ItchUploadMatch = appElement.TryGetProperty("itchUploadMatch", out var itchMatchEl) && itchMatchEl.ValueKind == JsonValueKind.String ? itchMatchEl.GetString() : null,
                        // [yabo-launcher fork] favorite ★ (apps.json "favorite"); set by the desktop star or mobile editor.
                        Favorite = appElement.TryGetProperty("favorite", out var favoriteElement) && favoriteElement.ValueKind == JsonValueKind.True,
                        Hidden = appElement.TryGetProperty("hidden", out var hiddenElement) && hiddenElement.ValueKind == JsonValueKind.True,
                        GameId = appElement.TryGetProperty("gameId", out var gameIdElement) && gameIdElement.ValueKind == JsonValueKind.String ? gameIdElement.GetString() : null,
                        Tested = appElement.TryGetProperty("tested", out var testedElement) ? testedElement.GetString() : null,
                        ArtName = appElement.TryGetProperty("artName", out var artNameElement) ? artNameElement.GetString() : null,
                        ArtUrl = appElement.TryGetProperty("artUrl", out var artUrlElement) ? artUrlElement.GetString() : null,
                        AppComponent = appElement.TryGetProperty("appComponent", out var appCompElement) && appCompElement.ValueKind == JsonValueKind.True,
                        PythonTooling = appElement.TryGetProperty("pythonTooling", out var pyToolElement) && pyToolElement.ValueKind == JsonValueKind.True,
                        ExecutableName = appElement.TryGetProperty("executableName", out var exeNmElement) && exeNmElement.ValueKind == JsonValueKind.String ? exeNmElement.GetString() : null,
                        LaunchArgs = appElement.TryGetProperty("launchArgs", out var launchArgsElement) && launchArgsElement.ValueKind == JsonValueKind.String ? launchArgsElement.GetString() : null,
                        BuildStep = appElement.TryGetProperty("buildStep", out var buildStepElement) && buildStepElement.ValueKind == JsonValueKind.String ? buildStepElement.GetString() : null,
                        BuildPatch = appElement.TryGetProperty("buildPatch", out var buildPatchElement) && buildPatchElement.ValueKind == JsonValueKind.String ? buildPatchElement.GetString() : null,
                        BuildSource = appElement.TryGetProperty("buildSource", out var buildSourceElement) && buildSourceElement.ValueKind == JsonValueKind.String ? buildSourceElement.GetString() : null,
                        BuildTarget = appElement.TryGetProperty("buildTarget", out var buildTargetElement) && buildTargetElement.ValueKind == JsonValueKind.String ? buildTargetElement.GetString() : null,
                        BuildSourceUrl = appElement.TryGetProperty("buildSourceUrl", out var buildSourceUrlElement) && buildSourceUrlElement.ValueKind == JsonValueKind.String ? buildSourceUrlElement.GetString() : null,
                        BuildExtractSource = appElement.TryGetProperty("buildExtractSource", out var buildExtractSourceElement) && buildExtractSourceElement.ValueKind == JsonValueKind.String ? buildExtractSourceElement.GetString() : null,
                        BuildSteps = ParseBuildSteps(appElement),
                        Games = ParseGames(appElement),
                        // [yabo-launcher fork] "companions": other cards' folderNames this card auto-installs after
                        // its own install (Doom 1 + 2 -> its 3 source-port engines). Empty when the key is absent.
                        Companions = ParseStringArray(appElement, "companions"),
                    };

                    apps.Add(app);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error parsing app: {ex.Message}");
                }
            }

            return apps;
        }

        // Parses the optional "dataFiles" array from an apps.json entry. Backward
        // compatible: apps without the key return an empty list (no data needs).
        private static List<DataFileNeed> ParseDataFiles(JsonElement appElement)
        {
            var needs = new List<DataFileNeed>();

            if (!appElement.TryGetProperty("dataFiles", out var dataFilesElement) ||
                dataFilesElement.ValueKind != JsonValueKind.Array)
            {
                return needs;
            }

            foreach (var entry in dataFilesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                needs.Add(new DataFileNeed
                {
                    Name = entry.TryGetProperty("name", out var n) ? n.GetString() : null,
                    TargetSubpath = entry.TryGetProperty("targetSubpath", out var t) ? t.GetString() : null,
                    Sha1 = entry.TryGetProperty("sha1", out var s) ? s.GetString() : null,
                    Xxh3 = entry.TryGetProperty("xxh3", out var x) ? x.GetString() : null,
                    // [yabo-launcher fork] "optional" data file: present-but-missing still lets the port install;
                    // it just doesn't unlock the game whose ROM is absent. Default false → required (other entries
                    // unaffected). Accept JSON true OR string "true" for tolerance.
                    Optional = entry.TryGetProperty("optional", out var o)
                               && (o.ValueKind == JsonValueKind.True
                                   || (o.ValueKind == JsonValueKind.String && string.Equals(o.GetString(), "true", StringComparison.OrdinalIgnoreCase))),
                });
            }

            return needs;
        }

        // [yabo-launcher fork] Parses the optional "buildSteps" array (multi-step build). Backward compatible:
        // entries without the key get an empty list and use the legacy single buildStep instead.
        private static List<BuildStepInfo> ParseBuildSteps(JsonElement appElement)
        {
            var steps = new List<BuildStepInfo>();
            if (!appElement.TryGetProperty("buildSteps", out var stepsElement) ||
                stepsElement.ValueKind != JsonValueKind.Array)
                return steps;

            foreach (var entry in stepsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                steps.Add(new BuildStepInfo
                {
                    Step = entry.TryGetProperty("step", out var st) ? st.GetString() : null,
                    Source = entry.TryGetProperty("source", out var sr) ? sr.GetString() : null,
                    Patch = entry.TryGetProperty("patch", out var pa) ? pa.GetString() : null,
                    Target = entry.TryGetProperty("target", out var tg) ? tg.GetString() : null,
                });
            }
            return steps;
        }

        // [yabo-launcher fork] Parses the optional "games" array (multi-game launch picker). Empty for
        // single-game ports.
        private static List<GameLaunchOption> ParseGames(JsonElement appElement)
        {
            var games = new List<GameLaunchOption>();
            if (!appElement.TryGetProperty("games", out var gamesElement) ||
                gamesElement.ValueKind != JsonValueKind.Array)
                return games;

            foreach (var entry in gamesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                games.Add(new GameLaunchOption
                {
                    Label = entry.TryGetProperty("label", out var lb) ? lb.GetString() : null,
                    Rom = entry.TryGetProperty("rom", out var rm) ? rm.GetString() : null,
                    Requires = entry.TryGetProperty("requires", out var rq) ? rq.GetString() : null,
                });
            }
            return games;
        }

        // [yabo-launcher fork] Parses the optional "platforms" array from an apps.json
        // entry, e.g. ["windows","linux","macos"]. Backward compatible: apps without the
        // key return an empty list (treated as "unknown / all platforms").
        private static List<string> ParsePlatforms(JsonElement appElement)
        {
            var platforms = new List<string>();

            if (!appElement.TryGetProperty("platforms", out var platformsElement) ||
                platformsElement.ValueKind != JsonValueKind.Array)
            {
                return platforms;
            }

            foreach (var entry in platformsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;

                var value = entry.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    platforms.Add(value.Trim());
            }

            return platforms;
        }

        // [yabo-launcher fork] Parses an optional flat string array (e.g. "companions") from an apps.json entry.
        // Backward compatible: a missing/non-array key returns an empty list. Blanks are skipped and trimmed.
        private static List<string> ParseStringArray(JsonElement appElement, string propertyName)
        {
            var values = new List<string>();

            if (!appElement.TryGetProperty(propertyName, out var arrayElement) ||
                arrayElement.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var entry in arrayElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;

                var value = entry.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value.Trim());
            }

            return values;
        }

        private static string? GetIconUrl(JsonElement appElement)
        {
            if (appElement.TryGetProperty("appIconUrl", out var appIconUrlElement) && appIconUrlElement.ValueKind != JsonValueKind.Null)
                return appIconUrlElement.GetString();

            if (appElement.TryGetProperty("gameIconUrl", out var gameIconUrlElement) && gameIconUrlElement.ValueKind != JsonValueKind.Null)
                return gameIconUrlElement.GetString();

            if (appElement.TryGetProperty("customDefaultIconUrl", out var legacyIconElement) && legacyIconElement.ValueKind != JsonValueKind.Null)
                return legacyIconElement.GetString();

            return null;
        }

        private static object SerializeApp(GameInfo app)
        {
            return new
            {
                name = app.Name,
                repository = app.Repository,
                folderName = app.FolderName,
                installPath = app.InstallPath,
                appIconUrl = app.GameIconUrl,
                preferredVersion = app.PreferredVersion,
                skippedUpdateVersion = app.SkippedUpdateVersion,
                assetPattern = app.AssetPattern,
                downloadUrlTemplate = app.DownloadUrlTemplate,
                category = app.Category,
                externalUrl = app.ExternalUrl,
                ingest = app.Ingest,
                steamAppId = app.SteamAppId,
                itchUrl = app.ItchUrl,
                itchGameId = app.ItchGameId,
                itchUploadMatch = app.ItchUploadMatch,
                gameId = app.GameId,
                buildStep = app.BuildStep,
                buildPatch = app.BuildPatch,
                buildSource = app.BuildSource,
                buildTarget = app.BuildTarget,
                buildSourceUrl = app.BuildSourceUrl,
                buildExtractSource = app.BuildExtractSource,
                buildSteps = (app.BuildSteps != null && app.BuildSteps.Count > 0)
                    ? app.BuildSteps.Select(b => new
                    {
                        step = b.Step,
                        source = b.Source,
                        patch = b.Patch,
                        target = b.Target,
                    }).ToList<object>()
                    : null,
                games = (app.Games != null && app.Games.Count > 0)
                    ? app.Games.Select(g => new
                    {
                        label = g.Label,
                        rom = g.Rom,
                        requires = g.Requires,
                    }).ToList<object>()
                    : null,
                executableName = app.ExecutableName,
                launchArgs = app.LaunchArgs,
                tested = app.Tested,
                artName = app.ArtName,
                artUrl = app.ArtUrl,
                appComponent = app.AppComponent ? (bool?)true : null,
                experimental = app.IsExperimental ? (bool?)true : null,
                platforms = (app.Platforms != null && app.Platforms.Count > 0)
                    ? app.Platforms.ToList<object>()
                    : null,
                companions = (app.Companions != null && app.Companions.Count > 0)
                    ? app.Companions.ToList<object>()
                    : null,
                dataFiles = (app.DataFiles != null && app.DataFiles.Count > 0)
                    ? app.DataFiles.Select(d => new
                    {
                        name = d.Name,
                        targetSubpath = d.TargetSubpath,
                        sha1 = d.Sha1,
                        xxh3 = d.Xxh3,
                        optional = d.Optional ? (bool?)true : null,
                    }).ToList<object>()
                    : null
            };
        }

        private async Task SaveAppsToJsonAsync(List<GameInfo> apps)
        {
            var data = new
            {
                apps = apps.Select(SerializeApp).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            await File.WriteAllTextAsync(_appsConfigPath, JsonSerializer.Serialize(data, options)).ConfigureAwait(false);
        }

        private void SaveAppsToJson(List<GameInfo> apps)
        {
            var data = new
            {
                apps = apps.Select(SerializeApp).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(_appsConfigPath, JsonSerializer.Serialize(data, options));
        }

        private async Task LoadCustomAndCachedIconsAsync()
        {
            if (Games == null || string.IsNullOrEmpty(_cacheFolder))
                return;

            foreach (var game in Games)
            {
                game?.LoadCustomIcon(_cacheFolder);
            }

            var tasks = Games
                .Where(g => g != null)
                .Select(g => g.LoadAndCacheDefaultIconAsync(_cacheFolder));

            await Task.WhenAll(tasks);
        }

        public async Task ClearIconCacheAsync()
        {
            try
            {
                var iconsDir = Path.Combine(_cacheFolder, "Icons");
                if (Directory.Exists(iconsDir))
                {
                    Directory.Delete(iconsDir, true);
                    await LoadCustomAndCachedIconsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear icon cache: {ex.Message}");
            }
        }

        public GameInfo? FindGameByName(string name)
        {
            return Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public GameInfo? FindGameByFolderName(string folderName)
        {
            return Games.FirstOrDefault(g => string.Equals(g.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task LoadGamesAsync(bool forceUpdateCheck = false)
        {
            var settings = AppSettings.Load();

            Games ??= [];
            var allApps = await LoadAppsFromJsonAsync();
            _allGames = allApps.Where(app => app != null).ToList();

            // [yabo-launcher fork] Hydrate the user-set local exe for any external (link-out) entry so it
            // launches from here instead of just opening the download page.
            foreach (var app in _allGames)
            {
                if (app.IsExternal && !string.IsNullOrWhiteSpace(app.FolderName) &&
                    settings.ExternalLaunchPaths != null &&
                    settings.ExternalLaunchPaths.TryGetValue(app.FolderName, out var exe))
                {
                    app.ExternalExePath = exe;
                }
            }

            // Build the visible view (hidden + active category filter).
            RebuildVisible(settings);

            await LoadCustomAndCachedIconsAsync();

            if (string.IsNullOrEmpty(_appsFolder))
                return;

            // Status-check the FULL set (not just the visible view) so switching category never shows
            // un-checked entries.
            await Task.WhenAll(_allGames.Where(app => app != null).Select(async app =>
            {
                try
                {
                    await app.CheckStatusAsync(_httpClient, _appsFolder, forceUpdateCheck);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error checking status for {app.Name}: {ex.Message}");
                }
            }));

            // [yabo-launcher fork] If the portable folder was moved since last run, repair the
            // absolute paths DoomLauncher/QuakeInjector hold for our engines — no manual --wire needed.
            await Task.Run(() => CategoryWiring.AutoHealIfMoved(this, _appsFolder));

            // [yabo-launcher fork] Fetch real cover art (SteamGridDB) for ports without a curated icon —
            // background + cached, only if an API key is set. Icons update live as each resolves.
            _ = ResolveCoverArtAsync(settings);
        }

        /// <summary>[yabo-launcher fork] Re-run the cover-art pass (e.g. after the user enters their
        /// SteamGridDB key in Settings) so art appears without a restart.</summary>
        public Task RefreshCoverArtAsync(AppSettings settings) => ResolveCoverArtAsync(settings);

        private async Task ResolveCoverArtAsync(AppSettings settings)
        {
            try
            {
                var svc = new SteamGridDbService(_httpClient, settings.SteamGridDbApiKey);
                if (!svc.Enabled) return;

                // [yabo-launcher fork] Default to SteamGridDB for ALL entries (cover art wins on a hit;
                // on a miss the GitHub owner avatar / curated icon remains). Cached, so it's one-time.
                var targets = _allGames.Where(g => g != null).ToList();
                if (targets.Count == 0) return;

                // A game is a "singleton" if only one port of it is in the catalog → search the GAME name
                // first (reliable; avoids a port name like "DevilutionX" grabbing a port-specific banner
                // instead of the Diablo cover). Several ports of one game → port-first to differentiate.
                var gameCounts = targets
                    .GroupBy(g => SteamGridDbService.SearchTerms(g!.Name, g.ArtName).game, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

                using var gate = new System.Threading.SemaphoreSlim(4);
                await Task.WhenAll(targets.Select(async g =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // A pinned direct art URL wins outright — no search, no cache.
                        if (!string.IsNullOrWhiteSpace(g!.ArtUrl))
                        {
                            g.ApplyResolvedIcon(g.ArtUrl!);
                            return;
                        }
                        var game = SteamGridDbService.SearchTerms(g!.Name, g.ArtName).game;
                        bool singleton = gameCounts.TryGetValue(game, out var n) && n == 1;
                        var url = await svc.ResolveCoverAsync(g.Name, g.ArtName, singleton).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(url))
                            g.ApplyResolvedIcon(url);
                    }
                    finally { gate.Release(); }
                }));

                svc.SaveCache();
                Log.Info($"SteamGridDB: cover-art pass done for {targets.Count} icon-less port(s).");
            }
            catch (Exception ex)
            {
                Log.Warn($"SteamGridDB cover-art resolution failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the visible <see cref="Games"/> collection from the full loaded set, applying the
        /// hidden filter and the active <see cref="CategoryFilter"/> (null/empty = All). Mutates the
        /// existing ObservableCollection in place so bound views update automatically. No network.
        /// </summary>
        public void RebuildVisible(AppSettings settings)
        {
            Games ??= [];
            Games.Clear();
            foreach (var app in _allGames)
            {
                if (app == null || IsGameHidden(settings, app))
                    continue;
                // [yabo-launcher fork] "Installed only" is a LIVE, reversible view filter — it does NOT
                // permanently hide games (the old button added every non-installed game to the hidden set,
                // which is how OutRun "vanished" catalog-wide). Turn it off and everything comes back.
                if (InstalledOnlyFilter && app.Status != GameStatus.Installed)
                    continue;
                // [yabo-launcher fork] Experimental ports stay hidden unless explicitly revealed; an installed
                // experimental port always shows (the user clearly opted into it).
                if (!ShowExperimentalFilter && app.IsExperimental && app.Status != GameStatus.Installed)
                    continue;
                if (!string.IsNullOrWhiteSpace(CategoryFilter) &&
                    !string.Equals(app.Category, CategoryFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                Games.Add(app);
            }
        }

        /// <summary>Sets the category filter (null/"" = All) and re-filters the visible list. No network.</summary>
        public void SetCategoryFilter(string? category, AppSettings settings)
        {
            CategoryFilter = string.IsNullOrWhiteSpace(category) ? null : category;
            RebuildVisible(settings);
        }

        /// <summary>[yabo-launcher fork] Live, non-destructive "installed only" toggle.</summary>
        public void SetInstalledOnlyFilter(bool on, AppSettings settings)
        {
            InstalledOnlyFilter = on;
            RebuildVisible(settings);
        }

        /// <summary>[yabo-launcher fork] Live toggle that reveals/hides experimental (untested, machine-scouted)
        /// ports. They keep their real platform category, so when shown they appear under N64/PC/Sega/etc.</summary>
        public void SetShowExperimentalFilter(bool on, AppSettings settings)
        {
            ShowExperimentalFilter = on;
            RebuildVisible(settings);
        }

        public async Task ExportGamesAsync()
        {
            try
            {
                var apps = await LoadAppsFromJsonAsync().ConfigureAwait(false);
                await SaveAppsToJsonAsync(apps).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"Apps exported successfully to {_appsConfigPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error exporting apps: {ex.Message}");
            }
        }

        public async Task UpdateGamesFolderAsync(string newPath)
        {
            try
            {
                string targetPath;

                if (!string.IsNullOrWhiteSpace(newPath))
                {
                    if (!Directory.Exists(newPath))
                        Directory.CreateDirectory(newPath);

                    targetPath = newPath;
                }
                else
                {
                    targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Profile.DefaultInstallFolderName);
                    Directory.CreateDirectory(targetPath);
                }

                _appsFolder = targetPath;
                Games.Clear();

                await LoadGamesAsync();

                OnPropertyChanged(nameof(Games));
                OnPropertyChanged(nameof(AppsFolder));
                OnPropertyChanged(nameof(GamesFolder));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating apps folder: {ex.Message}");
                _appsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Profile.DefaultInstallFolderName);
                Directory.CreateDirectory(_appsFolder);
                throw;
            }
        }

        private static string GetHiddenGameKey(GameInfo game)
        {
            if (!string.IsNullOrWhiteSpace(game.FolderName))
                return $"folder:{game.FolderName}";

            if (!string.IsNullOrWhiteSpace(game.Repository))
                return $"repo:{game.Repository}";

            return $"name:{game.Name ?? string.Empty}";
        }

        private static bool IsGameHidden(AppSettings settings, GameInfo game)
        {
            if (settings?.HiddenApps == null)
                return false;

            var hiddenKey = GetHiddenGameKey(game);
            return settings.HiddenApps.Contains(hiddenKey) ||
                   (!string.IsNullOrWhiteSpace(game.Name) && settings.HiddenApps.Contains(game.Name)) ||
                   IsGameManuallyHidden(settings, game);
        }

        public void ToggleUserHide(GameInfo game)
        {
            if (game == null)
                return;

            var settings = AppSettings.Load();
            if (IsGameManuallyHidden(settings, game))
            {
                RemoveManuallyHiddenGame(settings, game);
            }
            else
            {
                AddManuallyHiddenGame(settings, game);
            }
            AppSettings.Save(settings);
            FilterGames(settings);
        }

        public bool IsManuallyHidden(GameInfo game)
        {
            var settings = AppSettings.Load();
            return IsGameManuallyHidden(settings, game);
        }

        private static void AddHiddenGame(AppSettings settings, GameInfo game)
        {
            if (settings?.HiddenApps == null)
                return;

            var hiddenKey = GetHiddenGameKey(game);
            if (!settings.HiddenApps.Contains(hiddenKey))
                settings.HiddenApps.Add(hiddenKey);
        }

        public void HideGame(GameInfo game)
        {
            if (game == null)
                return;

            var settings = AppSettings.Load();
            if (!IsGameHidden(settings, game))
            {
                AddHiddenGame(settings, game);
                AppSettings.Save(settings);
                FilterGames(settings);
            }
        }

        public void UnhideAllGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenApps.Clear();
            AppSettings.Save(settings);
            FilterGames(settings);
        }

        public async Task HideAllNonInstalledGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenApps.Clear();
            AppSettings.Save(settings);

            await LoadGamesAsync();

            foreach (var game in Games)
            {
                if (game != null && game.Status == GameStatus.NotInstalled && !IsGameHidden(settings, game))
                    AddHiddenGame(settings, game);
            }
            AppSettings.Save(settings);
            await LoadGamesAsync();
        }

        private List<GameInfo> LoadGamesFromJson()
        {
            return LoadAppsFromJsonAsync().GetAwaiter().GetResult();
        }

        private void FilterGames(AppSettings settings)
        {
            if (Games == null || settings?.HiddenApps == null)
                return;

            for (int i = Games.Count - 1; i >= 0; i--)
            {
                if (Games[i] != null && IsGameHidden(settings, Games[i]))
                    Games.RemoveAt(i);
            }
        }

        private static bool IsGameManuallyHidden(AppSettings settings, GameInfo game)
        {
            if (settings?.ManuallyHiddenApps == null)
                return false;

            var key = GetHiddenGameKey(game);
            return settings.ManuallyHiddenApps.Contains(key) ||
                   (!string.IsNullOrWhiteSpace(game.Name) && settings.ManuallyHiddenApps.Contains(game.Name));
        }

        private static void AddManuallyHiddenGame(AppSettings settings, GameInfo game)
        {
            if (settings?.ManuallyHiddenApps == null)
                return;

            var key = GetHiddenGameKey(game);
            if (!settings.ManuallyHiddenApps.Contains(key))
                settings.ManuallyHiddenApps.Add(key);
        }

        private static void RemoveManuallyHiddenGame(AppSettings settings, GameInfo game)
        {
            if (settings?.ManuallyHiddenApps == null)
                return;

            var key = GetHiddenGameKey(game);
            settings.ManuallyHiddenApps.Remove(key);
            if (!string.IsNullOrWhiteSpace(game.Name))
                settings.ManuallyHiddenApps.Remove(game.Name);
        }

        public void RefreshGamesWithFilter(AppSettings settings)
        {
            _ = LoadGamesAsync();
        }

        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
