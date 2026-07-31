using System.Diagnostics;
using System.IO;
using System.Text;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] OPTIONAL Steam Rich Presence "puppet" using the Steam build of RetroArch.
    ///
    /// HOW IT WORKS — RetroArch's Steam integration sets Steam Rich Presence from the loaded content's
    /// filename BASENAME (see RetroArch steam/steam.c, which formats presence per the
    /// <c>steam_rich_presence_running_format</c> enum). So if we launch RetroArch with a dummy content
    /// file named "&lt;game&gt;.zip" and a no-op-ish core, Steam shows "Playing &lt;game&gt;" while our REAL
    /// standalone game runs in its own window. We set <c>steam_rich_presence_format</c> to CONTENT-only so
    /// only the basename shows (no core/system suffix).
    ///
    /// CONTENT format index: from RetroArch steam/steam.h
    /// <c>enum steam_rich_presence_running_format</c>:
    ///   NONE=0, CONTENT=1, CORE=2, SYSTEM=3, CONTENT_SYSTEM=4, CONTENT_CORE=5, CONTENT_SYSTEM_CORE=6, LAST=7.
    /// We want CONTENT only → "1".
    ///
    /// This is entirely opt-in (AppSettings.SteamPresenceMode == "retroarch"). It reuses
    /// <see cref="SteamContentLocator"/> for discovery — NO hand-rolled Steam path logic here.
    /// </summary>
    public static class RetroArchPresenceService
    {
        /// <summary>Steam appid for RetroArch.</summary>
        public const int AppIdRetroArch = 1118310;

        /// <summary>The CONTENT-only value of RetroArch's steam_rich_presence_running_format enum (CONTENT=1).</summary>
        public const string ContentOnlyFormat = "1";

        /// <summary>
        /// Known-good fallback install path on this machine (used ONLY if Steam discovery fails). The
        /// canonical resolution goes through <see cref="SteamContentLocator.FindAppInstallDir"/>.
        /// </summary>
        public const string FallbackRetroArchExe =
            @"C:\Program Files (x86)\Steam\steamapps\common\RetroArch\retroarch.exe";

        // [yabo-launcher fork] No dedicated "null"/dummy core ships with the Steam RetroArch core set on this
        // machine (the cores\ folder is all real emulator cores: fbneo, mesen, genesis_plus_gx, mgba, …).
        // We pick FB Neo as the puppet core: it is content-driven, loads an arbitrary zip path, and does not
        // require a system BIOS to merely start a session — which is all we need for RetroArch to push the
        // content basename to Steam Rich Presence. The puppet game never has to actually emulate anything.
        //
        // OWNER MUST VERIFY/TUNE LIVE: confirm that launching RetroArch with this core + the dummy zip
        // actually registers Steam presence (watch the Steam friends list show "Playing <game>"). If FB Neo
        // refuses to start a session on a bogus zip, swap CandidateCoreNames order (try "mesen_libretro" /
        // "genesis_plus_gx_libretro") or fetch a true null core. The dummy file is intentionally an empty
        // ".zip" so no core can mistake it for a real ROM and crash.
        private static readonly string[] CandidateCoreNames =
        {
            "fbneo_libretro.dll",
            "mesen_libretro.dll",
            "genesis_plus_gx_libretro.dll",
            "mgba_libretro.dll",
        };

        /// <summary>
        /// Locate retroarch.exe via <see cref="SteamContentLocator.FindAppInstallDir"/> (appid 1118310),
        /// falling back to the known install path. Returns null if neither resolves to an existing file.
        /// </summary>
        public static string? LocateRetroArch()
        {
            // REUSE: SteamContentLocator.FindAppInstallDir does registry → libraryfolders.vdf → appmanifest.
            var installDir = SteamContentLocator.FindAppInstallDir(AppIdRetroArch);
            if (!string.IsNullOrEmpty(installDir))
            {
                var exe = Path.Combine(installDir, "retroarch.exe");
                if (File.Exists(exe))
                {
                    Log.Info($"[RetroArchPresence] located via SteamContentLocator: {exe}");
                    return exe;
                }
                Log.Warn($"[RetroArchPresence] SteamContentLocator found install '{installDir}' but no retroarch.exe inside.");
            }

            if (File.Exists(FallbackRetroArchExe))
            {
                Log.Info($"[RetroArchPresence] using fallback path: {FallbackRetroArchExe}");
                return FallbackRetroArchExe;
            }

            Log.Warn("[RetroArchPresence] RetroArch not found (Steam appid 1118310 not installed and no fallback exe).");
            return null;
        }

        /// <summary>The retroarch.cfg next to a resolved retroarch.exe (or null).</summary>
        public static string? GetConfigPath(string retroArchExe)
        {
            var dir = Path.GetDirectoryName(retroArchExe);
            if (string.IsNullOrEmpty(dir)) return null;
            var cfg = Path.Combine(dir, "retroarch.cfg");
            return File.Exists(cfg) ? cfg : null;
        }

        /// <summary>
        /// Ensure RetroArch's retroarch.cfg has Steam rich presence enabled + set to CONTENT-only.
        /// Backs the file up first (one-time, to retroarch.cfg.yabo-bak) and rewrites ONLY the two keys
        /// (<c>steam_rich_presence_enable</c>, <c>steam_rich_presence_format</c>) in place, preserving every
        /// other line. Missing keys are appended. Returns true on success.
        /// </summary>
        /// <param name="cfgPath">Path to retroarch.cfg to edit.</param>
        /// <param name="makeBackup">When true, copy cfg → cfg.yabo-bak once before editing.</param>
        public static bool EnsurePresenceConfig(string cfgPath, bool makeBackup = true)
        {
            if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath))
            {
                Log.Warn($"[RetroArchPresence] EnsurePresenceConfig: cfg not found: {cfgPath}");
                return false;
            }

            try
            {
                if (makeBackup)
                {
                    var bak = cfgPath + ".yabo-bak";
                    if (!File.Exists(bak))
                    {
                        File.Copy(cfgPath, bak);
                        Log.Info($"[RetroArchPresence] backed up cfg → {bak}");
                    }
                }

                var lines = File.ReadAllLines(cfgPath).ToList();
                SetOrAddKey(lines, "steam_rich_presence_enable", "true");
                SetOrAddKey(lines, "steam_rich_presence_format", ContentOnlyFormat);

                // Preserve the file's existing newline style by joining with the platform newline; RetroArch
                // tolerates either. We write all lines back, including any we appended.
                File.WriteAllText(cfgPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                Log.Info($"[RetroArchPresence] cfg updated: steam_rich_presence_enable=\"true\", steam_rich_presence_format=\"{ContentOnlyFormat}\" (CONTENT).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[RetroArchPresence] EnsurePresenceConfig failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set <paramref name="key"/> to <c>key = "value"</c> in <paramref name="lines"/> (matching the
        /// RetroArch cfg style), replacing the first existing assignment (commented or not) or appending if
        /// absent. All other lines are untouched.
        /// </summary>
        private static void SetOrAddKey(List<string> lines, string key, string value)
        {
            var assignment = $"{key} = \"{value}\"";
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                // Match `key = ...` and `#key = ...` / `# key = ...` (commented-out form RetroArch sometimes emits).
                var body = trimmed.StartsWith("#") ? trimmed.TrimStart('#', ' ') : trimmed;
                if (body.StartsWith(key, StringComparison.Ordinal))
                {
                    // Ensure it's this exact key (next non-space char is '=' or whitespace), not a prefix match.
                    var rest = body.Substring(key.Length);
                    var restTrim = rest.TrimStart();
                    if (restTrim.StartsWith("="))
                    {
                        lines[i] = assignment;
                        return;
                    }
                }
            }
            lines.Add(assignment);
        }

        /// <summary>
        /// Build the dummy content file path for a game's display name: a (created-on-demand) empty
        /// "&lt;sanitized game&gt;.zip" inside our presence cache dir. The BASENAME is what Steam displays.
        /// </summary>
        public static string GetDummyContentPath(string gameDisplayName)
        {
            var cacheDir = Path.Combine(AppContext.BaseDirectory, "Cache", "steam-presence");
            var safe = SanitizeFileName(string.IsNullOrWhiteSpace(gameDisplayName) ? "Game" : gameDisplayName.Trim());
            return Path.Combine(cacheDir, safe + ".zip");
        }

        /// <summary>
        /// Build (without spawning) the ProcessStartInfo that would launch the RetroArch presence puppet for
        /// <paramref name="gameDisplayName"/>: <c>retroarch.exe -L &lt;core&gt; &lt;dummy&gt;.zip</c>. Returns
        /// null if RetroArch / a candidate core can't be located. Exposed so the launch path AND verification
        /// can inspect the exact invocation without starting a process.
        /// </summary>
        public static ProcessStartInfo? BuildPuppetStartInfo(string gameDisplayName, out string corePath, out string dummyPath)
        {
            corePath = string.Empty;
            dummyPath = GetDummyContentPath(gameDisplayName);

            var exe = LocateRetroArch();
            if (exe == null) return null;

            var coresDir = Path.Combine(Path.GetDirectoryName(exe)!, "cores");
            corePath = PickCore(coresDir);
            if (string.IsNullOrEmpty(corePath))
            {
                Log.Warn($"[RetroArchPresence] no candidate core found in {coresDir}.");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add(corePath);
            psi.ArgumentList.Add(dummyPath);
            return psi;
        }

        /// <summary>
        /// Start the presence puppet: ensure the cfg keys, write the empty dummy "&lt;game&gt;.zip", and spawn
        /// retroarch.exe to load it with the chosen no-op core. Returns the spawned Process, or null on failure
        /// (failure is non-fatal — the real game still launches without presence).
        /// </summary>
        public static Process? StartPuppet(string gameDisplayName)
        {
            try
            {
                var psi = BuildPuppetStartInfo(gameDisplayName, out var corePath, out var dummyPath);
                if (psi == null)
                {
                    Log.Warn("[RetroArchPresence] StartPuppet: could not build invocation (RetroArch/core missing) — skipping presence.");
                    return null;
                }

                // Make sure the cfg has presence enabled + CONTENT-only format.
                var cfg = GetConfigPath(psi.FileName!);
                if (cfg != null) EnsurePresenceConfig(cfg);

                // Write the empty dummy content file (its basename is the Steam presence text).
                Directory.CreateDirectory(Path.GetDirectoryName(dummyPath)!);
                if (!File.Exists(dummyPath)) File.WriteAllBytes(dummyPath, Array.Empty<byte>());

                Log.Info($"[RetroArchPresence] StartPuppet '{gameDisplayName}': {psi.FileName} -L \"{corePath}\" \"{dummyPath}\"");
                var proc = Process.Start(psi);
                if (proc != null)
                    Log.Info($"[RetroArchPresence] puppet started (PID {proc.Id}).");
                return proc;
            }
            catch (Exception ex)
            {
                Log.Error($"[RetroArchPresence] StartPuppet failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Kill the presence puppet process (best-effort, never throws).</summary>
        public static void StopPuppet(Process? puppet)
        {
            if (puppet == null) return;
            try
            {
                if (!puppet.HasExited)
                {
                    puppet.Kill(entireProcessTree: true);
                    Log.Info($"[RetroArchPresence] puppet stopped (PID {puppet.Id}).");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[RetroArchPresence] StopPuppet: {ex.Message}");
            }
            finally
            {
                try { puppet.Dispose(); } catch { }
            }
        }

        /// <summary>First existing candidate core full path inside <paramref name="coresDir"/>, or "".</summary>
        private static string PickCore(string coresDir)
        {
            if (!Directory.Exists(coresDir)) return string.Empty;
            foreach (var name in CandidateCoreNames)
            {
                var p = Path.Combine(coresDir, name);
                if (File.Exists(p)) return p;
            }
            // Fallback: any *_libretro.dll present (still document that the owner must verify it idles).
            var any = Directory.EnumerateFiles(coresDir, "*_libretro.dll").FirstOrDefault();
            return any ?? string.Empty;
        }

        private static string SanitizeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
