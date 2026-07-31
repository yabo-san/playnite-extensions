using System.IO;
using System.Text;
using System.Text.Json;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Auto-appended STRUCTURED action log — one JSON object per line (JSONL) so the
    /// owner can debug + catch regressions: every config/ini write (which file, which game, key old→new),
    /// every shader selection (in-engine GLSL), and every launch (exe + args).
    ///
    /// Lives beside the human-readable log (<c>&lt;exe dir&gt;\logs\actions.jsonl</c>, same dir as
    /// yabo-launcher.log per dev/LOG-LOCATIONS.md). Crash-safe by contract: <see cref="Write"/> NEVER throws
    /// into the caller — all IO/serialization errors are swallowed. Logging must not be able to break a
    /// config write, a shader assignment, or a launch.
    ///
    /// Each line: {"ts":"&lt;iso-8601&gt;","action":"...","game":"...",&lt;...fields...&gt;}
    /// </summary>
    public static class ActionLog
    {
        private static readonly object _gate = new();
        private static string? _logPath;
        private const long MaxBytes = 4_000_000;

        /// <summary>The JSONL action-log path (created lazily, in the same logs\ dir as the text log).</summary>
        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                    try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
                    _logPath = Path.Combine(dir, "actions.jsonl");
                }
                return _logPath;
            }
        }

        /// <summary>
        /// Append one structured event. <paramref name="fields"/> is any object (anonymous type is ideal)
        /// whose properties are merged in alongside ts/action/game. Never throws.
        /// </summary>
        /// <param name="action">Event kind, e.g. "config-write", "shader-select", "shader-bind", "launch".</param>
        /// <param name="game">The game/port name this event is about (null if not game-scoped).</param>
        /// <param name="fields">Extra fields (anonymous object) merged into the line.</param>
        public static void Write(string action, string? game, object? fields = null)
        {
            try
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("ts", DateTime.Now.ToString("o"));
                    w.WriteString("action", action);
                    if (game != null) w.WriteString("game", game);

                    if (fields != null)
                    {
                        // Merge the caller's fields as top-level properties (skip ts/action/game collisions).
                        foreach (var p in fields.GetType().GetProperties())
                        {
                            var nameLower = p.Name;
                            if (nameLower is "ts" or "action" or "game") continue;
                            object? val;
                            try { val = p.GetValue(fields); } catch { continue; }
                            w.WritePropertyName(p.Name);
                            WriteValue(w, val);
                        }
                    }

                    w.WriteEndObject();
                }

                var line = Encoding.UTF8.GetString(ms.ToArray());
                lock (_gate)
                {
                    RotateIfNeeded();
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Action logging must NEVER throw into the caller (config write / launch / shader path).
            }
        }

        private static void WriteValue(Utf8JsonWriter w, object? val)
        {
            switch (val)
            {
                case null: w.WriteNullValue(); break;
                case string s: w.WriteStringValue(s); break;
                case bool b: w.WriteBooleanValue(b); break;
                case int i: w.WriteNumberValue(i); break;
                case long l: w.WriteNumberValue(l); break;
                case double d: w.WriteNumberValue(d); break;
                case float f: w.WriteNumberValue(f); break;
                default: w.WriteStringValue(val.ToString()); break;
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var rolled = LogPath + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(LogPath, rolled);
                }
            }
            catch { /* non-fatal: keep appending */ }
        }

        /// <summary>
        /// Read recent events (newest-last order as written) for the dump command. Returns raw JSONL lines,
        /// optionally filtered to one game and limited to the last <paramref name="tail"/> lines. Never throws.
        /// </summary>
        public static List<string> ReadRecent(int tail, string? gameFilter)
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists(LogPath)) return result;
                string[] lines;
                lock (_gate) { lines = File.ReadAllLines(LogPath); }

                IEnumerable<string> seq = lines.Where(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(gameFilter))
                {
                    seq = seq.Where(l =>
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(l);
                            return doc.RootElement.TryGetProperty("game", out var g)
                                   && g.ValueKind == JsonValueKind.String
                                   && string.Equals(g.GetString(), gameFilter, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return false; }
                    });
                }
                var all = seq.ToList();
                if (tail > 0 && all.Count > tail) all = all.Skip(all.Count - tail).ToList();
                result = all;
            }
            catch { /* best-effort read */ }
            return result;
        }
    }
}
