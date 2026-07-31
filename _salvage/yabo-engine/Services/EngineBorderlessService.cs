using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] PART 1 (CORE): force each port's game window into a
    /// BORDERLESS, WGC-capturable, native-res window, and resolve the game's LIVE window title from its PID.
    /// Every engine reaches "borderless desktop fullscreen" a different way; this service maps
    /// folderName → that engine's mechanism (all source-pinned in dev/archive/shaderglass-automation.md,
    /// dev/integration-n64recomp.md, dev/integration-pc-oneoffs.md, dev/integration-sega-doom-quake.md).
    /// Two strategies:
    ///   (a) write the engine's borderless config key (creating the config file if it doesn't exist yet), or
    ///   (c) HWND-RESTYLE — for engines with no borderless config: set WS_POPUP / clear WS_OVERLAPPEDWINDOW +
    ///       SetWindowPos to the monitor RECT on the PID's top-level window.
    /// We NEVER select an engine's EXCLUSIVE-fullscreen path (not WGC-capturable).
    /// Windows-only; every method no-ops elsewhere and is best-effort (never throws into the launch path).
    /// </summary>
    public static class EngineBorderlessService
    {
        // ── borderless mechanism kinds ───────────────────────────────────────────────────────────────
        private enum Kind
        {
            None,            // snesrev / unknown → leave alone (in-engine GLSL path)
            LibUltraship,    // *.cfg.json  → Window.Backend=DXGI/DX11 (id 1) + Window.Fullscreen.Enabled=true + CVars.gSdlWindowedFullscreen=1
            N64Recomp,       // %LOCALAPPDATA%\<program_id>\graphics.json → wm_option:"Fullscreen"
            DevilutionX,     // diablo.ini  [Graphics] Fullscreen=1 + Upscale=1
            PerfectDark,     // pd.ini      [Video] Video.DefaultFullscreen=1 + Video.ExclusiveFullscreen=0
            Sm64Coop,        // sm64config.txt  → fullscreen true
            SmbRemastered,   // settings.cfg [video] mode=2
            RsdkV4V3,        // settings.ini [Window] FullScreen=true
            RsdkV5,          // Settings.ini [Video] windowed=0,exclusiveFS=0,border=0,screenShader=0
            VkQuake2,        // baseq2\config.cfg vid_fullscreen 0 (windowed) THEN HWND-restyle
            DsdaDoom,        // cfg use_fullscreen 1 + exclusive_fullscreen 0
            HwndRestyle,     // no config key → HWND restyle only (Infinite Mario 64, Paper Mario ReCut)
        }

        private sealed record EngineSpec(Kind Kind, string? ProgramId = null, string? FallbackTitle = null);

        // folderName → engine spec. program_id / fallback title cited per the integration docs.
        private static readonly Dictionary<string, EngineSpec> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── libultraship family (Window.Fullscreen.Enabled in *.cfg.json) ──
            ["harbourmasters.shipwright"]      = new(Kind.LibUltraship, FallbackTitle: "Ship of Harkinian (DirectX 11)"),
            ["harbourmasters.2ship2harkinian"] = new(Kind.LibUltraship, FallbackTitle: "2 Ship 2 Harkinian (DirectX 11)"),
            ["harbourmasters.starship"]        = new(Kind.LibUltraship, FallbackTitle: "Starship (DirectX 11)"),
            ["harbourmasters.ghostship"]       = new(Kind.LibUltraship, FallbackTitle: "Ghostship (DirectX 11)"),
            ["harbourmasters.spaghettikart"]   = new(Kind.LibUltraship, FallbackTitle: "Spaghetti Kart (DirectX 11)"),

            // ── N64Recomp family (wm_option:"Fullscreen" in %LOCALAPPDATA%\<program_id>\graphics.json) ──
            ["Zelda64Recomp.Zelda64Recomp"]        = new(Kind.N64Recomp, "Zelda64Recompiled",        "Zelda 64: Recompiled"),
            ["sonicdcer.Starfox64Recomp"]          = new(Kind.N64Recomp, "Starfox64Recompiled",      "Starfox 64: Recompiled"),
            ["sonicdcer.MarioKart64Recomp"]        = new(Kind.N64Recomp, "MarioKart64Recompiled",    "MarioKart 64: Recompiled"),
            ["sonicdcer.DNZHRecomp"]               = new(Kind.N64Recomp, "DNZHRecompiled",           "Duke Nukem Zero Hour: Recompiled"),
            ["BanjoRecomp.BanjoRecomp"]            = new(Kind.N64Recomp, "BanjoRecompiled",          "Banjo: Recompiled"),
            ["MegaMan64Recomp.MegaMan64Recompiled"]= new(Kind.N64Recomp, "MegaMan64Recompiled",      "Mega Man 64: Recompiled"),
            ["Rainchus.ChameleonTwist1-JP-Recomp"] = new(Kind.N64Recomp, "ChameleonTwistRecompiled", "Chameleon Twist: Recompiled"),
            ["Rainchus.Quest64-Recomp"]            = new(Kind.N64Recomp, "Quest64Recompiled",        "Quest 64: Recompiled"),
            ["RevoSucks.BM64Recomp"]               = new(Kind.N64Recomp, "BM64Recompiled",           "Bomberman 64: Recompiled"),
            ["RevoSucks.BMHeroRecomp"]             = new(Kind.N64Recomp, "BMHeroRecompiled",         "Bomberman Hero: Recompiled"),
            ["klorfmorf.Goemon64Recomp"]           = new(Kind.N64Recomp, "Goemon64Recompiled",       "Goemon 64: Recompiled"),
            ["theboy181.drmario64_recomp_plus"]    = new(Kind.N64Recomp, "drmario64_recomp",         "Dr. Mario 64: Recompiled"),
            // Paper Mario ReCut force-resets to Windowed → HWND-restyle (doc: integration-n64recomp.md SPECIAL).
            ["SMCGames.Paper-Mario-ReCut"]         = new(Kind.HwndRestyle, FallbackTitle: "Paper Mario ReCut"),

            // ── PC one-offs ──
            ["diasurgical.devilutionx"]            = new(Kind.DevilutionX, FallbackTitle: "DevilutionX"),
            ["perfect-dark-pc-port.perfect_dark"]  = new(Kind.PerfectDark, FallbackTitle: "Perfect Dark"),
            ["coop-deluxe.sm64coopdx"]             = new(Kind.Sm64Coop,   FallbackTitle: "Super Mario 64 Coop Deluxe"),
            ["JHDev2006.SuperMarioBrosRemastered"] = new(Kind.SmbRemastered, FallbackTitle: "SMB1R"),
            ["Brawmario.infinite-mario-64-ever"]   = new(Kind.HwndRestyle, FallbackTitle: "Infinite Mario 64"),

            // ── SEGA / RSDK ──
            ["Rubberduckycooly.Sonic-1-2-2013-Decompilation"] = new(Kind.RsdkV4V3, FallbackTitle: "Sonic the Hedgehog"),
            ["Rubberduckycooly.Sonic-CD-11-Decompilation"]    = new(Kind.RsdkV4V3, FallbackTitle: "Sonic CD"),
            ["Rubberduckycooly.RSDKv5-Decompilation"]         = new(Kind.RsdkV5,   FallbackTitle: "Sonic Mania"),

            // ── Doom / Quake ──
            ["kondrak.vkQuake2"] = new(Kind.VkQuake2, FallbackTitle: "Quake 2 (Vulkan) x64"),
            ["kraflab.dsda-doom"] = new(Kind.DsdaDoom, FallbackTitle: "dsda-doom 0.29.4"),

            // ── snesrev (in-engine GLSL renderer) ──
            ["snesrev.smw"] = new(Kind.None),
            ["snesrev.sm"]  = new(Kind.None),
        };

        /// <summary>Does this port need the borderless-overlay path at all? (false for snesrev/unknown).</summary>
        public static bool UsesBorderlessOverlay(string? folderName)
            => folderName != null && Map.TryGetValue(folderName, out var s) && s.Kind != Kind.None;

        /// <summary>The source-pinned fallback window title for a port (used only when the live PID title can't
        /// be read). null if the port is unknown.</summary>
        public static string? FallbackTitle(string? folderName)
            => folderName != null && Map.TryGetValue(folderName, out var s) ? s.FallbackTitle : null;

        /// <summary>True when this engine has NO borderless config key and relies on a post-launch HWND restyle
        /// (Infinite Mario 64, Paper Mario ReCut, and vkQuake2 after its windowed config write).</summary>
        public static bool NeedsHwndRestyle(string? folderName)
            => folderName != null && Map.TryGetValue(folderName, out var s)
               && (s.Kind == Kind.HwndRestyle || s.Kind == Kind.VkQuake2);

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // PART 1(a)/(c): force borderless BEFORE launch by writing the engine's config key.
        // Called BEFORE the game process starts. exeDir = the directory containing the game's exe (where
        // most of these per-game configs live, when they're not under %LOCALAPPDATA%). Best-effort.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        public static void ForceBorderlessConfig(string? folderName, string exeDir)
        {
            if (!OperatingSystem.IsWindows()) return;
            if (folderName == null || !Map.TryGetValue(folderName, out var spec)) return;
            try
            {
                switch (spec.Kind)
                {
                    case Kind.LibUltraship: WriteLibUltrashipCfg(exeDir); break;
                    case Kind.N64Recomp:    WriteN64RecompGraphics(exeDir, spec.ProgramId); break;
                    case Kind.DevilutionX:  WriteDevilutionXIni(exeDir); break;
                    case Kind.PerfectDark:  WritePerfectDarkIni(exeDir); break;
                    case Kind.Sm64Coop:     WriteSm64CoopConfig(); break;
                    case Kind.SmbRemastered:WriteSmbRemasteredCfg(exeDir); break;
                    case Kind.RsdkV4V3:     WriteRsdkV4Ini(exeDir); break;
                    case Kind.RsdkV5:       WriteRsdkV5Ini(exeDir); break;
                    case Kind.VkQuake2:     WriteVkQuake2Cfg(exeDir); break;   // windowed; HWND-restyle after launch
                    case Kind.DsdaDoom:     WriteDsdaDoomCfg(exeDir); break;
                    case Kind.HwndRestyle:  /* nothing to write — restyle after launch */ break;
                    case Kind.None:         default: break;
                }
            }
            catch (Exception ex) { Log.Warn($"borderless: config write failed for '{folderName}': {ex.Message}"); }
        }

        // ── libultraship: force the BORDERLESS-capturable fullscreen path in the (single) *.cfg.json beside
        //    the exe. Three keys, all source-pinned (Kenix3/libultraship):
        //
        //    1. Window.Backend = { Id:1, Name:"DirectX 11" }  →  WindowBackend::FAST3D_DXGI_DX11 (=1).
        //       enum WindowBackend { FAST3D_DXGI_DX11=1, FAST3D_SDL_OPENGL=2, FAST3D_SDL_METAL=3 }
        //       — include/fast/Fast3dWindow.h. The DXGI path (gfx_dxgi.cpp) ALWAYS goes borderless when
        //       fullscreen: start_in_fullscreen ⇒ ToggleBorderlessWindowFullScreen(true,false) ⇒
        //       SetWindowLongPtr(GWL_STYLE, WS_VISIBLE|WS_POPUP) sized to the monitor RECT (gfx_dxgi.cpp:194-201)
        //       = borderless-desktop at native res, WGC-capturable, NO exclusive mode-switch ⇒ NO WOBBLE.
        //       This is the SpaghettiKart fix: its cfg shipped Backend.Id=0 (INVALID — not one of 1/2/3),
        //       so libultraship fell off the DXGI borderless path and the captured window wobbled. Pin id 1.
        //    2. Window.Fullscreen.Enabled = true  →  isFullscreen (Fast3dWindow.cpp:85); the actual go-fullscreen.
        //    3. CVars.gSdlWindowedFullscreen = 1  →  belt-and-suspenders for the SDL path (gfx_sdl2.cpp:243-246:
        //       SDL_SetWindowFullscreen(on ? (gSdlWindowedFullscreen ? FULLSCREEN_DESKTOP : FULLSCREEN) : 0)).
        //       If a build/user ever forces the SDL backend, this keeps it borderless-DESKTOP (not exclusive).
        private static void WriteLibUltrashipCfg(string exeDir)
        {
            if (!Directory.Exists(exeDir)) return;
            var cfgs = Directory.EnumerateFiles(exeDir, "*.cfg.json", SearchOption.TopDirectoryOnly).ToList();
            if (cfgs.Count == 0)
            {
                // First launch hasn't created it yet → create a minimal one the engine merges over.
                var guess = Path.Combine(exeDir, "shipofharkinian.cfg.json");
                var node = new JsonObject
                {
                    ["CVars"] = new JsonObject { ["gSdlWindowedFullscreen"] = 1, ["gLowResMode"] = 0, ["gAdvancedResolution"] = new JsonObject { ["Enabled"] = 0 } },
                    ["Window"] = new JsonObject
                    {
                        ["Backend"] = new JsonObject { ["Id"] = 1, ["Name"] = "DirectX 11" },
                        ["Fullscreen"] = new JsonObject { ["Enabled"] = true },
                    },
                };
                File.WriteAllText(guess, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Log.Info($"borderless(libultraship): created {Path.GetFileName(guess)} with Backend.Id=1 (DXGI/DX11) + Window.Fullscreen.Enabled=true + gSdlWindowedFullscreen=1.");
                return;
            }
            foreach (var cfg in cfgs)
            {
                var root = JsonNode.Parse(File.ReadAllText(cfg)) as JsonObject ?? new JsonObject();

                var win = root["Window"] as JsonObject;
                if (win == null) { win = new JsonObject(); root["Window"] = win; }

                // (1) pin the DXGI/DX11 backend (id 1) — the always-borderless Windows path. Overwrite any
                //     invalid/stale id (e.g. SpaghettiKart's id 0) that drops the game off the borderless path.
                var backend = win["Backend"] as JsonObject;
                if (backend == null) { backend = new JsonObject(); win["Backend"] = backend; }
                backend["Id"] = 1;
                backend["Name"] = "DirectX 11";

                // (2) the actual fullscreen toggle.
                var fs = win["Fullscreen"] as JsonObject;
                if (fs == null) { fs = new JsonObject(); win["Fullscreen"] = fs; }
                fs["Enabled"] = true;

                // (3) SDL-path fallback → windowed-desktop (borderless), never exclusive.
                var cvars = root["CVars"] as JsonObject;
                if (cvars == null) { cvars = new JsonObject(); root["CVars"] = cvars; }
                cvars["gSdlWindowedFullscreen"] = 1;

                // (4) make the framebuffer FILL the borderless window — the "doesn't scale to window" fix.
                //     gLowResMode + gAdvancedResolution.Enabled force a centered/fixed-size blit (Fast3dGui.cpp
                //     DrawGame); with both OFF, LUS stretches the framebuffer to the whole window. (Starship was
                //     reported rendering at a fixed size inside the borderless window — this is the cause.)
                cvars["gLowResMode"] = 0;
                var advRes = cvars["gAdvancedResolution"] as JsonObject;
                if (advRes == null) { advRes = new JsonObject(); cvars["gAdvancedResolution"] = advRes; }
                advRes["Enabled"] = 0;

                File.WriteAllText(cfg, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Log.Info($"borderless(libultraship): set Backend.Id=1 + Fullscreen.Enabled=true + gSdlWindowedFullscreen=1 + scale-to-window (gLowResMode=0, gAdvancedResolution.Enabled=0) in {Path.GetFileName(cfg)}.");
            }
        }

        // ── N64Recomp / RT64: wm_option:"Fullscreen" in graphics.json (portable next-to-exe if portable.txt,
        //    else %LOCALAPPDATA%\<program_id>\graphics.json). Merge if it exists; create one-key file otherwise.
        //    "Fullscreen" IS the borderless-desktop value here — NOT exclusive. On Windows RT64 applies it via
        //    ApplicationWindow::setFullScreen(true): MonitorFromWindow → WS_VISIBLE|WS_POPUP & ~WS_OVERLAPPEDWINDOW
        //    sized to monitorInfo.rcMonitor (rt64_application_window.cpp, comment "Set borderless full screen to
        //    that monitor"); there is NO ChangeDisplaySettings / exclusive mode-switch (integration-n64recomp.md §3).
        //    enum WindowMode { Windowed, Fullscreen } / SERIALIZE_ENUM {{Windowed,"Windowed"},{Fullscreen,"Fullscreen"}}
        //    — N64ModernRuntime ultramodern/config.hpp. So this value is correct/capturable; a window that never
        //    appears (e.g. Banjo) is a launch/ROM-provisioning issue, not a borderless-config one. ──
        private static void WriteN64RecompGraphics(string exeDir, string? programId)
        {
            // ALWAYS write to %LOCALAPPDATA%\<program_id>\graphics.json. We NEVER create a portable.txt for
            // these ports — it redirects config to a CWD-relative path and crashes them (Banjo: 0xc0000409,
            // see RomLibraryService). The old "portable.txt → next to exe" branch was a footgun: if that file
            // ever existed it silently re-enabled the crash path. Removed.
            if (string.IsNullOrWhiteSpace(programId)) return;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(local, programId!, "graphics.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            JsonObject root = File.Exists(path)
                ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject())
                : new JsonObject();
            root["wm_option"] = "Fullscreen";
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"borderless(N64Recomp): set wm_option=\"Fullscreen\" in {path}.");
        }

        // ── DevilutionX: [Graphics] Fullscreen=1 + Upscale=1 in diablo.ini (exe dir, portable). ──
        private static void WriteDevilutionXIni(string exeDir)
        {
            var ini = Path.Combine(exeDir, "diablo.ini");
            IniMerge(ini, "Graphics", new (string, string)[] { ("Fullscreen", "1"), ("Upscale", "1") });
            Log.Info($"borderless(DevilutionX): [Graphics] Fullscreen=1, Upscale=1 in {ini}.");
        }

        // ── Perfect Dark: [Video] Video.DefaultFullscreen=1 + Video.ExclusiveFullscreen=0 in pd.ini. ──
        private static void WritePerfectDarkIni(string exeDir)
        {
            var ini = Path.Combine(exeDir, "pd.ini");
            IniMerge(ini, "Video", new (string, string)[]
            {
                ("Video.DefaultFullscreen", "1"),
                ("Video.ExclusiveFullscreen", "0"),
            }, spaceDelimited: true);
            Log.Info($"borderless(PerfectDark): [Video] DefaultFullscreen=1, ExclusiveFullscreen=0 in {ini}.");
        }

        // ── SM64 Co-op Deluxe: `fullscreen true` in %APPDATA%\sm64coopdx\sm64config.txt (key value lines). ──
        private static void WriteSm64CoopConfig()
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appdata, "sm64coopdx");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "sm64config.txt");
            KeyValueMerge(path, "fullscreen", "true");
            Log.Info($"borderless(sm64coopdx): fullscreen true in {path}.");
        }

        // ── SMB Remastered (Godot): [video] mode=2 (borderless fullscreen) in settings.cfg.
        //    Drop a portable.txt next to the exe so config lives in <exeDir>\config\settings.cfg. ──
        private static void WriteSmbRemasteredCfg(string exeDir)
        {
            try { var pf = Path.Combine(exeDir, "portable.txt"); if (!File.Exists(pf)) File.WriteAllText(pf, ""); } catch { }
            var cfgDir = Path.Combine(exeDir, "config");
            Directory.CreateDirectory(cfgDir);
            var path = Path.Combine(cfgDir, "settings.cfg");
            IniMerge(path, "video", new (string, string)[] { ("mode", "2") });
            Log.Info($"borderless(SMB Remastered): [video] mode=2 in {path}.");
        }

        // ── RSDKv4/v3: [Window] FullScreen=true in settings.ini (exe dir). ──
        private static void WriteRsdkV4Ini(string exeDir)
        {
            var ini = Path.Combine(exeDir, "settings.ini");
            IniMerge(ini, "Window", new (string, string)[] { ("FullScreen", "true") });
            Log.Info($"borderless(RSDKv4/v3): [Window] FullScreen=true in {ini}.");
        }

        // ── RSDKv5: [Video] windowed=0, exclusiveFS=0, border=0, screenShader=0 in Settings.ini.
        //    RSDKv5 writes booleans as y/n; the iniparser read accepts 0/1 too. Use n/y for fidelity. ──
        private static void WriteRsdkV5Ini(string exeDir)
        {
            var ini = Path.Combine(exeDir, "Settings.ini");
            IniMerge(ini, "Video", new (string, string)[]
            {
                ("windowed", "n"),
                ("exclusiveFS", "n"),
                ("border", "n"),
                ("screenShader", "0"),
            });
            Log.Info($"borderless(RSDKv5): [Video] windowed=n, exclusiveFS=n, border=n, screenShader=0 in {ini}.");
        }

        // ── vkQuake2: vid_fullscreen 0 (windowed; the fullscreen path is exclusive CDS — hostile to WGC).
        //    config.cfg lives in baseq2\. We write `set vid_fullscreen "0"` so the engine reads windowed,
        //    THEN HWND-restyle after launch makes it borderless-fill. ──
        private static void WriteVkQuake2Cfg(string exeDir)
        {
            // exeDir is vkquake2-…_win64\; baseq2 sits beside the exe.
            var baseq2 = Path.Combine(exeDir, "baseq2");
            Directory.CreateDirectory(baseq2);
            var cfg = Path.Combine(baseq2, "config.cfg");
            QuakeCvarMerge(cfg, "vid_fullscreen", "0");
            QuakeCvarMerge(cfg, "vid_ref", "vk");
            Log.Info($"borderless(vkQuake2): set vid_fullscreen \"0\" + vid_ref \"vk\" in {cfg} (HWND-restyle to fill after launch).");
        }

        // ── DSDA-Doom: use_fullscreen 1 + exclusive_fullscreen 0 in its config (key value lines).
        //    The config sits beside the exe (dsda-doom.cfg) for the portable Windows build. ──
        private static void WriteDsdaDoomCfg(string exeDir)
        {
            var cfg = Directory.Exists(exeDir)
                ? Directory.EnumerateFiles(exeDir, "*.cfg", SearchOption.TopDirectoryOnly)
                           .FirstOrDefault(f => Path.GetFileName(f).Contains("dsda", StringComparison.OrdinalIgnoreCase))
                  ?? Path.Combine(exeDir, "dsda-doom.cfg")
                : Path.Combine(exeDir, "dsda-doom.cfg");
            DoomCfgMerge(cfg, "use_fullscreen", "1");
            DoomCfgMerge(cfg, "exclusive_fullscreen", "0");
            Log.Info($"borderless(DSDA-Doom): use_fullscreen 1 + exclusive_fullscreen 0 in {cfg}.");
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // PART 1(b): resolve the game's LIVE window title from its PID (enumerate the process's top-level
        // visible window → use that title). Source-pinned titles are the FALLBACK only.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        public static string ResolveLiveTitle(Process? gameProcess, string? folderName, int timeoutMs = 15000)
        {
            var fallback = FallbackTitle(folderName) ?? string.Empty;
            if (!OperatingSystem.IsWindows() || gameProcess == null) return fallback;

            int waited = 0;
            while (waited < timeoutMs)
            {
                try { if (gameProcess.HasExited) break; } catch { break; }
                var t = TopLevelTitleForPid((uint)SafePid(gameProcess));
                if (!string.IsNullOrWhiteSpace(t)) return t!;
                System.Threading.Thread.Sleep(150);
                waited += 150;
            }
            return fallback;
        }

        private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }

        /// <summary>Enumerate the visible top-level windows owned by <paramref name="pid"/> and return the title
        /// of the first one with a non-empty caption. null if none yet.</summary>
        public static string? TopLevelTitleForPid(uint pid)
        {
            if (!OperatingSystem.IsWindows() || pid == 0 || pid == unchecked((uint)-1)) return null;
            string? found = null;
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd)) return true;
                        GetWindowThreadProcessId(hWnd, out var wpid);
                        if (wpid != pid) return true;
                        int len = GetWindowTextLength(hWnd);
                        if (len <= 0) return true;
                        var sb = new StringBuilder(len + 1);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        var s = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) { found = s; return false; }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // PART 1(c): HWND-RESTYLE — strip the border + size to the monitor on the PID's top-level window.
        // For engines with no borderless config (Infinite Mario 64, Paper Mario ReCut) and vkQuake2 (after
        // its windowed config). Best-effort; polls for the window for a few seconds.
        // ─────────────────────────────────────────────────────────────────────────────────────────────
        public static void HwndRestyleToBorderless(Process? gameProcess, string? folderName)
        {
            if (!OperatingSystem.IsWindows() || gameProcess == null) return;
            if (folderName == null || !NeedsHwndRestyle(folderName)) return;
            try
            {
                IntPtr hwnd = IntPtr.Zero;
                for (int i = 0; i < 100; i++)   // ≤15s
                {
                    try { if (gameProcess.HasExited) return; } catch { return; }
                    hwnd = TopLevelHwndForPid((uint)SafePid(gameProcess));
                    if (hwnd != IntPtr.Zero) break;
                    System.Threading.Thread.Sleep(150);
                }
                if (hwnd == IntPtr.Zero) { Log.Warn($"borderless(HWND-restyle): no window for '{folderName}' within timeout."); return; }

                // Give the engine a moment to finish creating/positioning its window, then restyle once.
                System.Threading.Thread.Sleep(700);
                ApplyPopupRestyle(hwnd);
                Log.Info($"borderless(HWND-restyle): WS_POPUP + monitor-rect applied to '{folderName}' window (hwnd 0x{hwnd.ToInt64():X}).");

                // Paper Mario ReCut force-resets to Windowed shortly after launch; re-apply a couple of times.
                if (string.Equals(folderName, "SMCGames.Paper-Mario-ReCut", StringComparison.OrdinalIgnoreCase))
                {
                    for (int k = 0; k < 3; k++)
                    {
                        System.Threading.Thread.Sleep(900);
                        try { if (gameProcess.HasExited) return; } catch { return; }
                        ApplyPopupRestyle(hwnd);
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"borderless(HWND-restyle) failed for '{folderName}': {ex.Message}"); }
        }

        private static void ApplyPopupRestyle(IntPtr hwnd)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero || !GetMonitorInfo(hmon, ref mi)) return;
            var r = mi.rcMonitor;

            long style = GetStyle(hwnd);
            style = (style | WS_VISIBLE | WS_POPUP) & ~WS_OVERLAPPEDWINDOW;
            SetStyle(hwnd, style);
            SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        public static IntPtr TopLevelHwndForPid(uint pid)
        {
            if (!OperatingSystem.IsWindows() || pid == 0) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd)) return true;
                        GetWindowThreadProcessId(hWnd, out var wpid);
                        if (wpid != pid) return true;
                        if (GetWindowTextLength(hWnd) <= 0) return true;   // skip invisible helper windows
                        found = hWnd; return false;
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        // ── tiny INI / key-value mergers (preserve other lines; replace-or-append a single key) ──────

        /// <summary>Merge `key=value` (or `key value` if <paramref name="spaceDelimited"/>) lines under
        /// <paramref name="section"/> ([Section]) in an INI, preserving the rest. Creates the file if missing.</summary>
        private static void IniMerge(string path, string section, (string Key, string Value)[] kvs, bool spaceDelimited = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            string nl = "\r\n";

            // locate the [section] header + its extent.
            int secStart = -1, secEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    var name = t[1..^1].Trim();
                    if (secStart < 0 && string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) { secStart = i; }
                    else if (secStart >= 0) { secEnd = i; break; }
                }
            }
            if (secStart < 0)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
                lines.Add($"[{section}]");
                secStart = lines.Count - 1; secEnd = lines.Count;
            }

            foreach (var (key, value) in kvs)
            {
                string newLine = spaceDelimited ? $"{key} {value}" : $"{key}={value}";
                bool replaced = false;
                for (int i = secStart + 1; i < secEnd && i < lines.Count; i++)
                {
                    var t = lines[i].TrimStart();
                    bool match = spaceDelimited
                        ? (t.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase) || t.Equals(key, StringComparison.OrdinalIgnoreCase))
                        : (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) || t.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase));
                    if (match) { lines[i] = newLine; replaced = true; break; }
                }
                if (!replaced) { lines.Insert(secEnd, newLine); secEnd++; }
            }
            File.WriteAllText(path, string.Join(nl, lines) + nl);
        }

        /// <summary>Replace-or-append a `key value` line in a flat config (sm64config.txt style).</summary>
        private static void KeyValueMerge(string path, string key, string value)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase) || t.Equals(key, StringComparison.OrdinalIgnoreCase))
                { lines[i] = $"{key} {value}"; replaced = true; break; }
            }
            if (!replaced) lines.Add($"{key} {value}");
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        }

        /// <summary>Replace-or-append a `set <cvar> "<value>"` line in a Quake config.cfg.</summary>
        private static void QuakeCvarMerge(string path, string cvar, string value)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith($"set {cvar} ", StringComparison.OrdinalIgnoreCase))
                { lines[i] = $"set {cvar} \"{value}\""; replaced = true; break; }
            }
            if (!replaced) lines.Add($"set {cvar} \"{value}\"");
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        }

        /// <summary>Replace-or-append a `key value` line in a dsda/prboom .cfg (space-delimited).</summary>
        private static void DoomCfgMerge(string path, string key, string value)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase) || t.StartsWith(key + "\t", StringComparison.OrdinalIgnoreCase))
                { lines[i] = $"{key} {value}"; replaced = true; break; }
            }
            if (!replaced) lines.Add($"{key} {value}");
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        }

        // ── P/Invoke ─────────────────────────────────────────────────────────────────────────────────
        private const int GWL_STYLE = -16;
        private const long WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO mi);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)] private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static long GetStyle(IntPtr h) => IntPtr.Size == 8 ? GetWindowLongPtr(h, GWL_STYLE) : (uint)GetWindowLong32(h, GWL_STYLE);
        private static void SetStyle(IntPtr h, long s) { if (IntPtr.Size == 8) SetWindowLongPtr(h, GWL_STYLE, s); else SetWindowLong32(h, GWL_STYLE, (int)s); }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    }
}
