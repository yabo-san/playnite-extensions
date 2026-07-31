using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] OPTIONAL ROM-source adapter for a user's self-hosted RomM instance
    /// (reached over Tailscale). Given a needed sha1 (from a card's dataFiles), it asks RomM whether
    /// it holds a matching ROM and pulls just that file — the ROM-side counterpart to the GitHub/IA
    /// source adapters. Designed to slot into the source-precedence chain (RomM -> IA index -> prompt).
    ///
    /// DESIGN RULES (so this can NEVER break catalog feeding / --list-json / existing delivery):
    ///   * ADDITIVE: a standalone service. Nothing on the --list-json or install path calls it yet.
    ///   * DORMANT by default: <see cref="IsConfigured"/> is false until the user sets RommBaseUrl,
    ///     so every method no-ops out of the gate.
    ///   * FAIL-SAFE: every method swallows errors and returns null/false, so an unreachable or
    ///     misbehaving RomM falls through to the next source instead of throwing into the caller.
    ///
    /// The endpoint paths + auth scheme below are best-guess and marked TODO-confirm — RomM's API was
    /// unreachable when this was written (its api container was down). Confirm against the live API,
    /// then nothing else needs to change: the guards already keep it safe.
    /// </summary>
    public sealed class RommSource
    {
        /// <summary>A ROM RomM reports it holds (only the fields yabo needs to pull + verify it).</summary>
        public sealed class RommRom
        {
            public int Id { get; set; }
            public string FileName { get; set; } = string.Empty;
            public string Sha1 { get; set; } = string.Empty;
            public long Size { get; set; }
        }

        private readonly AppSettings _settings;
        private readonly HttpClient _http;

        public RommSource(AppSettings settings, HttpClient? http = null)
        {
            _settings = settings;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        /// <summary>Dormant until the user configures a RomM base URL (a Tailscale MagicDNS name).</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings?.RommBaseUrl);

        private string Base => _settings.RommBaseUrl!.TrimEnd('/');

        /// <summary>
        /// Apply the configured auth. The scheme is configurable because RomM's exact API auth is
        /// unconfirmed: "basic" (RommUsername/RommPassword), "bearer"/"token", or "apikey" (X-Api-Key).
        /// </summary>
        private void ApplyAuth(HttpRequestMessage req)
        {
            var mode = (_settings.RommAuthMode ?? "basic").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "bearer":
                case "token":
                    if (!string.IsNullOrEmpty(_settings.RommToken))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.RommToken);
                    break;
                case "apikey":
                    if (!string.IsNullOrEmpty(_settings.RommToken))
                        req.Headers.TryAddWithoutValidation("X-Api-Key", _settings.RommToken);
                    break;
                default: // "basic"
                    if (!string.IsNullOrEmpty(_settings.RommUsername))
                    {
                        var raw = $"{_settings.RommUsername}:{_settings.RommPassword}";
                        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", b64);
                    }
                    break;
            }
        }

        /// <summary>
        /// Ask RomM for a ROM whose sha1 matches. Returns null when dormant, unreachable, or not found —
        /// never throws into the delivery path.
        /// TODO-confirm: RomM's search-by-hash endpoint + JSON field names (sha1_hash, id, fs_name).
        /// </summary>
        public async Task<RommRom?> QueryByHashAsync(string sha1, CancellationToken ct = default)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(sha1)) return null;
            try
            {
                // Best-guess: RomM exposes a rom list with a search term; each rom carries its hashes.
                var url = $"{Base}/api/roms?search_term={Uri.EscapeDataString(sha1)}&limit=50";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(req);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return MatchSha1(json, sha1);
            }
            catch { return null; } // fail-safe: a RomM problem never breaks delivery
        }

        /// <summary>
        /// Parse RomM's rom-list JSON and return the entry whose sha1 matches (case-insensitive).
        /// Tolerant of shape: a bare array, or an {items:[...]} / {roms:[...]} envelope.
        /// </summary>
        private static RommRom? MatchSha1(string json, string sha1)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement arr;
                if (root.ValueKind == JsonValueKind.Array) arr = root;
                else if (root.TryGetProperty("items", out var it)) arr = it;
                else if (root.TryGetProperty("roms", out var rm)) arr = rm;
                else return null;

                foreach (var e in arr.EnumerateArray())
                {
                    var h = GetStr(e, "sha1_hash") ?? GetStr(e, "sha1");
                    if (h != null && string.Equals(h, sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RommRom
                        {
                            Id = e.TryGetProperty("id", out var idv) && idv.TryGetInt32(out var id) ? id : 0,
                            FileName = GetStr(e, "fs_name") ?? GetStr(e, "file_name") ?? GetStr(e, "name") ?? string.Empty,
                            Sha1 = h,
                            Size = e.TryGetProperty("fs_size_bytes", out var sv) && sv.TryGetInt64(out var sz) ? sz : 0
                        };
                    }
                }
            }
            catch { /* tolerant: unknown shape -> no match */ }
            return null;
        }

        private static string? GetStr(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>
        /// Download the matched ROM to destPath. Returns false on any problem (the caller falls through
        /// to the next source / the user prompt). TODO-confirm: RomM's file-content endpoint shape.
        /// </summary>
        public async Task<bool> DownloadAsync(RommRom rom, string destPath, CancellationToken ct = default)
        {
            if (!IsConfigured || rom == null || rom.Id <= 0 || string.IsNullOrWhiteSpace(destPath)) return false;
            try
            {
                var fname = Uri.EscapeDataString(rom.FileName);
                var url = $"{Base}/api/roms/{rom.Id}/content/{fname}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(req);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return false;
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var fs = File.Create(destPath);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Lightweight reachability probe for the console/health UI. Never throws.</summary>
        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            if (!IsConfigured) return false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/api/heartbeat");
                ApplyAuth(req);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }
    }
}
