using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork — OTA feed] Pulls the CANON catalog (apps.json) from a remote, gated URL — the
    /// "private catalog file that gets updated" that IS the product. DORMANT by default (no RemoteFeedUrl =>
    /// no-op, the engine uses the bundled apps.json). FAIL-SAFE + ATOMIC: a failed or invalid fetch leaves
    /// the existing local apps.json untouched (last-good), and the previous file is backed up to
    /// apps.json.bak before a successful swap. The subscriber's token authorizes the fetch.
    ///
    /// Critically: user-apps.json (the user's own releases) and the exclusion list (settings.ManuallyHiddenApps)
    /// are SEPARATE from canon apps.json, so an OTA canon refresh never clobbers the user's additions or hides.
    /// </summary>
    public sealed class FeedSyncService
    {
        private readonly AppSettings _settings;
        private readonly HttpClient _http;
        private readonly string _appsPath;

        public FeedSyncService(AppSettings settings, string appsPath, HttpClient? http = null)
        {
            _settings = settings;
            _appsPath = appsPath;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>Dormant until the subscriber configures a feed URL.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings?.RemoteFeedUrl);

        /// <summary>
        /// Fetch the canon feed; on success atomically replace apps.json (old -> apps.json.bak).
        /// Returns true only when a valid new catalog was written. Never throws.
        /// </summary>
        public async Task<bool> SyncAsync(CancellationToken ct = default)
        {
            if (!IsConfigured) return false;
            try
            {
                // Resolve the feed body. A LOCAL feed (a plain path like D:\feeds\release.json, or a file:// URL,
                // or a UNC \\nas\share\release.json) is read straight off disk — HttpClient does NOT speak file://,
                // and this is how the platform loop is tested + how a feed served off a synced folder / file share
                // works without a web host. Everything else (http/https) goes over HTTP with the bearer token.
                string json;
                var url = _settings.RemoteFeedUrl.Trim();
                if (TryResolveLocalFeedPath(url, out var localPath))
                {
                    if (!File.Exists(localPath)) return false;
                    json = await File.ReadAllTextAsync(localPath, ct).ConfigureAwait(false);
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (!string.IsNullOrEmpty(_settings.RemoteFeedToken))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.RemoteFeedToken);
                    }
                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return false;
                    json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                if (!LooksLikeCatalog(json)) return false;   // never trust a non-catalog body

                // Atomic-ish swap: temp -> back up current -> move temp into place.
                var tmp = _appsPath + ".tmp";
                await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
                if (File.Exists(_appsPath))
                {
                    try { File.Copy(_appsPath, _appsPath + ".bak", overwrite: true); } catch { /* best-effort backup */ }
                }
                File.Move(tmp, _appsPath, overwrite: true);
                return true;
            }
            catch { return false; }   // fail-safe: keep the last-good local apps.json
        }

        /// <summary>
        /// Decide whether a feed URL points at the LOCAL filesystem and, if so, hand back the on-disk path.
        /// Handles: file:// URIs, UNC paths (\\host\share), and plain rooted paths (C:\..., /home/...).
        /// Returns false for http/https (and anything with a non-file URL scheme), which go over HTTP.
        /// </summary>
        internal static bool TryResolveLocalFeedPath(string url, out string localPath)
        {
            localPath = string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Explicit file:// URI → convert to a local path.
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { localPath = new Uri(url).LocalPath; return true; }
                catch { return false; }
            }

            // A real URL scheme (http:, https:, ftp:, …) is NOT local. Detect a scheme as letters then ':'
            // followed by '//' so a Windows drive root ("C:\") is not mistaken for a scheme.
            int colon = url.IndexOf(':');
            if (colon > 1 && url.Length > colon + 2 && url[colon + 1] == '/' && url[colon + 2] == '/')
                return false;   // e.g. https://...  → HTTP path

            // UNC (\\host\share) or a rooted local path → read from disk.
            if (Path.IsPathRooted(url)) { localPath = url; return true; }
            return false;
        }

        /// <summary>Guard against writing garbage: must parse + look like our catalog ({apps:[...]} or a bare array).</summary>
        private static bool LooksLikeCatalog(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array) return true;
                return root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array;
            }
            catch { return false; }
        }
    }
}
