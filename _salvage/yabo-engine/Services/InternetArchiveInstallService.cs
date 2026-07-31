using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] In-app installer for Internet-Archive (archive.org) repacked ports — the
    /// feature-parity port of the RohanKar launcher's IA install (see RohanKar src/main/main.js).
    ///
    /// Flow (apps.json entries with <c>"ingest":"internet-archive"</c> + a direct <c>contentUrl</c>):
    ///   1. DOWNLOAD the archive (zip/7z/rar) over HTTP, streamed to a temp file with a User-Agent set.
    ///   2. EXTRACT it via SharpCompress (pure managed C#, no external 7-Zip/WinRAR dependency) into the
    ///      install dir — SharpCompress auto-detects zip/7z/rar from the stream.
    ///   3. UNBLOCK: recursively delete the <c>&lt;file&gt;:Zone.Identifier</c> NTFS alternate-data-stream
    ///      (Windows Mark-of-the-Web) from every extracted file — Rohan's <c>unblockDirectory</c>. This is
    ///      what stops SmartScreen from blocking the freshly-downloaded exes.
    ///   4. FIND-EXE: locate the launchable executable(s) with Rohan's <c>findExesInDir</c> walk
    ///      (_GAME_-prefixed collection subdirs → all their exes; else a <c>bin/</c> subdir; else .exe files
    ///      directly here; else recurse non-ignored subdirs to the first level with exes; MAX_DEPTH 5).
    ///
    /// This is ADDITIVE: it does not touch the GitHub-release download/extract/launch path. IA repacks are
    /// portable, so <see cref="Uninstall"/> is simply "delete the install folder".
    /// </summary>
    public static class InternetArchiveInstallService
    {
        private const int MaxDepth = 5;

        // Rohan IGNORED_SUBDIRS (main.js ~line 584): folders skipped while recursing for the launch exe.
        private static readonly HashSet<string> IgnoredSubdirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "extras", "extra", "bonus", "soundtrack", "manuals", "manual"
        };

        /// <summary>
        /// Download <paramref name="contentUrl"/>, extract it into <paramref name="installDir"/>, strip the
        /// Mark-of-the-Web from every file, and return the launchable exe path(s) (Rohan find-exe order).
        /// Throws on download/extract failure so the caller can surface it.
        /// </summary>
        public static async Task<List<string>> InstallAsync(
            HttpClient httpClient,
            string contentUrl,
            string installDir,
            string? gameName = null,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(contentUrl))
                throw new ArgumentException("contentUrl is required for an internet-archive install.", nameof(contentUrl));
            if (string.IsNullOrWhiteSpace(installDir))
                throw new ArgumentException("installDir is required.", nameof(installDir));

            var label = gameName ?? "IA port";
            Directory.CreateDirectory(installDir);

            var tempArchive = Path.Combine(Path.GetTempPath(), "yabo-ia-" + Guid.NewGuid().ToString("N") + GuessExtension(contentUrl));
            try
            {
                Log.Info($"IA install '{label}': downloading {contentUrl}");
                await DownloadToFileAsync(httpClient, contentUrl, tempArchive, progress, cancellationToken).ConfigureAwait(false);

                // [dry-run on collide] Peek at the archive's file LIST before extracting: a repack that already
                // contains a launchable game .exe is SELF-CONTAINED (extract only — no GitHub binary needed); a
                // data-only archive is the case a collision must marry a GitHub binary onto. We log the verdict so
                // the delivery route is visible/diagnosable (and so a "0 exes" result is distinguishable from a
                // botched extract vs a genuinely binary-less repack).
                try
                {
                    var entries = ListArchiveEntries(tempArchive);
                    bool hasGameExe = entries.Any(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                       && !IsInstallerExe(e) && !IsUninstaller(e));
                    Log.Info($"IA install '{label}': dry-run — {entries.Count} archive entries; delivery = "
                             + (hasGameExe ? "SELF-CONTAINED (game .exe present in repack)"
                                           : "DATA-ONLY (no game .exe in repack — a collision would marry a GitHub binary)"));
                }
                catch { /* dry-run is diagnostic only — never block the install on it */ }

                Log.Info($"IA install '{label}': extracting {Path.GetFileName(tempArchive)} -> {installDir}");
                ExtractArchive(tempArchive, installDir);

                var unblocked = UnblockDirectory(installDir);
                Log.Info($"IA install '{label}': unblocked (Zone.Identifier removed) from {unblocked} files in {installDir}");

                var exes = FindExesInDir(installDir, 0);
                Log.Info($"IA install '{label}': found {exes.Count} launchable exe(s)"
                         + (exes.Count > 0 ? $" (first: {Path.GetFileName(exes[0])})" : ""));
                // [GameVault-style installer handling] If the repack is an INSTALLER (Inno/NSIS/InstallShield setup.exe)
                // or an ISO with no directly-runnable game, run the setup ONCE into the install dir, then re-scan for the
                // real game exe — so installer-repacks (e.g. Sega Rally Revo) become playable instead of dead-ending on
                // a setup.exe. Portable repacks (a real game exe already present) are untouched.
                exes = await RunInstallerIfNeededAsync(installDir, exes, label, cancellationToken).ConfigureAwait(false);
                return exes;
            }
            finally
            {
                try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { /* best-effort temp cleanup */ }
                // [resumable downloads] These archives use a unique per-call temp name (no cross-run resume), so any
                // leftover checkpoint sidecar is an orphan — clean it up too.
                try { var ck = tempArchive + CheckpointSuffix; if (File.Exists(ck)) File.Delete(ck); } catch { /* best-effort */ }
            }
        }

        // ── GameVault-style installer / ISO handling ───────────────────────────────────────────────────
        // GameVault classifies repacks WINDOWS_PORTABLE vs WINDOWS_SETUP and runs setup.exe for setup-type games.
        // We do the same here: if an extracted repack has NO directly-runnable game exe (only a setup.exe / an ISO),
        // run the installer ONCE into the install dir, then re-scan for the real game exe. Portable repacks (a real
        // game exe already present) are left untouched. We only auto-run installers we can identify (Inno/NSIS/IShield)
        // so we never pop an interactive installer window unattended.
        private enum InstallerKind { None, Inno, Nsis, InstallShield }

        private static bool IsUninstaller(string path)
        {
            var n = Path.GetFileName(path).ToLowerInvariant();
            return n.StartsWith("unins") || n.Contains("uninstall");
        }

        private static bool IsInstallerExe(string path)
        {
            if (IsUninstaller(path)) return false;
            var n = Path.GetFileName(path).ToLowerInvariant();
            return n == "setup.exe" || n.Contains("setup") || n.StartsWith("install") || n.Contains("installer");
        }

        private static InstallerKind DetectInstallerType(string exePath)
        {
            try
            {
                using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                long toRead = Math.Min(12L * 1024 * 1024, fs.Length);
                var buf = new byte[toRead];
                int read = 0; while (read < toRead) { int r = fs.Read(buf, read, (int)(toRead - read)); if (r <= 0) break; read += r; }
                var text = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                if (text.Contains("Inno Setup") || text.Contains("JR.Inno.Setup")) return InstallerKind.Inno;
                if (text.Contains("Nullsoft") || text.Contains("NSIS")) return InstallerKind.Nsis;
                if (text.Contains("InstallShield")) return InstallerKind.InstallShield;
            }
            catch { /* unreadable → unknown */ }
            return InstallerKind.None;
        }

        private static async Task RunSetupAsync(string installerExe, string targetDir, string label, CancellationToken ct)
        {
            var kind = DetectInstallerType(installerExe);
            string? args = kind switch
            {
                InstallerKind.Inno => $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART \"/DIR={targetDir}\"",
                InstallerKind.Nsis => $"/S /D={targetDir}",   // NSIS: /D must be the LAST arg and unquoted
                InstallerKind.InstallShield => "/s",
                _ => null
            };
            if (args == null)
            {
                Log.Warn($"IA install '{label}': installer '{Path.GetFileName(installerExe)}' toolkit not recognized — skipping silent auto-run (run it manually).");
                return;
            }
            Log.Info($"IA install '{label}': silent-installing via {kind} -> {targetDir}");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerExe, Arguments = args, UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerExe) ?? targetDir,
            };
            var p = System.Diagnostics.Process.Start(psi);
            if (p != null) await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string outp = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                return outp.Trim();
            }
            catch { return string.Empty; }
        }

        private static async Task RunIsoSetupAsync(string isoPath, string targetDir, string label, CancellationToken ct)
        {
            var drive = (await RunPowerShellAsync($"(Mount-DiskImage -ImagePath '{isoPath}' -PassThru | Get-Volume).DriveLetter", ct).ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(drive)) { Log.Warn($"IA install '{label}': couldn't mount ISO {Path.GetFileName(isoPath)}."); return; }
            var root = drive + ":\\";
            Log.Info($"IA install '{label}': mounted ISO at {root}");
            try
            {
                var setup = Directory.EnumerateFiles(root, "setup.exe", SearchOption.AllDirectories).FirstOrDefault()
                            ?? Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).FirstOrDefault(IsInstallerExe);
                if (setup != null) await RunSetupAsync(setup, targetDir, label, ct).ConfigureAwait(false);
                else Log.Warn($"IA install '{label}': no setup.exe on the mounted ISO.");
            }
            finally
            {
                await RunPowerShellAsync($"Dismount-DiskImage -ImagePath '{isoPath}'", ct).ConfigureAwait(false);
            }
        }

        private static async Task<List<string>> RunInstallerIfNeededAsync(string installDir, List<string> exes, string label, CancellationToken ct)
        {
            var gameExes = exes.Where(e => !IsInstallerExe(e) && !IsUninstaller(e)).ToList();
            if (gameExes.Count > 0) return exes;   // portable repack — already playable

            var installer = exes.FirstOrDefault(IsInstallerExe);
            var iso = Directory.EnumerateFiles(installDir, "*.iso", SearchOption.AllDirectories)
                        .Concat(Directory.EnumerateFiles(installDir, "*.mdf", SearchOption.AllDirectories)).FirstOrDefault();
            if (installer == null && iso == null) return exes;   // data-only / nothing to run

            try
            {
                if (installer != null) await RunSetupAsync(installer, installDir, label, ct).ConfigureAwait(false);
                else await RunIsoSetupAsync(iso!, installDir, label, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Error($"IA install '{label}': installer/ISO run failed", ex); }

            UnblockDirectory(installDir);
            var after = FindExesInDir(installDir, 0).Where(e => !IsInstallerExe(e) && !IsUninstaller(e)).ToList();
            Log.Info($"IA install '{label}': after setup, {after.Count} game exe(s)" + (after.Count > 0 ? $" (first: {Path.GetFileName(after[0])})" : " — may need a manual setup run"));
            return after.Count > 0 ? after : exes;
        }

        /// <summary>
        /// [yabo-launcher fork] Download an archive and extract ONLY the named files (matched by FILENAME at any
        /// depth) into <paramref name="destDir"/> — used by the collision "marriage" so an N64-hybrid repack drops
        /// just its ROM next to the binary instead of dumping the whole portable installer (keeps installs clean).
        /// Returns the filenames extracted. Throws on download failure.
        /// </summary>
        // Console-ROM extensions we treat as "the game image" for the extension-fallback in ExtractMembersAsync.
        // Kept tight (no .bin) so we never mis-grab an engine data blob — only true cart images.
        private static readonly HashSet<string> RomExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".z64", ".n64", ".v64", ".ndd", ".rom" };

        public static async Task<List<string>> ExtractMembersAsync(
            HttpClient httpClient,
            string contentUrl,
            IEnumerable<string> wantedFileNames,
            string destDir,
            string? gameName = null,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var wanted = new HashSet<string>(
                wantedFileNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            var extracted = new List<string>();
            if (wanted.Count == 0 || string.IsNullOrWhiteSpace(contentUrl) || string.IsNullOrWhiteSpace(destDir))
                return extracted;

            var label = gameName ?? "IA collision";
            Directory.CreateDirectory(destDir);
            var tempArchive = Path.Combine(Path.GetTempPath(), "yabo-ia-" + Guid.NewGuid().ToString("N") + GuessExtension(contentUrl));
            try
            {
                Log.Info($"Marriage '{label}': downloading archive {contentUrl} to extract {wanted.Count} member(s): {string.Join(", ", wanted)}");
                await DownloadToFileAsync(httpClient, contentUrl, tempArchive, progress, cancellationToken).ConfigureAwait(false);

                using var archive = ArchiveFactory.OpenArchive(tempArchive, new SharpCompress.Readers.ReaderOptions());
                // Pass 1 — exact basename match (Diablo's DIABDAT.MPQ, hellfire.mpq, …).
                var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key)) continue;
                    var name = Path.GetFileName(entry.Key.Replace('\\', '/'));
                    if (string.IsNullOrEmpty(name) || !wanted.Contains(name)) continue;

                    var destPath = Path.Combine(destDir, name);
                    entry.WriteToFile(destPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true, PreserveFileTime = true });
                    try { File.Delete(destPath + ":Zone.Identifier"); } catch { /* MOTW absent — fine */ }
                    long kb = 0; try { kb = new FileInfo(destPath).Length / 1024; } catch { }
                    extracted.Add(name);
                    matched.Add(name);
                    Log.Info($"Marriage '{label}': extracted '{name}' -> {destPath} ({kb} KB).");
                }

                // Pass 2 — ROM-extension fallback. An N64Recomp/port repack carries the ROM under whatever name the
                // packer chose (e.g. the declared 'bm64_us.z64' / 'pd.ntsc-final.z64' may be the packer's own naming,
                // not what's inside). For any still-missing wanted file with a ROM extension, grab the single biggest
                // archive entry sharing that extension and write it under the DECLARED name (what the engine expects).
                // This is what makes the recomp marriages robust regardless of internal naming — the owner's "the fix
                // should be on all the recomps".
                foreach (var w in wanted)
                {
                    if (matched.Contains(w)) continue;
                    var wExt = Path.GetExtension(w);
                    if (string.IsNullOrEmpty(wExt) || !RomExtensions.Contains(wExt.ToLowerInvariant())) continue;

                    var pick = archive.Entries
                        .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.Key)
                                    && string.Equals(Path.GetExtension(e.Key), wExt, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(e => { try { return e.Size; } catch { return 0L; } })
                        .FirstOrDefault();
                    if (pick == null) continue;

                    var destPath = Path.Combine(destDir, w);
                    pick.WriteToFile(destPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true, PreserveFileTime = true });
                    try { File.Delete(destPath + ":Zone.Identifier"); } catch { /* MOTW absent — fine */ }
                    long kb = 0; try { kb = new FileInfo(destPath).Length / 1024; } catch { }
                    extracted.Add(w);
                    matched.Add(w);
                    Log.Info($"Marriage '{label}': ROM-extension fallback matched '{Path.GetFileName(pick.Key)}' -> '{w}' ({kb} KB).");
                }

                if (extracted.Count < wanted.Count)
                    Log.Warn($"Marriage '{label}': archive did not contain {wanted.Count - extracted.Count} of the wanted file(s).");
            }
            finally
            {
                try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { /* best-effort */ }
            }
            return extracted;
        }

        /// <summary>
        /// Uninstall an IA repack: delete the whole install folder (these are portable — no registry/shared
        /// state). Best-effort; swallows errors. Returns true if the folder no longer exists afterward.
        /// </summary>
        public static bool Uninstall(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                return true;

            try
            {
                Directory.Delete(installDir, recursive: true);
                Log.Info($"IA uninstall: deleted {installDir}");
                return !Directory.Exists(installDir);
            }
            catch (Exception ex)
            {
                Log.Error($"IA uninstall: failed to delete {installDir}", ex);
                return false;
            }
        }

        // ── download ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>[yabo marriage — pattern 1] Download a RAW content file (not an archive) straight to destPath,
        /// e.g. Sonic Mania's Data.rsdk that an IA item serves directly. Streams with progress; no extraction.</summary>
        public static async Task DownloadFileAsync(
            HttpClient httpClient, string url, string destPath,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var tmp = destPath + ".part";
            await DownloadToFileAsync(httpClient, url, tmp, progress, cancellationToken).ConfigureAwait(false);
            try { File.Delete(destPath + ":Zone.Identifier"); } catch { /* MOTW absent — fine */ }
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }

        /// <summary>
        /// [resumable downloads — shared helper] Download <paramref name="url"/> to <paramref name="destPath"/> using
        /// the SAME resumable downloader the IA path uses (HTTP Range + a <c>&lt;dest&gt;.ck</c> "bytesSoFar;totalSize"
        /// checkpoint + periodic flush). Streams straight to <paramref name="destPath"/> (no <c>.part</c> rename) so a
        /// cancelled/crashed download leaves the partial + checkpoint for the next call to resume. This is the single
        /// resumable entry point the GitHub-release download in GameInfo routes through, so both the GitHub-binary and
        /// the Internet-Archive paths share one resumable implementation. Sends a User-Agent and honours cancellation.
        /// </summary>
        public static Task DownloadResumableAsync(
            HttpClient httpClient, string url, string destPath,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url is required.", nameof(url));
            if (string.IsNullOrWhiteSpace(destPath)) throw new ArgumentException("destPath is required.", nameof(destPath));
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return DownloadToFileAsync(httpClient, url, destPath, progress, cancellationToken);
        }

        /// <summary>Archive extensions the marriage knows how to extract. A contentUrl ending in anything else is a
        /// raw single content file → download it directly (pattern 1), don't try to open it as an archive.</summary>
        public static bool LooksLikeArchive(string url)
        {
            try
            {
                var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
                return ext is ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".zipx";
            }
            catch { return true; } // unknown → assume archive (existing behavior)
        }

        // [resumable downloads — ported from GameVault HttpClientDownloadWithProgress]
        // Sidecar checkpoint file written next to the partial download. Holds "bytesSoFar;totalSize" so an
        // interrupted download (cancel / crash / network drop) can resume instead of restarting from 0. GameVault
        // persists the exact same "{position};{size}" string to its `gamevault-metadata` file (see
        // HttpClientDownloadWithProgress.ProcessContentStream); we keep it per-destination as `<dest>.ck`.
        private const string CheckpointSuffix = ".ck";
        // Flush the checkpoint at most this often — GameVault uses a 2s wall-clock interval; we do the same.
        private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(2);

        private static async Task DownloadToFileAsync(
            HttpClient httpClient,
            string url,
            string destPath,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var checkpointPath = destPath + CheckpointSuffix;

            // [resumable downloads — ported from GameVault HttpClientDownloadWithProgress: InitResume]
            // If a partial file AND a valid checkpoint already exist for this destination, resume from the saved
            // byte offset by sending `Range: bytes=<offset>-` and appending to the partial. The checkpoint stores
            // "bytesSoFar;totalSize"; we only trust the offset when the partial on disk is at least that long
            // (a shorter/missing partial means a torn write → restart clean).
            long resumeFrom = 0;
            long? checkpointTotal = null;
            try
            {
                if (File.Exists(destPath) && File.Exists(checkpointPath))
                {
                    var parts = (await File.ReadAllTextAsync(checkpointPath, cancellationToken).ConfigureAwait(false))
                        .Split(';');
                    if (parts.Length >= 1 && long.TryParse(parts[0], out var savedPos) && savedPos > 0)
                    {
                        var onDisk = new FileInfo(destPath).Length;
                        if (onDisk >= savedPos)
                        {
                            resumeFrom = savedPos;
                            if (parts.Length >= 2 && long.TryParse(parts[1], out var savedTotal) && savedTotal > 0)
                                checkpointTotal = savedTotal;
                        }
                        else
                        {
                            Log.Warn($"IA download: checkpoint says {savedPos} bytes but partial is only {onDisk} — restarting {Path.GetFileName(destPath)} from 0.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"IA download: could not read checkpoint for {Path.GetFileName(destPath)} ({ex.Message}) — starting from 0."); resumeFrom = 0; }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // archive.org is lenient, but some endpoints/CDNs 403 an empty UA — set one (Rohan sends a UA too).
            if (!request.Headers.Contains("User-Agent"))
                request.Headers.TryAddWithoutValidation("User-Agent", "yabo-launcher/1.0 (+https://github.com)");
            // [resumable downloads] Ask the server to continue from where we left off.
            if (resumeFrom > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
                Log.Info($"IA download: resuming {Path.GetFileName(destPath)} from byte {resumeFrom} (Range request).");
            }

            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // [resumable downloads — Range honored?] A 206 Partial Content means the server is continuing from our
            // offset (append to the partial). Anything else (typically 200 OK) means it ignored Range and is sending
            // the WHOLE file from 0 — fall back to a clean restart (truncate) so we don't corrupt the file by appending
            // a fresh full body onto an existing partial. 416 (Range Not Satisfiable) can mean the partial is already
            // complete or the server dislikes the range — also restart clean.
            bool resuming = resumeFrom > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (resumeFrom > 0 && !resuming)
            {
                Log.Warn($"IA download: server did not honor Range (status {(int)response.StatusCode}) for {Path.GetFileName(destPath)} — restarting from 0.");
                resumeFrom = 0;
            }
            response.EnsureSuccessStatusCode();

            // Determine the total file size for progress: on a 206 the Content-Length is just the REMAINING bytes, so
            // prefer the Content-Range "*/<total>" (or the checkpoint's saved total); on a fresh 200 use Content-Length.
            long? total;
            if (resuming)
            {
                total = response.Content.Headers.ContentRange?.Length
                        ?? checkpointTotal
                        ?? (response.Content.Headers.ContentLength is long rem ? resumeFrom + rem : (long?)null);
            }
            else
            {
                total = response.Content.Headers.ContentLength;
            }

            await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            // [resumable downloads] Append when resuming (FileMode.Append seeks to end), else create/truncate.
            await using var fileStream = new FileStream(
                destPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long writtenTotal = resuming ? resumeFrom : 0;   // running byte count INCLUDING the resumed prefix
            var lastCheckpoint = DateTime.UtcNow;
            int read;
            try
            {
                while ((read = await httpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    writtenTotal += read;

                    if (progress != null && total is > 0)
                        progress.Report(Math.Clamp((double)writtenTotal / total.Value * 100.0, 0, 100));

                    // [resumable downloads — ported from GameVault: 2s checkpoint] Persist "bytesSoFar;totalSize"
                    // periodically so a crash/cancel mid-stream can resume. We only checkpoint when we KNOW the total
                    // (otherwise resume offset is fine but progress math isn't) — total is written best-effort.
                    if (total is > 0 && (DateTime.UtcNow - lastCheckpoint) >= CheckpointInterval)
                    {
                        await WriteCheckpointAsync(checkpointPath, fileStream, writtenTotal, total.Value).ConfigureAwait(false);
                        lastCheckpoint = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // [resumable downloads] On cancel, LEAVE the partial + a fresh checkpoint so the next call resumes,
                // rather than discarding the bytes (the old behavior restarted a cancelled 2GB download from zero).
                if (total is > 0)
                {
                    try { await WriteCheckpointAsync(checkpointPath, fileStream, writtenTotal, total.Value).ConfigureAwait(false); }
                    catch { /* best-effort — resume can still fall back to a clean restart */ }
                }
                throw;
            }
            catch (Exception ex)
            {
                // On any other failure, also try to checkpoint so a retry resumes; then propagate.
                if (total is > 0)
                {
                    try { await WriteCheckpointAsync(checkpointPath, fileStream, writtenTotal, total.Value).ConfigureAwait(false); }
                    catch { /* best-effort */ }
                }
                Log.Warn($"IA download: stream failed for {Path.GetFileName(destPath)} at {writtenTotal} bytes ({ex.Message}) — checkpoint kept for resume.");
                throw;
            }

            // [resumable downloads] Success → the file is complete; drop the checkpoint sidecar. The partial file is
            // left at destPath exactly as before (callers handle any .part → final rename themselves).
            try { if (File.Exists(checkpointPath)) File.Delete(checkpointPath); }
            catch { /* a leftover checkpoint is harmless — next run sees the full partial and re-validates */ }
        }

        // [resumable downloads — ported from GameVault: Preferences.Set(DownloadProgress, "{position};{size}")]
        // Flush the file buffer to disk THEN write the checkpoint, so the checkpoint offset is never ahead of the
        // bytes actually persisted (otherwise a crash could leave the checkpoint claiming bytes that weren't flushed,
        // and the resume would append into a gap). Written via a temp+replace to keep the sidecar non-torn.
        private static async Task WriteCheckpointAsync(string checkpointPath, FileStream fileStream, long bytesSoFar, long totalSize)
        {
            await fileStream.FlushAsync().ConfigureAwait(false);
            var tmp = checkpointPath + ".tmp";
            await File.WriteAllTextAsync(tmp, $"{bytesSoFar};{totalSize}").ConfigureAwait(false);
            // Move is atomic-ish on NTFS; overwrite any existing checkpoint.
            if (File.Exists(checkpointPath)) File.Delete(checkpointPath);
            File.Move(tmp, checkpointPath);
        }

        private static string GuessExtension(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath;
                var ext = Path.GetExtension(path);
                // SharpCompress sniffs the format from content, so the temp extension is cosmetic; keep a known
                // archive extension when present so logs/diagnostics read sensibly.
                if (!string.IsNullOrEmpty(ext) && ext.Length <= 5)
                    return ext;
            }
            catch { /* fall through */ }
            return ".bin";
        }

        // ── dry-run listing ────────────────────────────────────────────────────────────────────────────

        /// <summary>DRY RUN: list entry paths (forward-slash, dirs excluded) inside a downloaded archive WITHOUT
        /// extracting. 7z/rar via real 7-Zip (`7z l`), zip via SharpCompress. Lets a collision decide
        /// self-contained-vs-marry-GitHub by peeking at the file paths before committing the install route.</summary>
        public static List<string> ListArchiveEntries(string archivePath)
        {
            if (SevenZipCli.ShouldUseFor(archivePath)) return SevenZipCli.ListEntries(archivePath);
            var list = new List<string>();
            try
            {
                using var archive = ArchiveFactory.OpenArchive(archivePath, new SharpCompress.Readers.ReaderOptions());
                foreach (var e in archive.Entries)
                    if (!e.IsDirectory && !string.IsNullOrWhiteSpace(e.Key))
                        list.Add(e.Key!.Replace('\\', '/'));
            }
            catch (Exception ex) { Log.Warn($"archive list failed for {Path.GetFileName(archivePath)}: {ex.Message}"); }
            return list;
        }

        // ── extract (real 7-Zip for 7z/rar; SharpCompress for zip) ─────────────────────────────────────

        private static void ExtractArchive(string archivePath, string destDir)
        {
            Directory.CreateDirectory(destDir);

            // [.7z executables fix] SharpCompress silently DROPS BCJ2-filtered / solid 7z entries — exactly the
            // filter 7-Zip puts on .exe/.dll — so a SharpCompress .7z extract yields a "0 exes" data-only folder
            // (OutRun 2006's OR2006C2C.exe + dinput8.dll were the smoking gun). For 7z/rar, use a real 7z.exe when
            // present; only fall back to SharpCompress (with a loud warning) when no 7-Zip is available.
            if (SevenZipCli.ShouldUseFor(archivePath))
            {
                if (SevenZipCli.Extract(archivePath, destDir)) return;
                Log.Warn($"IA install: 7-Zip extract failed for {Path.GetFileName(archivePath)} — falling back to SharpCompress (executables may be dropped).");
            }
            else if (Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"IA install: no 7z.exe available for {Path.GetFileName(archivePath)} — SharpCompress will likely DROP .exe/.dll entries (BCJ2). Bundle 7z\\7z.exe beside the engine.");
            }

            var fullDestRoot = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            using var archive = ArchiveFactory.OpenArchive(archivePath, new SharpCompress.Readers.ReaderOptions());
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    continue;

                var key = entry.Key;
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                // Path-traversal guard: never let an entry escape the install dir (mirrors the core
                // extractor's GetSafeExtractionPath check).
                var sanitized = key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var destPath = Path.GetFullPath(Path.Combine(fullDestRoot, sanitized));
                if (!destPath.StartsWith(fullDestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !destPath.Equals(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"IA install: skipping archive entry that escapes the install dir: {key}");
                    continue;
                }

                var parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                entry.WriteToFile(destPath, new ExtractionOptions
                {
                    ExtractFullPath = false, // we already computed the safe full path above
                    Overwrite = true,
                    PreserveFileTime = true,
                });
            }
        }

        // ── unblock (delete <file>:Zone.Identifier NTFS ADS) — Rohan unblockDirectory ───────────────────

        private static int UnblockDirectory(string dir)
        {
            // Mark-of-the-Web is a Windows/NTFS concept; no-op elsewhere (matches Rohan's win32 guard).
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !Directory.Exists(dir))
                return 0;

            int count = 0;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
            catch { return 0; }

            foreach (var file in files)
            {
                try
                {
                    // Deleting the alternate data stream is exactly what right-click → Unblock / Unblock-File
                    // does. If the stream doesn't exist (most files), File.Delete throws — swallow it.
                    File.Delete(file + ":Zone.Identifier");
                    count++;
                }
                catch { /* stream absent or already gone — fine */ }
            }
            return count;
        }

        // ── find-exe (Rohan findExesInDir) ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Locate the launchable exe(s), replicating Rohan's <c>findExesInDir</c>:
        ///   _GAME_-prefixed subdirs ⇒ a collection (return ALL their exes); else a <c>bin/</c> subdir's exes;
        ///   else .exe files directly in this dir; else recurse non-ignored subdirs and return the FIRST level
        ///   that has any exes. Depth-limited to <see cref="MaxDepth"/>.
        /// </summary>
        public static List<string> FindExesInDir(string installDir, int depth)
        {
            if (depth > MaxDepth || !Directory.Exists(installDir))
                return new List<string>();

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(installDir); }
            catch { return new List<string>(); }

            // Collection detection: _GAME_-prefixed subfolders ⇒ return ALL their exes.
            var gameFolders = subdirs
                .Where(d => Path.GetFileName(d).StartsWith("_GAME_", StringComparison.Ordinal))
                .ToList();
            if (gameFolders.Count > 0)
            {
                var allExes = new List<string>();
                foreach (var gf in gameFolders)
                    allExes.AddRange(FindExesInDir(gf, depth + 1));
                return allExes;
            }

            // A 'bin' subfolder first (common pattern).
            var binDir = subdirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), "bin", StringComparison.OrdinalIgnoreCase));
            if (binDir != null)
            {
                var binExes = ExesInDir(binDir);
                if (binExes.Count > 0)
                    return binExes;
            }

            // .exe files directly in this folder.
            var localExes = ExesInDir(installDir);
            if (localExes.Count > 0)
                return localExes;

            // Recurse into non-ignored subdirs; return the first level that has exes.
            foreach (var sub in subdirs)
            {
                if (IgnoredSubdirs.Contains(Path.GetFileName(sub)))
                    continue;
                var found = FindExesInDir(sub, depth + 1);
                if (found.Count > 0)
                    return found;
            }

            return new List<string>();
        }

        private static List<string> ExesInDir(string dir)
        {
            try
            {
                return Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// From a set of candidate exes, choose the one to launch: a single candidate auto-launches; with
        /// multiple, pick the LARGEST (Rohan's heuristic — the game exe dwarfs helper/uninstaller stubs).
        /// Returns null when there are no candidates.
        /// </summary>
        public static string? ChooseLaunchExe(IReadOnlyList<string> exes)
        {
            if (exes == null || exes.Count == 0)
                return null;
            if (exes.Count == 1)
                return exes[0];

            string? best = null;
            long bestSize = -1;
            foreach (var exe in exes)
            {
                long size;
                try { size = new FileInfo(exe).Length; }
                catch { size = 0; }
                if (size > bestSize)
                {
                    bestSize = size;
                    best = exe;
                }
            }
            return best;
        }

        /// <summary>
        /// Launch an installed IA exe via ShellExecute (UseShellExecute=true) — matches Rohan's
        /// <c>shell.openPath</c>, which routes through Windows ShellExecute (handles UAC/elevation that a
        /// raw process spawn would fail on). Working directory is the exe's folder.
        /// </summary>
        public static Process? LaunchExe(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                throw new FileNotFoundException("Executable not found.", exePath);

            return Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
            });
        }
    }
}
