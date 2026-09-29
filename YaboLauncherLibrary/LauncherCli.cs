using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace YaboLauncherLibrary
{
    /// <summary>
    /// Runs the launcher with one command flag (--install, --uninstall, --launch)
    /// and an export id. The launcher prints one JSON line and exits 0 on success;
    /// a command that needs its window keeps running until the user closes it.
    /// </summary>
    public static class LauncherCli
    {
        public static Process Start(string exe, string command, string id)
        {
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                throw new FileNotFoundException("The y4bo launcher was not found. Set its path in the plugin settings.", exe);
            }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--{command} {Quote(id)}",
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var p = new Process { StartInfo = psi };
            p.Start();
            // Drained so a chatty launcher never blocks on a full pipe
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        /// <summary>Waits for exit, or until <paramref name="done"/> says the work is finished
        /// (the export shows the result) or the token is cancelled. True when either finished it.</summary>
        public static bool Wait(Process p, Func<bool> done, CancellationToken token, int pollMs = 1000)
        {
            while (!token.IsCancellationRequested)
            {
                if (p.WaitForExit(pollMs))
                {
                    p.WaitForExit();   // flush the async readers
                    return p.ExitCode == 0 || done();
                }
                if (done()) return true;
            }
            return false;
        }

        /// <summary>Windows command-line quoting for one argument.</summary>
        public static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new System.Text.StringBuilder("\"");
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', slashes); sb.Append(c); }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }
    }
}
