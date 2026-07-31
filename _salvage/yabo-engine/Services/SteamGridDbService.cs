using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Resolves real cover art for a game from SteamGridDB (steamgriddb.com) so ports
    /// without a curated icon show box art instead of the generic disc / owner avatar. Results (including
    /// misses) are cached to disk, so the API is hit at most once per game name, ever.
    /// </summary>
    public class SteamGridDbService
    {
        private const string ApiBase = "https://www.steamgriddb.com/api/v2/";
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _cachePath;
        private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private bool _dirty;

        /// <summary>[yabo-launcher fork] Favorite artists' steam64 ids, in PRIORITY ORDER — when one of these
        /// uploaders has a portrait cover for a game, the resolver prefers it over the default top-ranked grid
        /// (earlier entries win ties). Loaded at runtime from favorite-artists.json beside the exe, so artists
        /// can be added/reordered with NO rebuild. dev/resolve-art.py reads the same file.</summary>
        private readonly string[] _preferredAuthors;

        public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey);

        public SteamGridDbService(HttpClient http, string? apiKey)
        {
            _http = http;
            _apiKey = apiKey?.Trim() ?? string.Empty;
            _cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", "steamgriddb-cache.json");
            _preferredAuthors = LoadPreferredAuthors();
            LoadCache();
        }

        private static string[] LoadPreferredAuthors()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "favorite-artists.json");
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["artists"] is JsonArray arr)
                {
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var n in arr)
                    {
                        var id = n?["steam64"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(id!);
                    }
                    return list.ToArray();
                }
            }
            catch (Exception ex) { Log.Warn($"favorite-artists.json load failed: {ex.Message}"); }
            return Array.Empty<string>();
        }

        /// <summary>Strips port/fork suffixes so the search hits the actual game
        /// ("Mario Kart 64: Recompiled" -> "Mario Kart 64", "Doom (DSDA-Doom)" -> "Doom").</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var s = Regex.Replace(name, @"\s*\([^)]*\)\s*", " ");                  // drop (Ship of Harkinian) etc.
            s = Regex.Replace(s, @"\s*:\s*(Recompiled|Recomp(\s+Plus)?|Decompilation|Decomp).*$", "",
                              RegexOptions.IgnoreCase);                              // drop ": Recompiled" suffix
            s = Regex.Replace(s, @"\s+(Recompiled|Recomp\s+Plus|Recomp|Decompilation|Decomp)$", "",
                              RegexOptions.IgnoreCase);                              // drop trailing "Recomp Plus"
            return s.Trim();
        }

        /// <summary>Derives the two search terms for an entry: the PORT/TOOL name (the parenthetical, e.g.
        /// "Ghostship") tried first, then the GAME name (artName override, else the cleaned title).</summary>
        public static (string port, string game) SearchTerms(string name, string? artName)
        {
            var m = Regex.Match(name ?? string.Empty, @"\(([^)]+)\)");
            var port = m.Success ? m.Groups[1].Value.Trim() : (name ?? string.Empty);
            var game = !string.IsNullOrWhiteSpace(artName) ? artName! : CleanName(name ?? string.Empty);
            return (CleanName(port), game);
        }

        /// <summary>Cover-art URL for an entry. When the game is a singleton (only one port of it in the
        /// catalog) we search the GAME name first (more reliable); when several ports share a game we search
        /// the PORT/TOOL name first (to differentiate). Cached, or null if none/disabled.</summary>
        public async Task<string?> ResolveCoverAsync(string name, string? artName, bool gameFirst)
        {
            if (!Enabled) return null;
            var (port, game) = SearchTerms(name, artName);
            var order = gameFirst ? new[] { game, port } : new[] { port, game };
            foreach (var term in order)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                var url = await ResolveOneAsync(term).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(url)) return url;
            }
            return null;
        }

        /// <summary>Resolves one search term (cached), or null.</summary>
        private async Task<string?> ResolveOneAsync(string rawName)
        {
            var key = CleanName(rawName);
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (_cache.TryGetValue(key, out var cached))
                return string.IsNullOrEmpty(cached) ? null : cached;

            string? url = null;
            try
            {
                var search = await GetJsonAsync($"search/autocomplete/{Uri.EscapeDataString(key)}").ConfigureAwait(false);
                if (search == null) return null; // request failed (bad/missing key, network) — don't cache a miss
                var gameId = (search?["data"] as JsonArray)?.Count > 0
                    ? search!["data"]![0]!["id"]?.GetValue<int>()
                    : null;
                if (gameId != null)
                {
                    // Pull a page of grids (not just the top one) so we can prefer our favorite artist's cover.
                    // Restrict to portrait 2:3 dimensions so we only get proper box-art "grids", never the wide
                    // hero-style banners (which look wrong squeezed into the 2:3 tile).
                    var grids = await GetJsonAsync($"grids/game/{gameId}?limit=50&types=static&nsfw=false&dimensions=600x900,342x482,660x930").ConfigureAwait(false);
                    var arr = grids?["data"] as JsonArray;
                    if (arr != null && arr.Count > 0)
                    {
                        // GLOBAL no_logo preference: the card prints the game title underneath, so a logo on the
                        // box art is redundant. Prefer "no_logo"-styled grids for EVERY game; fall back to the full
                        // any-style pool only when the game has no no_logo grid (so we never drop a cover for the
                        // sake of style). This is GLOBAL, not per-artist: a favorite may lack a no_logo cover for a
                        // given game, so we narrow the POOL, then apply the favorite-artist priority WITHIN it.
                        var nologo = new System.Collections.Generic.List<JsonNode?>();
                        foreach (var g in arr)
                            if (g?["style"]?.GetValue<string>() == "no_logo") nologo.Add(g);
                        var pool = nologo.Count > 0 ? nologo : new System.Collections.Generic.List<JsonNode?>(arr);

                        // Prefer a favorite artist's cover; if several qualify, honor their priority order.
                        JsonNode? preferred = null;
                        foreach (var fav in _preferredAuthors)
                        {
                            foreach (var g in pool)
                            {
                                if (g?["author"]?["steam64"]?.GetValue<string>() == fav)
                                {
                                    preferred = g;
                                    break;
                                }
                            }
                            if (preferred != null) break;
                        }
                        var pick = preferred ?? pool[0];
                        url = pick?["url"]?.GetValue<string>();
                        if (preferred != null)
                            Log.Info($"SteamGridDB: using favorite artist ({preferred["author"]?["name"]?.GetValue<string>()}) cover for '{key}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"SteamGridDB: lookup failed for '{key}': {ex.Message}");
                return null; // transient — don't cache a miss so we can retry next launch
            }

            _cache[key] = url ?? string.Empty; // cache hits AND misses (empty) so we never re-query
            _dirty = true;
            return url;
        }

        private async Task<JsonNode?> GetJsonAsync(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonNode.Parse(json);
        }

        private void LoadCache()
        {
            try
            {
                if (!File.Exists(_cachePath)) return;
                var dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(_cachePath));
                if (dict != null)
                    foreach (var kv in dict) _cache[kv.Key] = kv.Value;
            }
            catch (Exception ex) { Log.Warn($"SteamGridDB: cache load failed: {ex.Message}"); }
        }

        /// <summary>Persists the cache. Call once after a resolution batch.</summary>
        public void SaveCache()
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllText(_cachePath,
                    JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>(_cache),
                                             new JsonSerializerOptions { WriteIndented = true }));
                _dirty = false;
            }
            catch (Exception ex) { Log.Warn($"SteamGridDB: cache save failed: {ex.Message}"); }
        }
    }
}
