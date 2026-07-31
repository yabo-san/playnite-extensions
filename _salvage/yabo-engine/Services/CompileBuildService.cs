using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Sibling of <see cref="BpsPatchService"/> for the "tcc-compile" build step.
    ///
    /// snesrev/sm (Super Metroid) bakes ROM-derived assets into the executable AT BUILD TIME, so — unlike
    /// zelda3/smw, which ship a prebuilt exe + a BPS asset patch — there is NO prebuilt sm.exe we can
    /// download. The only way to "just work" with the user's own ROM is to compile the port locally with
    /// that ROM present as sm.smc.
    ///
    /// We make this need ZERO toolchain on the user's machine by mirroring the upstream run_with_tcc.bat
    /// path: a self-contained portable TCC (FitzRoyX/tinycc) + SDL2 VC dev libs, both fetched into the
    /// install dir's third_party. Verified end-to-end in dev\_research\sm-native\tcc-test: TCC compiles
    /// the snesrev/sm tree against SDL2 2.24.1 into a working ~1.8 MB sm.exe that boots the ROM. The TCC
    /// build links SDL2 DYNAMICALLY, so we also copy SDL2.dll next to the exe (the .bat does the same).
    ///
    /// Steps (each logged), run at launch when sm.exe is missing:
    ///   1. Download the repo source archive (codeload zip of the default branch — snesrev/sm has no
    ///      release) and lay src/ + third_party/{gl_core,stb} into the install dir.
    ///   2. Download portable TCC -> third_party/tcc and SDL2 VC dev -> third_party/SDL2-2.24.1.
    ///   3. (ROM already placed as sm.smc by the existing dataFiles ingest.)
    ///   4. Run TCC (the run_with_tcc.bat command) -> sm.exe, then copy SDL2.dll beside it.
    /// </summary>
    public static class CompileBuildService
    {
        // Pinned to the exact versions upstream's BUILDING.md / run_with_tcc.bat call for. Self-contained
        // zips: TCC extracts to third_party/tcc/, SDL2 to third_party/SDL2-2.24.1/ (matching the .bat's paths).
        private const string TccUrl  = "https://github.com/FitzRoyX/tinycc/releases/download/tcc_20221020/tcc_20221020.zip";
        private const string Sdl2Url = "https://github.com/libsdl-org/SDL/releases/download/release-2.24.1/SDL2-devel-2.24.1-VC.zip";
        private const string Sdl2Dir = "SDL2-2.24.1"; // folder name the SDL2 zip unpacks to, and the .bat expects

        /// <summary>
        /// Compile snesrev/sm in <paramref name="installDir"/> with the user's ROM (already placed as
        /// <paramref name="romName"/>, e.g. "sm.smc"), producing <paramref name="exeName"/> (e.g. "sm.exe").
        /// Returns true if the exe exists (or was just built). Throws on a genuine build failure so the
        /// caller can surface a clear error. Downloads source + TCC + SDL2 self-contained — no user toolchain.
        /// </summary>
        public static void Compile(string installDir, string sourceArchiveUrl, string romName, string exeName, string gameName)
        {
            var exePath = Path.Combine(installDir, exeName);
            if (File.Exists(exePath)) return; // already built

            var rom = Path.Combine(installDir, romName);
            if (!File.Exists(rom))
                throw new FileNotFoundException($"ROM not found for compile: {rom} (provide the ROM first).");

            var thirdParty = Path.Combine(installDir, "third_party");
            Directory.CreateDirectory(thirdParty);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.Add("User-Agent", "yabo-launcher/1.0");

            // 1) Source archive — only if we don't already have the source tree (idempotent across retries).
            var srcDir = Path.Combine(installDir, "src");
            if (!Directory.Exists(srcDir) || !File.Exists(Path.Combine(srcDir, "main.c")))
            {
                Log.Info($"'{gameName}': tcc-compile — downloading source archive: {sourceArchiveUrl}");
                EnsureSourceTree(http, sourceArchiveUrl, installDir, gameName);
            }
            else
            {
                Log.Info($"'{gameName}': tcc-compile — source tree already present, skipping download.");
            }

            // 2) Toolchain — portable TCC + SDL2 dev libs into third_party (mirrors run_with_tcc.bat layout).
            var tccExe = Path.Combine(thirdParty, "tcc", "tcc.exe");
            if (!File.Exists(tccExe))
            {
                Log.Info($"'{gameName}': tcc-compile — downloading portable TCC: {TccUrl}");
                DownloadAndExtract(http, TccUrl, thirdParty, gameName, "TCC");
            }
            else
            {
                Log.Info($"'{gameName}': tcc-compile — TCC already present, skipping download.");
            }
            if (!File.Exists(tccExe))
                throw new FileNotFoundException($"TCC missing after download: {tccExe}");

            var sdlRoot = Path.Combine(thirdParty, Sdl2Dir);
            var sdlDll = Path.Combine(sdlRoot, "lib", "x64", "SDL2.dll");
            if (!File.Exists(sdlDll))
            {
                Log.Info($"'{gameName}': tcc-compile — downloading SDL2 dev libs: {Sdl2Url}");
                DownloadAndExtract(http, Sdl2Url, thirdParty, gameName, "SDL2");
            }
            else
            {
                Log.Info($"'{gameName}': tcc-compile — SDL2 already present, skipping download.");
            }
            if (!File.Exists(sdlDll))
                throw new FileNotFoundException($"SDL2 missing after download: {sdlDll}");

            // 4) Compile via TCC. This is the exact command run_with_tcc.bat issues (verified in
            //    dev\_research\sm-native\tcc-test). TCC expands the src/*.c globs itself on Windows.
            var sdlRel = $"third_party/{Sdl2Dir}";
            var args =
                $"-o{exeName} -DCOMPILER_TCC=1 -DSTBI_NO_SIMD=1 -DHAVE_STDINT_H=1 -D_HAVE_STDINT_H=1 " +
                $"-DSYSTEM_VOLUME_MIXER_AVAILABLE=0 -I{sdlRel}/include -L{sdlRel}/lib/x64 -lSDL2 -I. " +
                "src/*.c src/snes/*.c third_party/gl_core/gl_core_3_1.c";

            Log.Info($"'{gameName}': tcc-compile — starting TCC build in {installDir}");
            Log.Info($"'{gameName}': tcc-compile — {Path.GetFileName(tccExe)} {args}");

            var psi = new ProcessStartInfo
            {
                FileName = tccExe,
                Arguments = args,
                WorkingDirectory = installDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start TCC.");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            // TCC is noisy on stderr (e.g. an "M_PI redefined" warning) even on success — log, don't fail on it.
            if (!string.IsNullOrWhiteSpace(stdout)) Log.Info($"'{gameName}': tcc stdout: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr)) Log.Info($"'{gameName}': tcc stderr: {stderr.Trim()}");
            Log.Info($"'{gameName}': tcc-compile — TCC exited with code {proc.ExitCode}.");

            if (proc.ExitCode != 0 || !File.Exists(exePath))
                throw new InvalidOperationException(
                    $"TCC build failed (exit {proc.ExitCode}); {exeName} was not produced. See log for compiler output.");

            // The TCC build links SDL2 dynamically — copy SDL2.dll next to the exe so it can run.
            var dllTarget = Path.Combine(installDir, "SDL2.dll");
            if (!File.Exists(dllTarget))
            {
                File.Copy(sdlDll, dllTarget, overwrite: true);
                Log.Info($"'{gameName}': tcc-compile — copied SDL2.dll next to {exeName}.");
            }

            Log.Info($"'{gameName}': tcc-compile — produced {exeName} ({new FileInfo(exePath).Length} bytes).");
        }

        /// <summary>Download the repo source zip and lay its top-level contents into <paramref name="installDir"/>.
        /// GitHub source zips wrap everything in a single "&lt;repo&gt;-&lt;ref&gt;" folder — we flatten that.</summary>
        private static void EnsureSourceTree(HttpClient http, string url, string installDir, string gameName)
        {
            var tmpZip = Path.Combine(installDir, "_src.zip");
            var tmpExtract = Path.Combine(installDir, "_src_extract");
            try
            {
                using (var resp = http.GetStreamAsync(url).GetAwaiter().GetResult())
                using (var fs = File.Create(tmpZip))
                    resp.CopyTo(fs);
                Log.Info($"'{gameName}': tcc-compile — source archive downloaded ({new FileInfo(tmpZip).Length} bytes), extracting...");

                if (Directory.Exists(tmpExtract)) Directory.Delete(tmpExtract, true);
                ZipFile.ExtractToDirectory(tmpZip, tmpExtract);

                // Flatten the single wrapper folder (e.g. "sm-main/") into the install dir.
                var top = Directory.GetDirectories(tmpExtract);
                var root = top.Length == 1 ? top[0] : tmpExtract;
                CopyDirectory(root, installDir);
                Log.Info($"'{gameName}': tcc-compile — source tree laid into {installDir}.");
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
                try { if (Directory.Exists(tmpExtract)) Directory.Delete(tmpExtract, true); } catch { }
            }
        }

        /// <summary>Download a zip and extract it into <paramref name="destDir"/> (zip already carries its own
        /// top-level folder, e.g. tcc/ or SDL2-2.24.1/, so no flattening needed).</summary>
        private static void DownloadAndExtract(HttpClient http, string url, string destDir, string gameName, string label)
        {
            var tmpZip = Path.Combine(destDir, $"_{label}.zip");
            try
            {
                using (var resp = http.GetStreamAsync(url).GetAwaiter().GetResult())
                using (var fs = File.Create(tmpZip))
                    resp.CopyTo(fs);
                Log.Info($"'{gameName}': tcc-compile — {label} downloaded ({new FileInfo(tmpZip).Length} bytes), extracting...");
                ZipFile.ExtractToDirectory(tmpZip, destDir, overwriteFiles: true);
                Log.Info($"'{gameName}': tcc-compile — {label} extracted into {destDir}.");
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
