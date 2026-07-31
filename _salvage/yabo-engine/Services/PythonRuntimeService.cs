using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Managed Python runtime. Some ports' tooling shells out to a bare <c>python</c>
    /// (e.g. zelda3's <c>restool.py</c> needs PIL/pillow) and breaks against whatever Python is on the user's
    /// PATH — the classic "pillow installed for 3.11 but `python` is the Store 3.12" failure. We sidestep that
    /// by provisioning a known-good, relocatable CPython (python-build-standalone) with the needed packages and
    /// injecting it ahead of the system Python when launching a port flagged <c>pythonTooling</c>.
    ///
    /// Downloaded + pip-prepared once, cached under <c>runtime/python</c>. ~30 MB, only when first needed.
    /// </summary>
    public static class PythonRuntimeService
    {
        // python-build-standalone "install_only" build: relocatable CPython 3.11 incl. pip. Update the tag to bump.
        private const string DownloadUrl =
            "https://github.com/astral-sh/python-build-standalone/releases/download/20240814/cpython-3.11.9+20240814-x86_64-pc-windows-msvc-install_only.tar.gz";
        private static readonly string[] Packages = { "pillow", "pyyaml" };

        private static string Root => Path.Combine(AppContext.BaseDirectory, "runtime");
        private static string PyDir => Path.Combine(Root, "python");
        private static string PyExe => Path.Combine(PyDir, "python.exe");
        private static string ReadyMarker => Path.Combine(PyDir, ".yabo-ready");

        public static bool IsReady => File.Exists(ReadyMarker);
        public static string PythonExe => PyExe;

        /// <summary>Provisions the managed Python (download + extract + pip-install) on first call; cached after.
        /// Returns the python home dir, or null on failure.</summary>
        public static async Task<string?> EnsureAsync()
        {
            if (IsReady) return PyDir;
            // [yabo-launcher fork] Cross-process guard: two concurrent launches (double-click) both downloaded
            // python.tar.gz to the same path → file-lock IOException. Serialize provisioning with a system-wide
            // named mutex, re-check readiness inside it, and use a process-unique temp file.
            using var mutex = new System.Threading.Mutex(false, "Global\\yabo-managed-python-provision");
            bool held = false;
            try
            {
                try { held = mutex.WaitOne(TimeSpan.FromMinutes(6)); }
                catch (System.Threading.AbandonedMutexException) { held = true; }
                if (IsReady) return PyDir;   // another launch finished provisioning while we waited
                Directory.CreateDirectory(Root);
                Log.Info("PythonRuntime: provisioning managed Python 3.11 (one-time ~30MB download)…");

                var tgz = Path.Combine(Root, $"python.{Environment.ProcessId}.tar.gz");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) })
                using (var resp = await http.GetAsync(DownloadUrl).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tgz);
                    await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                }

                if (Directory.Exists(PyDir)) Directory.Delete(PyDir, true);
                // [yabo-launcher fork] Extract via Windows' bundled tar (bsdtar). System.Formats.Tar MANGLED this
                // archive's entry names (python.exe -> "python.exe_hon.exe", python311.dll -> "..._311.dll"), so
                // the managed Python never appeared. tar handles python-build-standalone cleanly; the archive's
                // top dir is "python/", so with cwd=Root we get Root/python/python.exe.
                var tarPsi = new ProcessStartInfo("tar")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    WorkingDirectory = Root,
                };
                tarPsi.ArgumentList.Add("-xzf");
                tarPsi.ArgumentList.Add(tgz);
                using (var tarProc = Process.Start(tarPsi)!)
                {
                    var terr = await tarProc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    await tarProc.WaitForExitAsync().ConfigureAwait(false);
                    if (tarProc.ExitCode != 0)
                    {
                        Log.Warn($"PythonRuntime: tar extract failed (exit {tarProc.ExitCode}): {terr}");
                        return null;
                    }
                }
                try { File.Delete(tgz); } catch { /* non-fatal */ }

                if (!File.Exists(PyExe))
                {
                    Log.Warn($"PythonRuntime: python.exe not found after extract ({PyExe})");
                    return null;
                }

                var pip = new ProcessStartInfo(PyExe)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = PyDir,
                };
                pip.ArgumentList.Add("-m"); pip.ArgumentList.Add("pip"); pip.ArgumentList.Add("install");
                pip.ArgumentList.Add("--no-warn-script-location");
                foreach (var p in Packages) pip.ArgumentList.Add(p);
                using (var proc = Process.Start(pip)!)
                {
                    var err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                    if (proc.ExitCode != 0)
                    {
                        Log.Warn($"PythonRuntime: pip install failed (exit {proc.ExitCode}): {err}");
                        return null;
                    }
                }

                File.WriteAllText(ReadyMarker, DateTime.UtcNow.ToString("o"));
                Log.Info($"PythonRuntime: ready at {PyDir} (packages: {string.Join(", ", Packages)})");
                return PyDir;
            }
            catch (Exception ex)
            {
                Log.Error("PythonRuntime: provisioning failed", ex);
                return null;
            }
            finally
            {
                if (held) { try { mutex.ReleaseMutex(); } catch { } }
            }
        }

        /// <summary>Prepends the managed Python (+ its Scripts dir) to a child process's PATH so the port's
        /// bare <c>python</c> calls resolve to ours, not the system one. No-op if not provisioned.</summary>
        public static void InjectIntoPath(ProcessStartInfo startInfo)
        {
            if (!IsReady) return;
            var scripts = Path.Combine(PyDir, "Scripts");
            var existing = startInfo.EnvironmentVariables.ContainsKey("Path")
                ? startInfo.EnvironmentVariables["Path"]
                : Environment.GetEnvironmentVariable("Path");
            startInfo.EnvironmentVariables["Path"] = $"{PyDir};{scripts};{existing}";
            Log.Info($"PythonRuntime: injected managed Python into launch PATH ({PyDir})");
        }
    }
}
