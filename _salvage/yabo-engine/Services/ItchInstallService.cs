using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork — itch.io route] Resolves a FREE itch.io game/app into a transient signed
    /// download URL; the normal IA download+extract path (InternetArchiveInstallService.InstallAsync)
    /// then takes over (SharpCompress content-sniffs the zip, so the no-extension URL is fine).
    ///
    /// itch requires an API key EVEN FOR FREE downloads (anonymous calls -> "authentication required").
    /// Flow, grounded in itch's own go-itchio client:
    ///   1. {itchUrl}/data.json            -> numeric game id   (public, no key)
    ///   2. GET api.itch.io/games/{id}/uploads  (Authorization: &lt;key&gt;, Accept: application/vnd.itch.v2)
    ///   3. api.itch.io/uploads/{uploadId}/download?api_key=&lt;key&gt;   (302 -> CDN file; HttpClient follows it)
    /// </summary>
    public static class ItchInstallService
    {
        private const string ApiBase = "https://api.itch.io";

        /// <summary>True when an itch API key is present (the route is otherwise dormant).</summary>
        public static bool HasKey(AppSettings? settings) => !string.IsNullOrWhiteSpace(settings?.ItchApiKey);

        /// <summary>Where the user gets a (free, unscoped) API key.</summary>
        public const string KeyUrl = "https://itch.io/user/settings/api-keys";

        /// <summary>
        /// Resolve {itchUrl | gameId} + key -> a download URL for the matched upload. Throws on any failure
        /// (no key, bad key, no uploads, no match) with a human-readable message.
        /// </summary>
        public static async Task<string> ResolveDownloadUrlAsync(
            HttpClient http, string? itchUrl, long? gameId, string? uploadMatch, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException($"itch route: no API key set. Get one at {KeyUrl}, then run --set-itch-key \"<key>\".");

            long id = gameId ?? await ResolveGameIdAsync(http, itchUrl).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/games/{id}/uploads");
            req.Headers.TryAddWithoutValidation("Authorization", apiKey);   // itch uses the RAW key — no "Bearer " prefix
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.itch.v2");
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"itch uploads list failed for game {id} ({(int)resp.StatusCode}): {Trunc(body)}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("uploads", out var uploads) ||
                uploads.ValueKind != JsonValueKind.Array || uploads.GetArrayLength() == 0)
                throw new InvalidOperationException($"itch game {id}: no uploads returned. {Trunc(body)}");

            long uploadId = PickUpload(uploads, uploadMatch);
            // key goes in the QUERY for the download endpoint (go-itchio AddAPICredentials)
            return $"{ApiBase}/uploads/{uploadId}/download?api_key={Uri.EscapeDataString(apiKey)}";
        }

        private static async Task<long> ResolveGameIdAsync(HttpClient http, string? itchUrl)
        {
            if (string.IsNullOrWhiteSpace(itchUrl))
                throw new InvalidOperationException("itch route: card has neither itchGameId nor itchUrl.");
            var dataUrl = itchUrl.TrimEnd('/') + "/data.json";
            var json = await http.GetStringAsync(dataUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)) return id;
            throw new InvalidOperationException($"itch: couldn't read a game id from {dataUrl}.");
        }

        // Pick deterministically: explicit filename/display-name substring first, then a "win" match, then first.
        private static long PickUpload(JsonElement uploads, string? match)
        {
            var selectors = string.IsNullOrWhiteSpace(match) ? new[] { "win" } : new[] { match!, "win" };
            foreach (var sel in selectors)
            {
                foreach (var u in uploads.EnumerateArray())
                {
                    var fn = (u.TryGetProperty("filename", out var f) ? f.GetString() : null) ?? "";
                    var dn = (u.TryGetProperty("display_name", out var d) ? d.GetString() : null) ?? "";
                    if (fn.IndexOf(sel, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        dn.IndexOf(sel, StringComparison.OrdinalIgnoreCase) >= 0)
                        return GetId(u);
                }
            }
            return GetId(uploads[0]);   // last resort: the first upload
        }

        private static long GetId(JsonElement u) =>
            u.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var uid)
                ? uid : throw new InvalidOperationException("itch: matched upload has no numeric id.");

        private static string Trunc(string s) => s != null && s.Length > 200 ? s.Substring(0, 200) : (s ?? "");
    }
}
