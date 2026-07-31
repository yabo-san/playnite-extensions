using System.IO;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Minimal, dependency-free file logger so there's an actual record of what
    /// the launcher did — what installed, what launched, where files went, what got wired, and errors.
    ///
    /// Writes timestamped lines to <c>&lt;exe dir&gt;\logs\yabo-launcher.log</c> (rolls to .1 past ~2 MB).
    /// Thread-safe and never throws — logging must not be able to break an install/launch. This replaces
    /// the old Debug.WriteLine diagnostics, which were compiled out of Release builds entirely.
    /// </summary>
    public static class Log
    {
        private static readonly object _gate = new();
        private static string? _logPath;
        private const long MaxBytes = 2_000_000;

        /// <summary>When true (set by the CLI), WARN/ERROR lines are also echoed to stderr.</summary>
        public static bool EchoToConsole { get; set; }

        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                    try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
                    _logPath = Path.Combine(dir, "yabo-launcher.log");
                }
                return _logPath;
            }
        }

        public static void Info(string message)  => Write("INFO", message);
        public static void Warn(string message)  => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        /// <summary>Logs an exception with its message and stack trace.</summary>
        public static void Error(string context, Exception ex)
            => Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        /// <summary>Writes a session banner (call once at startup).</summary>
        public static void SessionStart(string mode, string? extra = null)
        {
            Write("INFO", "================================================================");
            // [yabo-launcher fork] Append the build stamp (from build-info.json beside the exe, with a fallback
            // to the exe's LastWriteTime) so each log makes the RUNNING build identifiable — e.g. "(build
            // 2026-06-01T15:40:00)". Best-effort; BuildInfo never throws.
            Write("INFO", $"yabo-launcher session start ({mode}) — base dir: {AppContext.BaseDirectory}"
                          + (string.IsNullOrEmpty(extra) ? "" : $" — {extra}")
                          + $" (build {BuildInfo.Short()})");
        }

        private static void Write(string level, string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                lock (_gate)
                {
                    RotateIfNeeded();
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                if (EchoToConsole && level != "INFO")
                    Console.Error.WriteLine(line);
            }
            catch
            {
                // Logging must never throw.
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
                    if (File.Exists(rolled))
                        File.Delete(rolled);
                    File.Move(LogPath, rolled);
                }
            }
            catch
            {
                // Non-fatal: keep appending to the current file.
            }
        }
    }
}
