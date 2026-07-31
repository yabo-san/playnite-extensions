using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Reads the build stamp written by build.ps1 (build-info.json beside the exe) so the
    /// running build is identifiable — surfaced in the session-start log line, the <c>--version</c> command, and
    /// <c>--list-json</c>'s top-level <c>build</c> field (which the UI displays). All best-effort: if
    /// build-info.json is missing/corrupt, falls back to the exe's LastWriteTime; never throws.
    /// </summary>
    public static class BuildInfo
    {
        private static bool _loaded;
        private static string? _built;   // ISO-8601 build timestamp (or null if unknown)
        private static string? _rid;     // runtime identifier the build targeted (e.g. win-x64)

        /// <summary>The build timestamp (ISO-8601) or null when it can't be determined.</summary>
        public static string? Built { get { Load(); return _built; } }

        /// <summary>The runtime identifier from build-info.json (or null).</summary>
        public static string? Rid { get { Load(); return _rid; } }

        /// <summary>The running assembly's version (AssemblyVersion), e.g. "1.0.0.0".</summary>
        public static string AssemblyVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "build-info.json");
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("built", out var b) && b.ValueKind == JsonValueKind.String)
                        _built = b.GetString();
                    if (root.TryGetProperty("rid", out var r) && r.ValueKind == JsonValueKind.String)
                        _rid = r.GetString();
                }
            }
            catch
            {
                // Corrupt/unreadable — fall through to the exe-timestamp fallback below.
            }

            if (string.IsNullOrWhiteSpace(_built))
            {
                // Fallback: the exe's own LastWriteTime is a decent "when was this built" proxy.
                try
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                        _built = File.GetLastWriteTime(exe).ToString("o");
                }
                catch { /* leave _built null — callers tolerate "unknown" */ }
            }
        }

        /// <summary>A short, log/CLI-friendly string, e.g. "2026-06-01T15:40:00" (date+time, no fractional/zone)
        /// when known, else "unknown".</summary>
        public static string Short()
        {
            var b = Built;
            if (string.IsNullOrWhiteSpace(b)) return "unknown";
            return DateTime.TryParse(b, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToString("yyyy-MM-ddTHH:mm:ss")
                : b!;
        }
    }
}
