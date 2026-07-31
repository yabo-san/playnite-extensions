using GitHubLauncher.Core.Models;
using GitHubLauncher.Core.Services;
using GithubLauncher.Services;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace GithubLauncher.Models
{
    /// <summary>
    /// Describes one data file (ROM / IWAD / pak / disc image) that a port needs
    /// the user to supply, and where the launcher should place it relative to the
    /// port's install directory. Parsed from the optional apps.json "dataFiles" key.
    /// </summary>
    public class DataFileNeed
    {
        /// <summary>Expected file name, e.g. "Super Mario 64.z64" or "DOOM.WAD".</summary>
        public string? Name { get; set; }

        /// <summary>
        /// Relative path under the install dir where the file goes
        /// (e.g. "" for next-to-exe, "data", "id1"). Never absolute.
        /// </summary>
        public string? TargetSubpath { get; set; }

        /// <summary>Optional SHA-1 hash (hex) for validation/matching.</summary>
        public string? Sha1 { get; set; }

        /// <summary>Optional XXH3-64 hash (hex) for validation/matching.</summary>
        public string? Xxh3 { get; set; }

        /// <summary>[yabo-launcher fork] When true, the port can install and launch WITHOUT this data file —
        /// it just unlocks one game (the Doom model: one engine, the ROMs you provide unlock games). When
        /// false (the default), the file is required for the port to function. Backward-compatible: entries
        /// that don't set it default to required, so the other 178 catalog entries are unaffected. From the
        /// optional apps.json "optional" key on a dataFiles element.</summary>
        public bool Optional { get; set; }
    }

    /// <summary>[yabo-launcher fork] One element of a multi-step build (apps.json "buildSteps" array). Lets a
    /// single catalog entry run SEVERAL post-install build steps, each gated on its own source ROM being
    /// present ("bring what you have"). Mirrors the existing single buildStep/buildPatch/buildSource/buildTarget
    /// fields, but per-step. Reuses the same step implementations (bps-patch, smas-extract).</summary>
    public class BuildStepInfo
    {
        /// <summary>Step kind: "bps-patch" or "smas-extract" (same vocabulary as the single buildStep).</summary>
        public string? Step { get; set; }
        /// <summary>The placed ROM filename this step consumes. The step runs ONLY IF this file exists in the
        /// install dir (that's how "bring what you have" works). For bps-patch this is the patch input; for
        /// smas-extract this is the SMAS ROM used as the zstd dictionary.</summary>
        public string? Source { get; set; }
        /// <summary>bps-patch only: the BPS patch filename (shipped in the prebuilt release).</summary>
        public string? Patch { get; set; }
        /// <summary>bps-patch only: the output file to produce (e.g. smw_assets.dat). Idempotency guard:
        /// the step is skipped if this already exists.</summary>
        public string? Target { get; set; }
    }

    /// <summary>[yabo-launcher fork] One launchable game offered by a multi-game port (apps.json "games" array),
    /// for the UI game picker. The Doom model: one engine (smw.exe) + the ROMs the user provides unlock games.</summary>
    public class GameLaunchOption
    {
        /// <summary>Display label in the picker, e.g. "Super Mario Bros.".</summary>
        public string? Label { get; set; }
        /// <summary>ROM filename passed to the engine via --rom for this game. EMPTY means launch with NO --rom
        /// (e.g. SMW boots from smw_assets.dat). Resolved relative to the install dir at launch time.</summary>
        public string? Rom { get; set; }
        /// <summary>The asset file (in the install dir) that must exist for this game to be OFFERED — i.e. the
        /// product of the build step that unlocks it (smw_assets.dat / smb1.sfc / smbll.sfc).</summary>
        public string? Requires { get; set; }
    }

    public class GameInfo : INotifyPropertyChanged
    {
        private const string DefaultInstalledVersion = "v0.0.0";
        public event Action<Process?>? GameProcessStarted;
        private string? _latestVersion;
        private string? _installedVersion;
        private string? _preferredVersion;
        private string? _skippedUpdateVersion;
        private GameStatus _status = GameStatus.NotInstalled;
        private bool _isLoading;
        private GitHubRelease? _cachedRelease;
        public GameManager? GameManager { get; set; }

        /// <summary>[yabo-launcher fork] Captured stdout/stderr of the most recent launched process, drained
        /// live while it ran. The fire-and-forget exit handler dumps these in GUI mode; the headless CLI
        /// (`--run`) flushes them synchronously before exit (since its Task.Run is killed on return).</summary>
        private StringBuilder? _lastStdout;
        private StringBuilder? _lastStderr;
        private DateTime _lastLaunchUtc;

        public string? Name { get; set; }
        public string? Repository { get; set; }
        public string? FolderName { get; set; }
        public string? InstallPath { get; set; }
        public string? GameIconUrl { get; set; }

        /// <summary>[yabo-launcher fork] System/engine grouping for the sidebar category filter
        /// (e.g. "N64", "SNES", "Doom", "Quake"). Populated from the optional apps.json "category" key.</summary>
        public string? Category { get; set; }

        /// <summary>
        /// [yabo-launcher fork] For tools that aren't portable / aren't downloadable in-model (installer-only,
        /// off-GitHub, anti-bot — e.g. Zandronum/Doomseeker). When set, the launcher does NOT download or
        /// manage it: the action just opens this URL in the browser so the user installs it themselves; the
        /// launcher only detects + wires it afterward. Populated from the apps.json "externalUrl" key.
        /// </summary>
        public string? ExternalUrl { get; set; }
        public bool IsExternal => !string.IsNullOrWhiteSpace(ExternalUrl);

        /// <summary>[yabo-launcher fork] ROM/data ingest mode for the Playnite extension + data staging
        /// ("place-file" / "picker" / "steam-data" / "data-folder" / "cli-arg"). Populated from the
        /// apps.json "ingest" key. Carried through so a catalog round-trip (or GUI edit) doesn't strip it.</summary>
        public string? Ingest { get; set; }

        /// <summary>[yabo-launcher fork] Steam appid this port sources its game DATA from when
        /// <see cref="Ingest"/> is "steam-data" (Steam-first / IA-fallback precedence). When set, the install
        /// flow resolves the user's OWNED Steam copy via <see cref="GithubLauncher.Services.SteamContentLocator"/>
        /// and stages the declared <see cref="DataFiles"/> from it into the binary's install dir. When NOT set,
        /// the locator falls back to a name->appid map for the known steam-data cards
        /// (<see cref="GithubLauncher.Services.SteamContentLocator.ResolveSteamDataAppId"/>). From the apps.json
        /// "steamAppId" key. Cards that still need it set: Duck Game Rebuilt (312530), OpenRCT2 needs RCT2
        /// Deluxe (285330) — the others (ironwail 2310, vkQuake2 2320) resolve by name.</summary>
        public int? SteamAppId { get; set; }

        /// <summary>[yabo-launcher fork] Internet-Archive in-app install (RohanKar parity). A DIRECT
        /// archive.org download URL of the port's repacked archive (zip/7z/rar), e.g.
        /// <c>https://archive.org/download/&lt;identifier&gt;/&lt;file&gt;</c>. When <see cref="Ingest"/> is
        /// "internet-archive" and this is set, <see cref="PerformActionAsync"/> downloads + extracts it via
        /// <see cref="GithubLauncher.Services.InternetArchiveInstallService"/> (SharpCompress) into the
        /// install dir, strips Mark-of-the-Web Zone.Identifier streams, and locates the launch exe — instead
        /// of the GitHub-release path. From the apps.json "contentUrl" key.</summary>
        public string? ContentUrl { get; set; }

        /// <summary>[yabo-launcher fork — itch.io route] When Ingest=="itch": the public itch page URL
        /// (e.g. https://yellowafterlife.itch.io/gmedit). Resolved to a game id via {url}/data.json. apps.json "itchUrl".</summary>
        public string? ItchUrl { get; set; }
        /// <summary>[itch route] Optional numeric itch game id — skips the data.json lookup. apps.json "itchGameId".</summary>
        public long? ItchGameId { get; set; }
        /// <summary>[itch route] Optional upload selector: a filename/display-name substring (e.g. "GMEdit-Windows.zip")
        /// to pick the right upload deterministically; falls back to the first Windows upload. apps.json "itchUploadMatch".</summary>
        public string? ItchUploadMatch { get; set; }

        /// <summary>[yabo-launcher fork] The archive.org item identifier for an internet-archive entry (the
        /// <c>&lt;identifier&gt;</c> in the download URL). Informational/diagnostic; the actual download uses
        /// <see cref="ContentUrl"/>. From the apps.json "iaIdentifier" key.</summary>
        public string? IaIdentifier { get; set; }

        /// <summary>[yabo-launcher fork] User favorite flag (apps.json "favorite"). Set by the desktop ★ toggle or
        /// the mobile catalog editor; the UI surfaces a Favorites filter. Pure catalog metadata, no install effect.</summary>
        public bool Favorite { get; set; }

        /// <summary>[yabo-launcher fork] User HIDDEN flag (apps.json "hidden"). Tucks a card behind the "Show hidden"
        /// toggle WITHOUT deleting it (owner: "I don't want to lose anything"). Set via the mobile editor / desktop.</summary>
        public bool Hidden { get; set; }

        /// <summary>[yabo-launcher fork] Part of Rohan's bulk IA library — hidden by default, revealed by the
        /// "Show Rohan's Packs" toggle (Settings → Catalog). Lets the launcher gate his 280+ repacks as a group.</summary>
        public bool RohanLibrary { get; set; }

        /// <summary>[yabo-launcher fork] True when the "Open download page" context-menu item should be shown:
        /// either there's a real <see cref="ExternalUrl"/>, or it's a managed Internet-Archive card with an
        /// <see cref="IaIdentifier"/> (whose details page we can synthesize). Additive over <see cref="IsExternal"/>.</summary>
        public bool HasInfoLink => !string.IsNullOrWhiteSpace(ExternalUrl) || !string.IsNullOrWhiteSpace(IaIdentifier);

        /// <summary>[yabo-launcher fork] Label for the "Open download page" menu item: the IA wording when this is
        /// an Internet-Archive-only card (no externalUrl), the original wording otherwise.</summary>
        public string InfoLinkHeader =>
            string.IsNullOrWhiteSpace(ExternalUrl) && !string.IsNullOrWhiteSpace(IaIdentifier)
                ? "Open on Internet Archive"
                : "Open download page";

        /// <summary>[yabo-launcher fork] For N64Recomp (librecomp) ports: the framework's <c>game_id</c>
        /// (the <c>recomp::GameEntry.game_id</c> registered via <c>recomp::register_game</c>). librecomp
        /// auto-loads a STORED ROM from <c>config_path/&lt;game_id&gt;.z64</c>, validated by hash on startup
        /// (<c>check_all_stored_roms</c> → <c>check_stored_rom</c>). When set, the engine drops a
        /// <c>portable.txt</c> in the install dir (making <c>config_path</c> = the install dir, the same
        /// portable behavior as graphics.json) and stages the STANDARD US .z64 as <c>&lt;game_id&gt;.z64</c>
        /// so the port auto-loads it WITHOUT the in-game ROM picker. Populated from the apps.json
        /// "gameId" key. NOTE: the recomp decompresses/byteswaps internally — the staged file is just the
        /// standard ROM, not a pre-decompressed one.</summary>
        public string? GameId { get; set; }

        /// <summary>[yabo-launcher fork] Verification status, kept in the catalog so it's the single source
        /// of truth (not memory): "played" (confirmed to gameplay), "launches" (exe starts, gameplay
        /// unconfirmed), or null (untested). Populated from the apps.json "tested" key; SUPPORT.md is
        /// generated from it.</summary>
        public string? Tested { get; set; }

        /// <summary>[yabo-launcher fork] Optional override of the name used to search SteamGridDB for cover
        /// art, for ports whose catalog name is the port/repo, not the game ("OpenLoco" -> "Locomotion").
        /// Populated from the apps.json "artName" key; falls back to the (cleaned) display name.</summary>
        public string? ArtName { get; set; }
        /// <summary>[yabo-launcher fork] Optional DIRECT cover-art image URL (a specific SteamGridDB grid the
        /// user picked). When set it pins the cover and bypasses the search entirely. From the "artUrl" key.</summary>
        public string? ArtUrl { get; set; }
        /// <summary>[yabo-launcher fork] App-level component (a managed utility) — managed in Settings,
        /// NOT shown as a library tile. From the "appComponent" key.</summary>
        public bool AppComponent { get; set; }
        /// <summary>[yabo-launcher fork] Port whose tooling shells out to a bare `python` (e.g. zelda3's
        /// restool.py). When true, the managed Python runtime is injected ahead of the system Python at launch
        /// so it doesn't break against the user's Python version. From the "pythonTooling" key.</summary>
        public bool PythonTooling { get; set; }
        /// <summary>[yabo-launcher fork] For multi-exe release archives (e.g. NBlood ships nblood/rednukem/
        /// pcexhumed in one zip), the exact .exe filename this entry should launch — so headless `--run`
        /// auto-selects it instead of bailing to the old GUI executable picker. From "executableName".</summary>
        public string? ExecutableName { get; set; }

        /// <summary>[yabo-launcher fork] Optional fixed command-line argument(s) appended to the launched exe on
        /// the direct-launch path. Used by the native SMAS entry to tell the snesrev/smw engine which extracted
        /// ROM to boot (e.g. "smb1.sfc" — the default game until the UI exposes a per-launch game picker). From
        /// the apps.json "launchArgs" key.</summary>
        public string? LaunchArgs { get; set; }

        public bool IsExperimental { get; set; }
        public bool IsCustom { get; set; }

        /// <summary>[yabo-launcher fork] Optional list of OTHER cards' <see cref="FolderName"/>s this card pulls in
        /// automatically right after its OWN install — "one staged card = the whole experience". Populated from the
        /// apps.json "companions" string array. The Doom 1 + 2 (DoomLauncher) card uses it to auto-install its three
        /// source-port engines (Doom Retro Tier-1, DSDA-Doom, GZDoom) so the user stages a single card instead of
        /// installing each engine by hand; after the companions land, the post-install flow re-runs
        /// <see cref="GithubLauncher.Services.CategoryWiring.WireDoomLauncher"/> so they're registered and the
        /// default port is pinned. Each companion is resolved by folderName from the catalog and installed via the
        /// SAME <see cref="PerformActionAsync"/> path (so it gets versioning/data/wiring); already-installed
        /// companions are skipped. Backward-compatible: an absent key yields an empty list (no companions), so the
        /// other catalog entries are unaffected.</summary>
        public List<string> Companions { get; set; } = new List<string>();

        /// <summary>[yabo-launcher fork] Post-install build step that produces a runtime data file from the
        /// user's supplied ROM, so we can ship the snesrev prebuilt exe natively instead of relying on the
        /// upstream RadzPrower launcher's fragile clone+TCC-compile chain. Currently the only mode is
        /// "bps-patch": apply <see cref="BuildPatch"/> (a BPS patch shipped in the prebuilt release) to
        /// <see cref="BuildSource"/> (the placed ROM) → <see cref="BuildTarget"/> (e.g. zelda3_assets.dat),
        /// which the prebuilt exe loads. Runs at launch when the target is missing. From the apps.json
        /// "buildStep"/"buildPatch"/"buildSource"/"buildTarget" keys.</summary>
        public string? BuildStep { get; set; }
        public string? BuildPatch { get; set; }
        public string? BuildSource { get; set; }
        public string? BuildTarget { get; set; }

        /// <summary>[yabo-launcher fork] Optional MULTI-step build (apps.json "buildSteps" array). When present,
        /// <see cref="RunBuildStepIfNeeded"/> iterates these instead of the single buildStep, running EACH step
        /// only if its <see cref="BuildStepInfo.Source"/> ROM exists in the install dir ("bring what you have").
        /// Entries that use the legacy single buildStep leave this empty and behave exactly as before.</summary>
        public List<BuildStepInfo> BuildSteps { get; set; } = new List<BuildStepInfo>();

        /// <summary>[yabo-launcher fork] Optional list of launchable games this port offers (apps.json "games"
        /// array) for the UI game picker — multi-game engines like snesrev/smw (SMW + SMB1 + Lost Levels).
        /// Empty for single-game ports.</summary>
        public List<GameLaunchOption> Games { get; set; } = new List<GameLaunchOption>();

        /// <summary>[yabo-launcher fork] For the "smas-extract" build step (native snesrev/smw replacement for
        /// qurious-pixel/SMAS_Launcher): the placed Super Mario All-Stars ROM (e.g. "smas.sfc") used as the
        /// zstd dictionary to decompress snesrev/smw's bundled smb1/smbll frames into smb1.sfc + smbll.sfc.
        /// The standalone smw.sfc that feeds the bps-patch (→ smw_assets.dat, which smw.exe always loads) is
        /// still given via <see cref="BuildSource"/>/<see cref="BuildPatch"/>/<see cref="BuildTarget"/>.
        /// From the apps.json "buildExtractSource" key.</summary>
        public string? BuildExtractSource { get; set; }

        /// <summary>[yabo-launcher fork] Source archive URL for the "tcc-compile" build step — the GitHub
        /// codeload zip of a port that has NO prebuilt release and bakes ROM-derived assets into the exe at
        /// build time (snesrev/sm / Super Metroid). The step downloads this + a self-contained portable TCC
        /// and SDL2, places the user's ROM as <see cref="BuildSource"/>, and compiles <see cref="BuildTarget"/>
        /// locally so the user needs no toolchain. From the apps.json "buildSourceUrl" key.</summary>
        public string? BuildSourceUrl { get; set; }

        /// <summary>[yabo-launcher fork] Run the declared <see cref="BuildStep"/> if its output is missing.
        /// Returns true if the target exists (or was just produced) or there is no build step; false on failure.
        /// Safe to call on every launch — it no-ops once the target is present.</summary>
        public bool RunBuildStepIfNeeded(string installDir)
        {
            // [yabo-launcher fork] MULTI-step path (apps.json "buildSteps"). Iterate each declared step and run
            // it ONLY IF its source ROM is present in the install dir — that's how "bring what you have" works:
            // a port with both smw.sfc and smas.sfc builds all three games; with only smw.sfc it builds just
            // SMW and skips the SMAS extract. Each step keeps its own idempotency guard (skip if target exists).
            // The legacy single-buildStep path below is untouched for the other ports.
            if (BuildSteps != null && BuildSteps.Count > 0)
            {
                foreach (var bs in BuildSteps)
                {
                    if (bs == null || string.IsNullOrWhiteSpace(bs.Step)) continue;

                    // [yabo-launcher fork] "rsdk-activate": for RSDKv4 engines (Sonic 1/2 2013 decomp). The engine
                    // has NO per-game CLI flag and NO --rom path — it ALWAYS loads a single data pack named
                    // bs.Target (default "Data.rsdk") from next to the exe, and auto-detects Sonic 1 vs Sonic 2
                    // from the GameConfig title INSIDE that pack. So selecting a game = swapping which per-game
                    // .rsdk is the active Data.rsdk. The picked game's data file arrives via LaunchArgs (set by
                    // `--run … --rom <Sonic1.rsdk|Sonic2.rsdk>`); we copy it onto bs.Target. With no explicit
                    // pick we activate whichever single per-game .rsdk the user provided ("bring what you own").
                    // Source-agnostic, so it runs BEFORE the bs.Source presence check below.
                    if (string.Equals(bs.Step, "rsdk-activate", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!RunRsdkActivate(installDir, bs.Target)) return false;
                        continue;
                    }

                    var srcName = bs.Source;
                    if (string.IsNullOrWhiteSpace(srcName))
                    {
                        Log.Warn($"'{Name}': buildSteps entry '{bs.Step}' has no source — skipping.");
                        continue;
                    }
                    var srcPath = Path.Combine(installDir, srcName);
                    if (!File.Exists(srcPath))
                    {
                        // Source ROM not provided — this game just stays locked. Not an error.
                        Log.Info($"'{Name}': buildSteps '{bs.Step}' source '{srcName}' not present — skipping (game locked until provided).");
                        continue;
                    }

                    bool ok;
                    if (string.Equals(bs.Step, "smas-extract", StringComparison.OrdinalIgnoreCase))
                        ok = RunSmasExtract(installDir, srcPath);
                    else if (string.Equals(bs.Step, "bps-patch", StringComparison.OrdinalIgnoreCase))
                        ok = RunBpsPatch(installDir, bs.Patch, srcName, bs.Target);
                    else { Log.Warn($"'{Name}': unknown buildSteps step '{bs.Step}' — skipping."); ok = true; }

                    if (!ok) return false;
                }
                return true;
            }

            if (string.IsNullOrWhiteSpace(BuildStep)) return true;

            // [yabo-launcher fork] "tcc-compile": for ports with no prebuilt release that bake ROM-derived
            // assets into the exe at build time (snesrev/sm). Compile locally with the user's ROM using a
            // self-contained portable TCC + SDL2 (no user toolchain). BuildSource = placed ROM (sm.smc),
            // BuildTarget = exe to produce (sm.exe), BuildSourceUrl = repo source archive zip.
            if (string.Equals(BuildStep, "tcc-compile", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(BuildSource) || string.IsNullOrWhiteSpace(BuildTarget) || string.IsNullOrWhiteSpace(BuildSourceUrl))
                {
                    Log.Warn($"'{Name}': tcc-compile build step missing source/target/sourceUrl — skipping.");
                    return true;
                }

                var exe = Path.Combine(installDir, BuildTarget);
                if (File.Exists(exe)) return true; // already built

                var romPath = Path.Combine(installDir, BuildSource);
                if (!File.Exists(romPath)) { Log.Warn($"'{Name}': ROM not found for compile: {romPath} (provide the ROM first)"); return false; }

                try
                {
                    Log.Info($"'{Name}': compiling '{BuildTarget}' from source with ROM '{BuildSource}' (tcc-compile)...");
                    Services.CompileBuildService.Compile(installDir, BuildSourceUrl!, BuildSource!, BuildTarget!, Name ?? "app");
                    Log.Info($"'{Name}': tcc-compile done — '{BuildTarget}' ready.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"'{Name}': tcc-compile failed: {ex.Message}");
                    try { if (File.Exists(exe)) File.Delete(exe); } catch { }
                    return false;
                }
            }

            // [yabo-launcher fork] "smas-extract": native replacement for qurious-pixel/SMAS_Launcher. Run on
            // the snesrev/smw prebuilt engine (smw.exe). Two outputs are needed:
            //   1) smb1.sfc + smbll.sfc — zstd-decompressed from bundled frames using the placed SMAS ROM
            //      (BuildExtractSource, e.g. smas.sfc) as the dictionary. smw.exe takes one of these as its
            //      ROM argument to run SMB1 / Lost Levels.
            //   2) smw_assets.dat — produced by the SAME bps-patch logic below (BuildSource = smw.sfc), which
            //      smw.exe ALWAYS loads regardless of which SMB ROM it runs.
            // So we extract here, then fall through into the bps-patch path for smw_assets.dat.
            if (string.Equals(BuildStep, "smas-extract", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(BuildExtractSource))
                {
                    Log.Warn($"'{Name}': smas-extract build step missing buildExtractSource — skipping.");
                    return true;
                }
                var smasRom = Path.Combine(installDir, BuildExtractSource);
                if (!File.Exists(smasRom)) { Log.Warn($"'{Name}': SMAS ROM not found for extract: {smasRom} (provide the ROM first)"); return false; }
                if (!RunSmasExtract(installDir, smasRom)) return false;
                // Fall through to bps-patch (smw_assets.dat from smw.sfc). If the catalog didn't declare a
                // bps target, we're done after the extract.
                if (string.IsNullOrWhiteSpace(BuildPatch) || string.IsNullOrWhiteSpace(BuildSource) || string.IsNullOrWhiteSpace(BuildTarget))
                    return true;
            }
            else if (!string.Equals(BuildStep, "bps-patch", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"'{Name}': unknown buildStep '{BuildStep}' — skipping.");
                return true;
            }
            if (string.IsNullOrWhiteSpace(BuildPatch) || string.IsNullOrWhiteSpace(BuildSource) || string.IsNullOrWhiteSpace(BuildTarget))
            {
                Log.Warn($"'{Name}': bps-patch build step missing patch/source/target — skipping.");
                return true;
            }

            var source = Path.Combine(installDir, BuildSource);
            if (!File.Exists(source)) { Log.Warn($"'{Name}': ROM not found for asset build: {source} (provide the ROM first)"); return false; }
            return RunBpsPatch(installDir, BuildPatch, BuildSource, BuildTarget);
        }

        /// <summary>[yabo-launcher fork] Shared "smas-extract" implementation used by BOTH the legacy single
        /// buildStep and the new buildSteps array. Extracts smb1.sfc + smbll.sfc from the placed SMAS ROM.
        /// <paramref name="smasRomPath"/> is the full path to the SMAS dictionary ROM (already verified present
        /// by the caller). Returns false on failure (wrong ROM / hash mismatch / missing payload).</summary>
        private bool RunSmasExtract(string installDir, string smasRomPath)
        {
            try
            {
                Log.Info($"'{Name}': extracting SMB1 + Lost Levels from '{Path.GetFileName(smasRomPath)}' (smas-extract)...");
                Services.SmasExtractService.Extract(installDir, smasRomPath, Name ?? "app");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"'{Name}': smas-extract failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>[yabo-launcher fork] Shared "bps-patch" implementation used by BOTH the legacy single
        /// buildStep and the new buildSteps array. Applies <paramref name="patchName"/> to
        /// <paramref name="sourceName"/> producing <paramref name="targetName"/> (all relative to the install
        /// dir). Idempotent (no-ops if the target already exists). Returns false on failure.</summary>
        private bool RunBpsPatch(string installDir, string? patchName, string? sourceName, string? targetName)
        {
            if (string.IsNullOrWhiteSpace(patchName) || string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(targetName))
            {
                Log.Warn($"'{Name}': bps-patch step missing patch/source/target — skipping.");
                return true;
            }

            var target = Path.Combine(installDir, targetName);
            if (File.Exists(target)) return true; // already built

            var patch = Path.Combine(installDir, patchName);
            var source = Path.Combine(installDir, sourceName);
            if (!File.Exists(patch)) { Log.Warn($"'{Name}': BPS patch not found: {patch}"); return false; }
            if (!File.Exists(source)) { Log.Warn($"'{Name}': ROM not found for asset build: {source} (provide the ROM first)"); return false; }

            try
            {
                Log.Info($"'{Name}': building '{targetName}' from '{sourceName}' via BPS patch '{patchName}'...");
                Services.BpsPatchService.Apply(source, patch, target);
                Log.Info($"'{Name}': built '{targetName}' ({new FileInfo(target).Length} bytes).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"'{Name}': BPS asset build failed: {ex.Message}");
                try { if (File.Exists(target)) File.Delete(target); } catch { }
                return false;
            }
        }

        /// <summary>[yabo-launcher fork] "rsdk-activate" implementation for RSDKv4 ports (Sonic 1/2 2013 decomp).
        /// The engine always loads one pack named <paramref name="targetName"/> (default "Data.rsdk") from next
        /// to the exe and picks Sonic 1 vs Sonic 2 from the GameConfig title inside it — there is no game-select
        /// flag. So we ACTIVATE the chosen game by copying its per-game .rsdk onto the target name:
        ///   • If <see cref="LaunchArgs"/> names a per-game .rsdk (set by the game picker via `--rom`), copy THAT.
        ///   • Otherwise, if exactly one declared data file is present in the install dir, activate it.
        ///   • If the target already equals the chosen source (same length+content sentinel via length+name), skip.
        /// Returns true on success or when there's simply nothing to activate yet (data not provided — game stays
        /// locked, not an error). Returns false only on a real copy failure.</summary>
        private bool RunRsdkActivate(string installDir, string? targetName)
        {
            var target = string.IsNullOrWhiteSpace(targetName) ? "Data.rsdk" : targetName!;
            var targetPath = Path.Combine(installDir, target);

            // Resolve the source per-game pack to activate.
            string? sourceName = null;
            if (!string.IsNullOrWhiteSpace(LaunchArgs))
            {
                // The picker passes the per-game .rsdk via --rom → LaunchArgs (may be several whitespace tokens;
                // take the first that looks like a data file present in the install dir).
                foreach (var tok in LaunchArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var p = Path.Combine(installDir, Path.GetFileName(tok));
                    if (File.Exists(p)) { sourceName = Path.GetFileName(tok); break; }
                }
                // LaunchArgs is the engine command line elsewhere; for RSDKv4 the engine ignores file args, so we
                // CONSUME it here (clear it) — the activation IS the selection, no arg should reach the exe.
                LaunchArgs = null;
            }
            if (sourceName == null)
            {
                // No explicit pick — activate the single provided per-game pack, if exactly one is present.
                var present = (DataFiles ?? new List<DataFileNeed>())
                    .Where(d => !string.IsNullOrWhiteSpace(d.Name) &&
                                !string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(Path.Combine(installDir, d.Name!)))
                    .Select(d => d.Name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (present.Count == 1) sourceName = present[0];
                else if (present.Count == 0)
                {
                    // Nothing provided yet. If the target already exists (user dropped Data.rsdk directly), keep it.
                    if (File.Exists(targetPath)) return true;
                    Log.Info($"'{Name}': rsdk-activate — no data pack provided yet (game stays locked until you supply one).");
                    return true;
                }
                else
                {
                    // Multiple provided but no pick (e.g. GUI direct-launch with both games staged). Leave any
                    // existing Data.rsdk in place; the picker (--rom) selects which to activate.
                    if (File.Exists(targetPath)) return true;
                    Log.Info($"'{Name}': rsdk-activate — multiple packs present and no game picked; leaving target unset. Use the game picker.");
                    return true;
                }
            }

            var sourcePath = Path.Combine(installDir, sourceName);
            try
            {
                // Skip if target already is this source (cheap length compare — packs differ greatly in size).
                if (File.Exists(targetPath) &&
                    new FileInfo(targetPath).Length == new FileInfo(sourcePath).Length)
                {
                    // Same size — assume already active (avoids a multi-hundred-MB copy on every launch).
                    return true;
                }
                Log.Info($"'{Name}': rsdk-activate — copying '{sourceName}' → '{target}' (selecting that game).");
                File.Copy(sourcePath, targetPath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"'{Name}': rsdk-activate failed copying '{sourceName}' → '{target}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Data files (ROMs/IWADs/etc.) this port needs the user to supply.
        /// Empty for ports with no declared data needs. Populated from apps.json.
        /// </summary>
        public List<DataFileNeed> DataFiles { get; set; } = new List<DataFileNeed>();

        public bool HasDataNeeds => DataFiles != null && DataFiles.Count > 0;

        /// <summary>[yabo-launcher fork] True when this card sources its game DATA from the user's Steam copy
        /// (apps.json ingest:"steam-data"). Used to gate the Steam-first/IA-fallback data staging on install even
        /// for steam-data cards that declare neither dataFiles nor a contentUrl (e.g. Duck Game Rebuilt).</summary>
        public bool IsSteamData => string.Equals(Ingest, "steam-data", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// [yabo-launcher fork] Optional list of OS platforms this port ships builds
        /// for (e.g. ["windows","linux","macos"]). Informational / used to scope the
        /// per-app platform selector. Backward-compatible: an empty list means
        /// "unknown / all platforms" and imposes no restriction. Populated from the
        /// optional apps.json "platforms" key and round-tripped on save.
        /// </summary>
        public List<string> Platforms { get; set; } = new List<string>();

        /// <summary>
        /// [yabo-launcher fork] Optional regex matched (case-insensitive) against asset
        /// file names to disambiguate when a release ships several assets for the same
        /// platform (e.g. DoomLauncher's portable zip vs its MSI "_install" zip). When
        /// set, the downloadable-asset list is narrowed to matching names; if exactly one
        /// survives the user is never prompted. A pattern that matches nothing is ignored
        /// (we fall back to the unfiltered list) so a bad/stale regex can't block a
        /// download. Populated from the optional apps.json "assetPattern" key.
        /// </summary>
        public string? AssetPattern { get; set; }

        /// <summary>
        /// [yabo-launcher fork] Optional direct-download URL template for tools whose binaries are NOT
        /// hosted on GitHub releases (e.g. Doomseeker → doomseeker.drdteam.org), while still tracking
        /// the GitHub repo's latest TAG for the version. `{version}` is replaced with the latest tag
        /// (leading 'v' stripped). When set, the launcher downloads this URL instead of a GitHub asset;
        /// extraction/update logic is unchanged. Example:
        /// "https://doomseeker.drdteam.org/files/doomseeker-{version}_windows.zip".
        /// </summary>
        public string? DownloadUrlTemplate { get; set; }

        /// <summary>Builds a direct-download URL from <see cref="DownloadUrlTemplate"/> for the given tag.</summary>
        public string BuildDirectDownloadUrl(string? tag)
        {
            var version = (tag ?? string.Empty).TrimStart('v', 'V');
            return (DownloadUrlTemplate ?? string.Empty)
                .Replace("{version}", version)
                .Replace("{tag}", tag ?? string.Empty);
        }

        /// <summary>
        /// True if <paramref name="assetName"/> should be kept given an optional
        /// <paramref name="pattern"/>. A null/empty pattern keeps everything; an invalid
        /// regex also keeps everything (never hard-fails a download).
        /// </summary>
        public static bool AssetMatchesPattern(string? assetName, string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return true;
            if (string.IsNullOrEmpty(assetName)) return false;
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    assetName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch
            {
                return true; // bad regex → don't filter
            }
        }

        private string? _customIconPath { get; set; }
        public string? CustomIconPath
        {
            get => _customIconPath;
            set
            {
                if (_customIconPath != value)
                {
                    _customIconPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IconUrl));
                    OnPropertyChanged(nameof(HasCustomIcon));
                }
            }
        }
        private string? _cachedDefaultIconPath;
        public bool HasCustomIcon => !string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath);

        public string IconUrl
        {
            get
            {
                // custom cover image
                if (!string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath))
                {
                    return CustomIconPath;
                }

                // Cached default icon
                if (!string.IsNullOrEmpty(_cachedDefaultIconPath) && File.Exists(_cachedDefaultIconPath))
                {
                    return _cachedDefaultIconPath;
                }

                // Direct URL (will download)
                return DefaultIconUrl;
            }
        }

        public bool HasStoredExecutable
        {
            get
            {
                if (string.IsNullOrEmpty(FolderName) || GameManager == null)
                    return false;

                try
                {
                    var gamePath = GetInstallPath(GameManager.GamesFolder);
                    if (string.IsNullOrWhiteSpace(gamePath))
                        return false;

                    var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");
                    return File.Exists(selectedExePath);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// [yabo-launcher fork] READ-ONLY check: does this port's actual launch executable exist on disk?
        /// This is the robust install signal — a port is only truly "installed" when something LAUNCHABLE is
        /// present, not merely when its folder exists. Mirrors how <see cref="LaunchAsync"/> resolves the exe
        /// (a previously remembered selected_executable.txt, else exe candidates at the top level, else any in a
        /// subfolder) but WITHOUT mutating anything: it does NOT call EnsureExecutableAtRoot (which moves files)
        /// and does NOT write/delete any file. Pure inspection.
        ///
        /// Fixes both failure modes of the old "directory exists → installed" test:
        ///   • FALSE POSITIVE: an empty / data-only / partially-downloaded folder (e.g. a single-exe port like
        ///     PvZ-Portable or a STALKER build whose download was cancelled) has NO launch exe → not installed.
        ///   • FALSE NEGATIVE: an OLD install (recomp ports — Star Fox 64 / Majora's Mask) whose exe sits at the
        ///     root with its data is correctly recognized: its .exe is a candidate, so it stays Installed.
        /// </summary>
        private bool HasLaunchExecutable(string gamePath)
        {
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                return false;

            try
            {
                // A remembered, still-present user selection is a definitive launchable exe.
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");
                if (File.Exists(selectedExePath))
                {
                    var saved = File.ReadAllText(selectedExePath)?.Trim();
                    if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
                        return true;
                    // A stale pointer (saved exe gone) is NOT proof of an install — fall through and scan.
                }

                // Same candidate search the launch path uses: top level first, then any subfolder. READ-ONLY —
                // unlike launch we deliberately skip EnsureExecutableAtRoot so a status refresh never moves files.
                var candidates = GameInstallationService.FindExecutableCandidates(
                    gamePath, SearchOption.TopDirectoryOnly, GetInstallationOptions(), out _);
                if (candidates.Count == 0)
                {
                    candidates = GameInstallationService.FindExecutableCandidates(
                        gamePath, SearchOption.AllDirectories, GetInstallationOptions(), out _);
                }
                return candidates.Count > 0;
            }
            catch
            {
                // Never let a status probe throw — but on error treat as "no exe found" so we don't fabricate an
                // install. (A genuinely installed port re-resolves fine on the next refresh.)
                return false;
            }
        }

        /// <summary>[yabo-launcher fork] Apply a resolved cover-art URL (e.g. from SteamGridDB) and refresh
        /// the bound image. No-op if empty or unchanged.</summary>
        public void ApplyResolvedIcon(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || url == GameIconUrl) return;
            GameIconUrl = url;
            // The disc/avatar may already have been cached to _cachedDefaultIconPath at load time, and the
            // IconUrl getter returns that BEFORE the URL — clear it so the new cover art actually shows.
            _cachedDefaultIconPath = null;
            OnPropertyChanged(nameof(IconUrl));
            OnPropertyChanged(nameof(DefaultIconUrl));
        }

        public string DefaultIconUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(GameIconUrl))
                    return GameIconUrl;

                // [yabo-launcher fork] No curated icon → use the GitHub owner's avatar (every repo has
                // one) so ports show a real logo instead of the generic disc. Repo-less (external)
                // entries keep the disc.
                if (!string.IsNullOrWhiteSpace(Repository) && Repository.Contains('/'))
                {
                    var owner = Repository.Split('/')[0];
                    if (!string.IsNullOrWhiteSpace(owner))
                        return $"https://github.com/{owner}.png?size=128";
                }

                return "/Assets/DefaultGame.png";
            }
        }

        private List<string>? _availableExecutables;
        public List<string>? AvailableExecutables
        {
            get => _availableExecutables;
            set
            {
                if (_availableExecutables != value)
                {
                    _availableExecutables = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(HasMultipleExecutables));
                    DispatchPropertyChanged(nameof(HasExecutableChoice));
                    DispatchPropertyChanged(nameof(CanLaunchOptions));
                }
            }
        }

        private string? _selectedExecutable;
        public string? SelectedExecutable
        {
            get => _selectedExecutable;
            set
            {
                if (_selectedExecutable != value)
                {
                    _selectedExecutable = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public bool HasMultipleExecutables => AvailableExecutables?.Count > 1;
        public bool HasExecutableChoice
        {
            get
            {
                if (!IsInstalled || string.IsNullOrWhiteSpace(FolderName) || GameManager == null)
                    return false;

                if (HasMultipleExecutables)
                    return true;

                try
                {
                    var gamePath = GetInstallPath(GameManager.GamesFolder);
                    if (!Directory.Exists(gamePath))
                        return false;

                    var executables = GameInstallationService.FindExecutableCandidates(
                        gamePath,
                        SearchOption.TopDirectoryOnly,
                        GetInstallationOptions(),
                        out _);
                    if (executables.Count <= 1)
                    {
                        executables = GameInstallationService.FindExecutableCandidates(
                            gamePath,
                            SearchOption.AllDirectories,
                            GetInstallationOptions(),
                            out _);
                    }

                    return executables.Count > 1;
                }
                catch
                {
                    return false;
                }
            }
        }

        private List<GitHubAsset>? _availableDownloads;
        public List<GitHubAsset>? AvailableDownloads
        {
            get => _availableDownloads;
            set
            {
                if (_availableDownloads != value)
                {
                    _availableDownloads = value;
                    DispatchPropertyChanged();
                }
            }
        }

        private GitHubAsset? _selectedDownload;
        public GitHubAsset? SelectedDownload
        {
            get => _selectedDownload;
            set
            {
                if (_selectedDownload != value)
                {
                    _selectedDownload = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public bool HasMultipleDownloads => AvailableDownloads?.Count > 1;

        public bool IsInstalled
        {
            get
            {
                return Status == GameStatus.Installed ||
                       Status == GameStatus.UpdateAvailable;
            }
        }

        public bool CanLaunch => Status == GameStatus.Installed;
        // External link-outs aren't downloaded/located by us — hide those menu items for them.
        public bool CanDownload => Status == GameStatus.NotInstalled && !IsExternal;
        public bool CanLocateInstall => Status == GameStatus.NotInstalled && !IsExternal;

        // [yabo-launcher fork] Local exe the user pointed us at for an external/link-out entry (loaded from
        // settings.ExternalLaunchPaths). When set and the file still exists, the tile LAUNCHES it instead of
        // opening the download page.
        private string? _externalExePath;
        public string? ExternalExePath
        {
            get => _externalExePath;
            set
            {
                if (_externalExePath != value)
                {
                    _externalExePath = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(ExternalLaunchable));
                    DispatchPropertyChanged(nameof(ButtonText));
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }
        public bool ExternalLaunchable =>
            IsExternal && !string.IsNullOrWhiteSpace(ExternalExePath) && File.Exists(ExternalExePath!);
        public bool CanUpdate => Status == GameStatus.UpdateAvailable;
        public bool CanSkipUpdate => Status == GameStatus.UpdateAvailable;
        public bool CanChangeVersion => IsInstalled && !string.IsNullOrWhiteSpace(Repository);
        public bool CanVersionOptions => CanSkipUpdate || CanChangeVersion || IsInstalled;
        public bool CanLaunchOptions => HasExecutableChoice || IsInstalled;
        public bool CanInfoOptions => !string.IsNullOrWhiteSpace(Repository);
        public bool HasPreferredVersion => !string.IsNullOrWhiteSpace(PreferredVersion);

        public string? LatestVersion
        {
            get => _latestVersion;
            set
            {
                if (_latestVersion != value)
                {
                    _latestVersion = value;

                    if (AreVersionsEquivalent(_preferredVersion, _latestVersion))
                    {
                        _preferredVersion = null;
                        DispatchPropertyChanged(nameof(PreferredVersion));
                        DispatchPropertyChanged(nameof(HasPreferredVersion));
                    }

                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string? InstalledVersion
        {
            get => _installedVersion;
            set
            {
                if (_installedVersion != value)
                {
                    _installedVersion = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string? PreferredVersion
        {
            get => _preferredVersion;
            set
            {
                if (AreVersionsEquivalent(value, LatestVersion))
                {
                    value = null;
                }

                if (_preferredVersion != value)
                {
                    _preferredVersion = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(HasPreferredVersion));
                }
            }
        }

        public string? SkippedUpdateVersion
        {
            get => _skippedUpdateVersion;
            set
            {
                if (_skippedUpdateVersion != value)
                {
                    _skippedUpdateVersion = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }

        public GameStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(ButtonText));
                    DispatchPropertyChanged(nameof(StatusText));
                    DispatchPropertyChanged(nameof(IsInstalled));
                    DispatchPropertyChanged(nameof(CanLaunch));
                    DispatchPropertyChanged(nameof(CanDownload));
                    DispatchPropertyChanged(nameof(CanLocateInstall));
                    DispatchPropertyChanged(nameof(CanUpdate));
                    DispatchPropertyChanged(nameof(CanSkipUpdate));
                    DispatchPropertyChanged(nameof(CanChangeVersion));
                    DispatchPropertyChanged(nameof(CanVersionOptions));
                    DispatchPropertyChanged(nameof(HasExecutableChoice));
                    DispatchPropertyChanged(nameof(CanLaunchOptions));
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public string ButtonText
        {
            get
            {
                if (IsExternal)
                    return ExternalLaunchable ? "Launch" : "Get it →";
                return Status switch
                {
                    GameStatus.NotInstalled => "Download",
                    GameStatus.Installed => "Launch",
                    GameStatus.UpdateAvailable => "Update",
                    GameStatus.Downloading => "Downloading...",
                    GameStatus.Installing => "Installing...",
                    _ => "Download"
                };
            }
        }

        public string StatusText
        {
            get
            {
                if (IsExternal)
                    return ExternalLaunchable ? "Ready · external (you manage updates)" : "Link-out · install it yourself";

                if (Status == GameStatus.Installed && !string.IsNullOrEmpty(InstalledVersion))
                    return $"Installed: {InstalledVersion}";

                if (Status == GameStatus.UpdateAvailable && !string.IsNullOrEmpty(LatestVersion))
                    return $"Update available!: {InstalledVersion} -> {LatestVersion}";
                return Status switch
                {
                    GameStatus.NotInstalled => "Not installed",
                    GameStatus.Downloading => "Downloading...",
                    GameStatus.Installing => "Installing...",
                    _ => ""
                };
            }
        }

        private double _downloadProgress;
        public double DownloadProgress
        {
            get => _downloadProgress;
            set
            {
                if (_downloadProgress != value)
                {
                    _downloadProgress = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(IsDownloading));
                }
            }
        }

        public bool IsDownloading => Status == GameStatus.Downloading || Status == GameStatus.Installing || Status == GameStatus.Updating;

        public void SetGameManager(GameManager gameManager)
        {
            GameManager = gameManager;
            DispatchPropertyChanged(nameof(HasExecutableChoice));
            DispatchPropertyChanged(nameof(CanLaunchOptions));
        }

        private void DispatchPropertyChanged([CallerMemberName] string propertyName = "")
        {
            // [yabo-launcher fork] CLI-only engine — no UI thread to marshal onto; raise the event directly.
            OnPropertyChanged(propertyName);
        }

        static Task ShowMessageBoxAsync(string message, string title)
        {
            // [yabo-launcher fork] CLI-only engine — surface errors to the console (and the log) for the Playnite
            // plugin to relay. The old Avalonia message-box dialog was removed with the desktop GUI.
            Log.Warn($"{title}: {message}");
            Console.WriteLine();
            try { Console.ForegroundColor = ConsoleColor.Red; } catch { }
            Console.WriteLine($"ERROR: {title}");
            try { Console.ResetColor(); } catch { }
            Console.WriteLine(message);
            Console.WriteLine();
            return Task.CompletedTask;
        }

        public async Task CheckStatusAsync(HttpClient httpClient, string gamesFolder, bool forceUpdateCheck = false)
        {
            // External entries (Zandronum/Doomseeker, GTA decomps) have no portable GitHub build — don't
            // poll a release we don't manage. They render as a link-out, not an install target.
            if (IsExternal)
            {
                Status = GameStatus.NotInstalled;
                IsLoading = false;
                return;
            }

            if (string.IsNullOrEmpty(FolderName))
            {
                System.Diagnostics.Debug.WriteLine($"Warning: FolderName is null or empty for game {Name}");
                Status = GameStatus.NotInstalled;
                return;
            }

            IsLoading = true;

            try
            {
                var gamePath = GetInstallPath(gamesFolder);
                var versionFile = Path.Combine(gamePath, "version.txt");

                // [yabo-launcher fork] ROBUST install detection: a port counts as installed ONLY when its actual
                // launch executable is present on disk — not merely when the folder exists. The old check tested
                // directory existence alone, which produced BOTH failure directions:
                //   • False POSITIVE — a single-exe port (PvZ-Portable, STALKER) whose download was cancelled, or
                //     any empty/data-only folder, left a directory behind (and CheckStatusAsync then FABRICATED a
                //     version.txt via EnsureInstalledVersionFileAsync), so it showed as Installed with no game.
                //   • False NEGATIVE — handled here too: an OLD install with its exe at the root is recognized
                //     because HasLaunchExecutable finds the .exe (it does not depend on any newer marker file).
                // HasLaunchExecutable is read-only (it never moves/writes/deletes anything), so this only affects
                // the computed Status/tint — install files and saves are untouched.
                bool directoryExists = Directory.Exists(gamePath) && HasLaunchExecutable(gamePath);
                bool versionFileExists = File.Exists(versionFile);

                bool isInstalled = false;
                if (directoryExists)
                {
                    if (versionFileExists)
                    {
                        try
                        {
                            InstalledVersion = (await File.ReadAllTextAsync(versionFile).ConfigureAwait(false))?.Trim();

                            if (string.IsNullOrWhiteSpace(InstalledVersion))
                            {
                                InstalledVersion = await EnsureInstalledVersionFileAsync(versionFile).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            InstalledVersion = null;
                        }

                        Status = GameStatus.Installed;
                        isInstalled = true;
                    }
                    else
                    {
                        Status = GameStatus.Installed;
                        InstalledVersion = await EnsureInstalledVersionFileAsync(versionFile).ConfigureAwait(false);
                        isInstalled = true;
                    }
                }
                else
                {
                    Status = GameStatus.NotInstalled;
                    InstalledVersion = "";
                }

                // Different update check logic for installed vs not-installed games
                if (forceUpdateCheck)
                {
                    // Force check - always check
                    await CheckLatestVersionAsync(httpClient, forceCheck: true).ConfigureAwait(false);
                }
                else if (isInstalled)
                {
                    // Installed games: check if needs update (more frequent - every 6 hours by default)
                    if (GitHubApiCache.NeedsUpdateCheck(Repository ?? string.Empty, isInstalledGame: true))
                    {
                        await CheckLatestVersionAsync(httpClient).ConfigureAwait(false);
                    }
                    else if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache != null)
                    {
                        // Use cached data
                        LatestVersion = cache.Version;
                        _cachedRelease = cache.CachedRelease;
                    }
                }
                else
                {
                    // Not-installed games: check less frequently (once per day)
                    if (GitHubApiCache.NeedsUpdateCheck(Repository ?? string.Empty, isInstalledGame: false))
                    {
                        await CheckLatestVersionAsync(httpClient).ConfigureAwait(false);
                    }
                    else if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache != null)
                    {
                        // Use cached data
                        LatestVersion = cache.Version;
                        _cachedRelease = cache.CachedRelease;
                    }
                }

                if (isInstalled && string.IsNullOrWhiteSpace(InstalledVersion))
                {
                    InstalledVersion = string.IsNullOrWhiteSpace(LatestVersion)
                        ? "Unknown"
                        : DefaultInstalledVersion;
                }

                if (isInstalled)
                {
                    RefreshInstalledStatus();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking status for {Name}: {ex.Message}");
                Status = GameStatus.NotInstalled;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static async Task<string?> EnsureInstalledVersionFileAsync(string versionFile)
        {
            try
            {
                var versionDirectory = Path.GetDirectoryName(versionFile);
                if (!string.IsNullOrEmpty(versionDirectory))
                {
                    Directory.CreateDirectory(versionDirectory);
                }

                await File.WriteAllTextAsync(versionFile, DefaultInstalledVersion).ConfigureAwait(false);
                return DefaultInstalledVersion;
            }
            catch
            {
                return null;
            }
        }

        public string GetInstallPath(string gamesFolder)
        {
            if (!string.IsNullOrWhiteSpace(InstallPath))
                return InstallPath;

            return string.IsNullOrWhiteSpace(FolderName)
                ? string.Empty
                : Path.Combine(gamesFolder, FolderName);
        }

        /// <summary>
        /// [yabo-launcher fork] Resolve the directory that CONTAINS this port's launchable
        /// executable, mirroring how LaunchAsync locates the exe. Several ports extract their
        /// exe into a versioned subfolder (e.g. devilutionx\, dsda-doom-0.29.4-win-x64\), and the
        /// engine looks for its data NEXT TO its own exe — so data must be staged there, not at the
        /// install root. This is what ProvideToPort uses as the placement base.
        ///
        /// Resolution (version-proof, no hardcoded folder names):
        ///   1. Flatten a lone wrapper folder if present (EnsureExecutableAtRoot), same as launch.
        ///   2. Find exe candidates at the top level; if none, search all subdirectories — exactly
        ///      what FindExecutableCandidates does at launch.
        ///   3. Prefer the catalog's declared executableName when set, else the first candidate.
        /// Returns the install root unchanged when the exe is already at the root (the ~22 working
        /// ports) or when no exe can be located (caller falls back safely).
        /// </summary>
        public string ResolveExecutableDir(string installPath)
        {
            if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                return installPath;

            try
            {
                // Mirror launch: flatten a single wrapper subfolder so a top-level exe is found.
                GameInstallationService.EnsureExecutableAtRoot(installPath, GetInstallationOptions());

                var candidates = GameInstallationService.FindExecutableCandidates(
                    installPath, SearchOption.TopDirectoryOnly, GetInstallationOptions(), out _);

                if (candidates.Count == 0)
                {
                    candidates = GameInstallationService.FindExecutableCandidates(
                        installPath, SearchOption.AllDirectories, GetInstallationOptions(), out _);
                }

                if (candidates.Count == 0)
                    return installPath; // no exe located — fall back to root, unchanged behavior.

                string? chosen = null;

                // Prefer the catalog-declared executableName (match by file name) when present.
                if (!string.IsNullOrWhiteSpace(ExecutableName))
                {
                    chosen = candidates.FirstOrDefault(c =>
                        string.Equals(Path.GetFileName(c), ExecutableName, StringComparison.OrdinalIgnoreCase));
                }

                chosen ??= candidates[0];

                var dir = Path.GetDirectoryName(chosen);
                return string.IsNullOrWhiteSpace(dir) ? installPath : dir;
            }
            catch
            {
                return installPath; // any failure → safe fallback to the install root.
            }
        }

        private static string NormalizeVersionString(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return "0.0.0";

            var normalized = version.Trim().TrimStart('v', 'V');
            var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();

            while (segments.Count < 3)
            {
                segments.Add("0");
            }

            return string.Join(".", segments.Take(4));
        }

        private static bool IsNewerVersion(string candidateVersion, string baselineVersion)
        {
            try
            {
                var candidate = new Version(NormalizeVersionString(candidateVersion));
                var baseline = new Version(NormalizeVersionString(baselineVersion));
                return candidate.CompareTo(baseline) > 0;
            }
            catch
            {
                return !candidateVersion.TrimStart('v', 'V').Equals(
                    baselineVersion.TrimStart('v', 'V'),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool AreVersionsEquivalent(string? firstVersion, string? secondVersion)
        {
            if (string.IsNullOrWhiteSpace(firstVersion) || string.IsNullOrWhiteSpace(secondVersion))
                return false;

            try
            {
                return new Version(NormalizeVersionString(firstVersion))
                    .Equals(new Version(NormalizeVersionString(secondVersion)));
            }
            catch
            {
                return firstVersion.TrimStart('v', 'V').Trim()
                    .Equals(secondVersion.TrimStart('v', 'V').Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool ShouldSuggestUpdate()
        {
            if (string.IsNullOrWhiteSpace(LatestVersion) ||
                string.IsNullOrWhiteSpace(InstalledVersion) ||
                InstalledVersion == "Unknown")
            {
                return false;
            }

            if (!IsNewerVersion(LatestVersion, InstalledVersion))
                return false;

            if (!string.IsNullOrWhiteSpace(SkippedUpdateVersion) &&
                !IsNewerVersion(LatestVersion, SkippedUpdateVersion))
            {
                return false;
            }

            return true;
        }

        private void RefreshInstalledStatus()
        {
            if (ShouldSuggestUpdate())
            {
                Status = GameStatus.UpdateAvailable;
            }
            else if (Status != GameStatus.Downloading && Status != GameStatus.Installing && !string.IsNullOrWhiteSpace(InstalledVersion))
            {
                Status = GameStatus.Installed;
            }
        }

        public void SetVersionPreferences(string? preferredVersion, string? skippedUpdateVersion)
        {
            PreferredVersion = preferredVersion;
            SkippedUpdateVersion = skippedUpdateVersion;
            RefreshInstalledStatus();
        }

        public void SkipLatestUpdate()
        {
            if (string.IsNullOrWhiteSpace(LatestVersion))
                return;

            var effectiveInstalledVersion = InstalledVersion;
            if (string.IsNullOrWhiteSpace(effectiveInstalledVersion) ||
                effectiveInstalledVersion == "Unknown" ||
                AreVersionsEquivalent(effectiveInstalledVersion, DefaultInstalledVersion))
            {
                effectiveInstalledVersion = LatestVersion;
            }

            if (!string.IsNullOrWhiteSpace(effectiveInstalledVersion))
            {
                InstalledVersion = effectiveInstalledVersion;
                PreferredVersion = effectiveInstalledVersion;
            }

            SkippedUpdateVersion = LatestVersion;
            RefreshInstalledStatus();
        }

        public async Task ForceUpdateAsync(HttpClient httpClient, string gamesFolder)
        {
            if (string.IsNullOrWhiteSpace(FolderName))
                throw new InvalidOperationException("App configuration is invalid (missing folder name).");

            if (string.IsNullOrWhiteSpace(Repository))
                throw new InvalidOperationException("App configuration is invalid (missing repository).");

            var gamePath = GetInstallPath(gamesFolder);
            if (!Directory.Exists(gamePath))
                throw new DirectoryNotFoundException($"App folder not found: {gamePath}");

            var versionFile = Path.Combine(gamePath, "version.txt");

            IsLoading = true;
            try
            {
                _cachedRelease = null;
                LatestVersion = string.Empty;
                GitHubApiCache.RemoveCache(Repository);

                if (File.Exists(versionFile))
                {
                    File.Delete(versionFile);
                }

                await CheckStatusAsync(httpClient, gamesFolder, forceUpdateCheck: true).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void SetCustomIcon(string sourcePath, string cacheDirectory)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                throw new ArgumentException("Source file does not exist or path is invalid.");

            if (string.IsNullOrEmpty(FolderName))
                throw new InvalidOperationException("FolderName is required for custom icon operations.");

            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            Directory.CreateDirectory(customIconsDir);

            var extension = Path.GetExtension(sourcePath);
            var fileName = $"{FolderName}_custom{extension}";
            var destinationPath = Path.Combine(customIconsDir, fileName);

            try
            {
                if (!string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath))
                {
                    ClearImageFromMemory();

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    TryDeleteFileWithRetry(CustomIconPath, maxRetries: 3, delayMs: 100);
                }

                using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(destStream);
                }

                if (File.Exists(destinationPath))
                {
                    var attributes = File.GetAttributes(destinationPath);
                    if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                CustomIconPath = destinationPath;

                OnPropertyChanged(nameof(CustomIconPath));
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(HasCustomIcon));
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to set custom icon: {ex.Message}", ex);
            }
        }

        public void RemoveCustomIcon()
        {
            if (string.IsNullOrEmpty(CustomIconPath))
                return;

            var pathToDelete = CustomIconPath;

            try
            {
                CustomIconPath = "";

                OnPropertyChanged(nameof(CustomIconPath));
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(HasCustomIcon));

                ClearImageFromMemory();

                // [yabo-launcher fork] Defer the file delete to a background task (no UI dispatcher) so the handle
                // held by any in-flight image load is released before we try to delete.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    try
                    {
                        if (File.Exists(pathToDelete))
                        {
                            TryDeleteFileWithRetry(pathToDelete, maxRetries: 5, delayMs: 200);
                        }
                    }
                    catch (Exception deleteEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Warning: Failed to delete custom icon file {pathToDelete}: {deleteEx.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to remove custom icon: {ex.Message}", ex);
            }
        }

        public void LoadCustomIcon(string cacheDirectory)
        {
            if (string.IsNullOrEmpty(FolderName))
                return;

            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            if (!Directory.Exists(customIconsDir))
                return;

            var possibleExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" };
            foreach (var ext in possibleExtensions)
            {
                var fileName = $"{FolderName}_custom{ext}";
                var iconPath = Path.Combine(customIconsDir, fileName);
                if (File.Exists(iconPath))
                {
                    try
                    {
                        var attributes = File.GetAttributes(iconPath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(iconPath, attributes & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to check/modify file attributes for {iconPath}: {ex.Message}");
                    }

                    CustomIconPath = iconPath;
                    break;
                }
            }
        }

        public void SaveSelectedExecutable(string executablePath, string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName) || string.IsNullOrEmpty(executablePath))
                return;

            try
            {
                var gamePath = GetInstallPath(gamesFolder);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");
                File.WriteAllText(selectedExePath, executablePath);
                System.Diagnostics.Debug.WriteLine($"Saved selected executable for {Name}: {executablePath}");
                OnPropertyChanged(nameof(HasStoredExecutable));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save selected executable for {Name}: {ex.Message}");
            }
        }

        public string? LoadSelectedExecutable(string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName))
                return null;

            try
            {
                var gamePath = GetInstallPath(gamesFolder);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");

                if (File.Exists(selectedExePath))
                {
                    var savedPath = File.ReadAllText(selectedExePath).Trim();
                    if (File.Exists(savedPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"Loaded selected executable for {Name}: {savedPath}");
                        return savedPath;
                    }
                    else
                    {
                        // File no longer exists, delete the preference
                        File.Delete(selectedExePath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load selected executable for {Name}: {ex.Message}");
            }

            return null;
        }

        public void ClearSelectedExecutable(string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName))
                return;

            try
            {
                var gamePath = GetInstallPath(gamesFolder);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");

                if (File.Exists(selectedExePath))
                {
                    File.Delete(selectedExePath);
                    System.Diagnostics.Debug.WriteLine($"Cleared selected executable for {Name}");
                }

                SelectedExecutable = null;
                OnPropertyChanged(nameof(HasStoredExecutable));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear selected executable for {Name}: {ex.Message}");
            }
        }

        public async Task LoadAndCacheDefaultIconAsync(string cacheDirectory)
        {
            if (string.IsNullOrEmpty(FolderName))
                return;

            try
            {
                var iconsDir = Path.Combine(cacheDirectory, "Icons");
                Directory.CreateDirectory(iconsDir);

                var defaultUrl = DefaultIconUrl;

                // Only cache if it's an actual URL (not a local asset path)
                if (!defaultUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !defaultUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Create a safe filename from the URL
                var urlHash = GetUrlHash(defaultUrl);
                var extension = Path.GetExtension(defaultUrl);

                // If no extension in URL or it's too long, default to .png
                if (string.IsNullOrEmpty(extension) || extension.Length > 5 || extension.Contains('?'))
                    extension = ".png";

                var cachedIconPath = Path.Combine(iconsDir, $"{FolderName}_{urlHash}{extension}");

                // If cached icon exists and is valid, use it
                if (File.Exists(cachedIconPath))
                {
                    try
                    {
                        // Verify the file is valid by checking its size
                        var fileInfo = new FileInfo(cachedIconPath);
                        if (fileInfo.Length > 0)
                        {
                            _cachedDefaultIconPath = cachedIconPath;
                            OnPropertyChanged(nameof(IconUrl));
                            System.Diagnostics.Debug.WriteLine($"Using cached icon for {Name}: {cachedIconPath}");
                            return;
                        }
                    }
                    catch
                    {
                        // If file is corrupted, delete it and re-download
                        try { File.Delete(cachedIconPath); } catch { }
                    }
                }

                // Download icon if not cached
                System.Diagnostics.Debug.WriteLine($"Downloading icon for {Name} from {defaultUrl}");

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Github-Launcher/1.0");

                var iconData = await httpClient.GetByteArrayAsync(defaultUrl);

                // Save to cache
                await File.WriteAllBytesAsync(cachedIconPath, iconData);
                _cachedDefaultIconPath = cachedIconPath;
                OnPropertyChanged(nameof(IconUrl));
                System.Diagnostics.Debug.WriteLine($"Icon cached for {Name}: {cachedIconPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cache icon for {Name}: {ex.Message}");
                // Fallback to direct URL
            }
        }

        private static string GetUrlHash(string url)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            return Convert.ToHexString(hashBytes).Substring(0, 16).ToLowerInvariant();
        }

        private void ClearImageFromMemory()
        {
            // [yabo-launcher fork] CLI-only engine — no UI dispatcher; raise the change notifications directly.
            OnPropertyChanged(nameof(IconUrl));
            OnPropertyChanged(nameof(HasCustomIcon));
        }

        private static void TryDeleteFileWithRetry(string filePath, int maxRetries = 5, int delayMs = 200)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        var attributes = File.GetAttributes(filePath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(filePath, FileAttributes.Normal);
                        }

                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        File.Delete(filePath);
                        System.Diagnostics.Debug.WriteLine($"Successfully deleted file: {filePath}");
                        return;
                    }
                    else
                    {
                        return;
                    }
                }
                catch (IOException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1}/{maxRetries} failed to delete {filePath}: {ex.Message}");
                    System.Threading.Thread.Sleep(delayMs * (i + 1));

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1}/{maxRetries} - Access denied for {filePath}: {ex.Message}");

                    try
                    {
                        File.SetAttributes(filePath, FileAttributes.Normal);
                    }
                    catch { }

                    System.Threading.Thread.Sleep(delayMs * (i + 1));
                }
            }

            System.Diagnostics.Debug.WriteLine($"Unable to delete file after {maxRetries} attempts: {filePath}. File may be in use.");
        }

        private async Task CheckLatestVersionAsync(HttpClient httpClient)
        {
            await CheckLatestVersionAsync(httpClient, forceCheck: false);
        }

        private async Task CheckLatestVersionAsync(HttpClient httpClient, bool forceCheck)
        {
            if (string.IsNullOrEmpty(Repository))
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Repository is null or empty for game {Name}");
                return;
            }

            try
            {
                if (!forceCheck && !GitHubApiCache.NeedsUpdateCheck(Repository))
                {
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var cachedData) && cachedData != null)
                    {
                        LatestVersion = cachedData.Version;
                        _cachedRelease = cachedData.CachedRelease;
                        RefreshInstalledStatus();
                    }
                    return;
                }

                var result = await GitHubReleaseService.FetchReleasesAsync(
                    httpClient,
                    Repository,
                    GetGitHubApiToken(),
                    GitHubApiCache.GetETag(Repository)).ConfigureAwait(false);

                if (result.IsNotModified)
                {
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var existingCache) && existingCache != null)
                    {
                        LatestVersion = existingCache.Version;
                        _cachedRelease = existingCache.CachedRelease;
                        GitHubApiCache.SetCache(Repository, existingCache.Version, existingCache.ETag, existingCache.CachedRelease);
                        RefreshInstalledStatus();
                    }
                    return;
                }

                var latestRelease = result.Releases.FirstOrDefault();
                if (latestRelease != null && !string.IsNullOrWhiteSpace(latestRelease.tag_name))
                {
                    LatestVersion = latestRelease.tag_name;
                    _cachedRelease = latestRelease;
                    GitHubApiCache.SetCache(Repository, latestRelease.tag_name, result.ETag ?? string.Empty, latestRelease);
                    RefreshInstalledStatus();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"No releases found for {Repository}");
                }
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Network error fetching latest version for {Repository}: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching latest version for {Repository}: {ex.Message}");
            }
        }
        private string GetGitHubApiToken()
        {
            try
            {
                var settings = AppSettings.Load();
                return settings?.GitHubApiToken ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task PerformActionAsync(HttpClient httpClient, string gamesFolder, AppSettings settings)
        {
            if (string.IsNullOrEmpty(FolderName))
            {
                await ShowMessageBoxAsync("App configuration is invalid (missing folder name).", "Configuration Error");
                return;
            }

            string gamePath = GetInstallPath(gamesFolder);

            // [yabo-launcher fork — itch.io route] Free itch.io apps/games (e.g. GMEdit). Resolve the signed
            // download URL via the itch API, then reuse the IA download+extract path (SharpCompress content-sniffs
            // the zip). Needs the user's itch API key; if absent, prompt them to add one and stop — the bootstrap
            // asks for it once, and this is the deferred "prompt on first itch install" fallback.
            if (string.Equals(Ingest, "itch", StringComparison.OrdinalIgnoreCase))
            {
                if (Status == GameStatus.Installed)
                {
                    await LaunchAsync(gamesFolder);
                    return;
                }
                if (!Services.ItchInstallService.HasKey(settings))
                {
                    await ShowMessageBoxAsync(
                        $"{Name} is a free download from itch.io, which needs a free itch API key.\n\n" +
                        $"Get one (takes ~30 seconds):\n{Services.ItchInstallService.KeyUrl}\n\n" +
                        "Then run:  yabo-launcher --set-itch-key \"<key>\"  (owner-only — itch isn't part of the public catalog).",
                        "itch.io API key needed");
                    return;
                }
                try
                {
                    Status = GameStatus.Downloading;
                    DownloadProgress = 0;
                    var progress = new Progress<double>(p => DownloadProgress = p);

                    var itchDownloadUrl = await Services.ItchInstallService.ResolveDownloadUrlAsync(
                        httpClient, ItchUrl, ItchGameId, ItchUploadMatch, settings!.ItchApiKey).ConfigureAwait(false);

                    var exes = await Services.InternetArchiveInstallService.InstallAsync(
                        httpClient, itchDownloadUrl, gamePath, Name, progress).ConfigureAwait(false);

                    if (exes.Count == 0)
                    {
                        Log.Warn($"itch install '{Name}': no executable found under {gamePath} after extraction.");
                        await ShowMessageBoxAsync(
                            $"Installed {Name}, but no executable was found in:\n{gamePath}\n\nThe upload may be data-only or structured unexpectedly.",
                            "Executable Not Found");
                    }

                    var versionFile = Path.Combine(gamePath, "version.txt");
                    var versionTag = ItchGameId.HasValue ? $"itch-{ItchGameId.Value}" : "itch";
                    try { await File.WriteAllTextAsync(versionFile, versionTag).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Warn($"itch install '{Name}': couldn't write version.txt: {ex.Message}"); }

                    InstalledVersion = versionTag;
                    Status = GameStatus.Installed;
                    DownloadProgress = 100;
                    Log.Info($"itch install '{Name}': complete -> Installed ({gamePath})");
                }
                catch (Exception ex)
                {
                    Status = GameStatus.NotInstalled;
                    Log.Error($"itch install '{Name}' failed", ex);
                    await ShowMessageBoxAsync(
                        $"Couldn't install {Name} from itch.io:\n{ex.Message}",
                        "Install Failed");
                }
                return;
            }

            // [yabo-launcher fork] Internet-Archive in-app install (RohanKar parity). MUST run BEFORE the
            // IsExternal branch below: IA entries also carry no GitHub repo, but UNLIKE external entries we
            // DO manage them (download + extract + unblock + launch), so they must not fall through to the
            // "not managed by yabo-launcher" link-out dialog. We download the direct archive.org repack via
            // InternetArchiveInstallService (SharpCompress: zip/7z/rar), strip Mark-of-the-Web, find the exe,
            // then hand off to the engine's normal LaunchAsync path (find-exe + ShellExecute) for launch.
            if (string.Equals(Ingest, "internet-archive", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(ContentUrl))
            {
                if (Status == GameStatus.Installed)
                {
                    await LaunchAsync(gamesFolder);
                    return;
                }

                // [adopt-a-repack — task #10] If GitHub has a NEWER binary, use it: overlay the fresh release onto
                // the existing install (the IA data stays put) instead of re-pulling the whole IA pack. Reuses the
                // inherited GithubLauncher update path. First-time install (NotInstalled) still comes from the IA
                // repack below — that's where the data lives.
                if (Status == GameStatus.UpdateAvailable && !string.IsNullOrWhiteSpace(Repository))
                {
                    await DownloadAndInstallAsync(httpClient, gamesFolder, GetLatestRelease(), settings, GameStatus.UpdateAvailable).ConfigureAwait(false);
                    return;
                }

                // NotInstalled → install the IA repack (data + bundled binary).
                try
                {
                    Status = GameStatus.Downloading;
                    DownloadProgress = 0;
                    var progress = new Progress<double>(p => DownloadProgress = p);

                    var exes = await Services.InternetArchiveInstallService.InstallAsync(
                        httpClient, ContentUrl!, gamePath, Name, progress).ConfigureAwait(false);

                    if (exes.Count == 0)
                    {
                        Log.Warn($"IA install '{Name}': no executable found under {gamePath} after extraction.");
                        await ShowMessageBoxAsync(
                            $"Installed {Name}, but no executable was found in:\n{gamePath}\n\nThe archive may be data-only or structured unexpectedly.",
                            "Executable Not Found");
                    }

                    // Synthetic version: IA repacks have no semantic version — record the item identifier (or
                    // "ia") so the entry shows as Installed and re-install/force-update works the same way the
                    // static direct-URL ports do.
                    var versionFile = Path.Combine(gamePath, "version.txt");
                    var versionTag = string.IsNullOrWhiteSpace(IaIdentifier) ? "ia" : IaIdentifier!;
                    try { await File.WriteAllTextAsync(versionFile, versionTag).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Warn($"IA install '{Name}': couldn't write version.txt: {ex.Message}"); }

                    InstalledVersion = versionTag;
                    Status = GameStatus.Installed;
                    DownloadProgress = 100;
                    Log.Info($"IA install '{Name}': complete -> Installed ({gamePath})");
                }
                catch (Exception ex)
                {
                    Status = GameStatus.NotInstalled;
                    Log.Error($"IA install '{Name}' failed", ex);
                    await ShowMessageBoxAsync(
                        $"Couldn't install {Name} from the Internet Archive:\n{ex.Message}",
                        "Install Failed");
                }
                return;
            }

            // [yabo-launcher fork] External (unmanaged) entries: we don't download/own/update these — we
            // just point the user to a good place to grab a port. Open it in their browser (which also
            // passes any anti-bot the CLI can't). Getting + updating it is on the user.
            if (IsExternal)
            {
                // If the user has already installed it and pointed us at the .exe, just LAUNCH it — we don't
                // manage the install, but we can still be the one place they start it from.
                if (!string.IsNullOrWhiteSpace(ExternalExePath) && File.Exists(ExternalExePath!))
                {
                    Log.Info($"External app '{Name}': launching user-set executable {ExternalExePath}");
                    try
                    {
                        Process.Start(new ProcessStartInfo(ExternalExePath!)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(ExternalExePath!) ?? string.Empty
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"External launch failed for '{Name}' ({ExternalExePath})", ex);
                        await ShowMessageBoxAsync(
                            $"Couldn't launch {Name}:\n{ex.Message}\n\n" +
                            $"The file may have moved. Right-click the tile → \"Set launchable .exe…\" to re-point it.",
                            "Launch failed");
                    }
                    return;
                }

                // Not pointed at a local exe yet → link-out: open the download page, explain we don't manage it.
                Log.Info($"External app '{Name}': opening {ExternalUrl} (not managed by yabo-launcher — user installs it)");
                OpenExternalUrl(ExternalUrl!);
                await ShowMessageBoxAsync(
                    $"{Name} isn't managed by yabo-launcher — we don't download, own, or update it. " +
                    $"We're just pointing you to a good place to grab a port; installing and keeping it current is up to you.\n\n" +
                    $"Opening it in your browser now. Once it's installed, right-click the tile → \"Set launchable .exe…\" " +
                    $"and yabo can launch it for you from here.",
                    "Not managed by yabo-launcher");
                return;
            }

            switch (Status)
            {
                case GameStatus.NotInstalled:
                case GameStatus.UpdateAvailable:

                    await DownloadAndInstallAsync(httpClient, gamesFolder, GetLatestRelease(), settings, _status);
                    // [yabo-launcher fork] The data "marriage" (binary + Library/IA data) now lives INSIDE
                    // DownloadAndInstallAsync (before Status=Installed), so it runs for BOTH --download and --play
                    // and the CLI install monitor can't cut it off. See EnsureMarriedDataAsync.
                    break;

                case GameStatus.Installed:

                    await LaunchAsync(gamesFolder);
                    break;
            }
        }

        /// <summary>Opens a URL in the user's browser (for external/unmanaged apps).</summary>
        private static void OpenExternalUrl(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
            }
            catch (Exception ex)
            {
                Log.Error($"OpenExternalUrl failed for {url}", ex);
            }
        }

        private static Task<bool> ShowWineNotFoundWarning()
        {
            // [yabo-launcher fork] CLI-only engine — no interactive dialog. This Linux-only path fires when a
            // Windows-asset port is requested but no Wine/Proton runner was detected. Surface the warning and
            // continue, mirroring a "download anyway" choice (the port simply won't launch until a runner is set).
            Console.WriteLine();
            Console.WriteLine("WARNING: This game requires a Linux Windows-runner to launch, but none was detected.");
            Console.WriteLine("Install Wine/Proton or set a custom command in Settings for Bottles or another launcher.");
            Console.WriteLine("Continuing the download anyway; the game will not launch without a configured runner.");
            Console.WriteLine();
            Log.Warn("Windows-runner not found (Linux) — continuing install without an interactive prompt.");
            return Task.FromResult(true);
        }

        private static Task<bool> ShowWineDownloadWarning()
        {
            // [yabo-launcher fork] CLI-only engine — no interactive dialog. A compatible Wine/Proton runner was
            // detected/configured, so proceed with the download.
            Log.Info("Windows-runner available (Linux) — proceeding with download.");
            return Task.FromResult(true);
        }

        private GitHubRelease? GetLatestRelease()
        {
            return _cachedRelease;
        }

        public async Task<List<GitHubRelease>> FetchReleasesAsync(HttpClient httpClient)
        {
            if (string.IsNullOrWhiteSpace(Repository))
                return [];

            return await GitHubReleaseService.FetchReleasesWithAssetsAsync(
                httpClient,
                Repository,
                GetGitHubApiToken()).ConfigureAwait(false);
        }
        public async Task InstallReleaseAsync(HttpClient httpClient, string gamesFolder, AppSettings settings, GitHubRelease release, GitHubAsset selectedAsset)
        {
            SelectedDownload = selectedAsset;
            await DownloadAndInstallAsync(httpClient, gamesFolder, release, settings, Status);
        }

        private static List<GitHubAsset> GetDownloadableAssets(GitHubRelease release)
        {
            return GitHubReleaseService.GetDownloadableAssets(release);
        }
        /// <summary>[yabo-launcher fork] Install a SPECIFIC release version (the "Change Version" parity command).
        /// Records it as the preferred version so it isn't auto-updated away.</summary>
        public async Task InstallVersionAsync(HttpClient httpClient, string gamesFolder, string tag, AppSettings settings)
        {
            var releases = await FetchReleasesAsync(httpClient).ConfigureAwait(false);
            var rel = releases.FirstOrDefault(r => string.Equals(r.tag_name, tag, StringComparison.OrdinalIgnoreCase));
            if (rel == null) throw new InvalidOperationException($"Version '{tag}' not found for {Name}.");
            PreferredVersion = tag;
            await DownloadAndInstallAsync(httpClient, gamesFolder, rel, settings, GameStatus.UpdateAvailable).ConfigureAwait(false);
        }

        /// <summary>[yabo-launcher fork] Force a fresh download + overwrite (the "Force Update" / manual-update
        /// command). Deletes version.txt first so it re-pulls even when the version matches — required for static
        /// direct-URL ports (synthetic tag is always "latest") and useful for re-grabbing a GitHub build.</summary>
        public async Task ForceUpdateAsync(HttpClient httpClient, string gamesFolder, AppSettings settings)
        {
            try
            {
                var vf = Path.Combine(GetInstallPath(gamesFolder), "version.txt");
                if (File.Exists(vf)) File.Delete(vf);
            }
            catch { /* best-effort */ }
            await DownloadAndInstallAsync(httpClient, gamesFolder, null, settings, GameStatus.UpdateAvailable).ConfigureAwait(false);
        }

        /// <summary>
        /// [yabo-launcher fork] THE MARRIAGE — ensure a port's declared data is in place after its GitHub binary
        /// installs (this is the entire "marry a GitHub release to an IA download" job):
        ///   (1) own-data-first — arm the declared dataFiles from the user's Library (RomLibraryService.ProvideToPort,
        ///       a local copy honoring each file's TargetSubpath; no download if the user owns the game, e.g. Diablo);
        ///   (2) IA fallback — if the data is STILL missing and a contentUrl exists, download + extract the
        ///       Internet-Archive repack into the install dir.
        /// No-op once the required (non-optional) files are present. Best-effort; logs each step so a grep tells the
        /// whole story.
        /// </summary>
        private async Task EnsureMarriedDataAsync(HttpClient httpClient, string gamesFolder, string gamePath, AppSettings settings)
        {
            // [yabo-launcher fork] STEAM-FIRST / IA-FALLBACK for steam-data cards. When a port needs game DATA the
            // user OWNS on Steam (Quake/Quake II paks, RCT2's g1.dat, …), prefer their Steam copy and stage it in
            // place; only fall back to the IA overlay if they don't own it. Precedence:
            //   (a) resolve the card's Steam appid (explicit apps.json steamAppId, else repo->appid map) and, if the
            //       game is installed, COPY each declared dataFile out of the Steam install into the binary's dir;
            //   (b) if NOT owned on Steam, fall through to the IA path below (contentUrl + dataFiles overlay);
            //   (c) if neither resolves (no Steam copy, no IA contentUrl), surface a clear message.
            if (string.Equals(Ingest, "steam-data", StringComparison.OrdinalIgnoreCase))
            {
                var steamSatisfied = TryStageSteamData(gamePath);
                if (steamSatisfied)
                    return; // (a) owned on Steam and staged — done; do NOT also pull IA.
                // (b)/(c): not owned (or nothing to stage) — only the IA overlay can help now.
                if (string.IsNullOrWhiteSpace(ContentUrl))
                {
                    Log.Warn($"'{Name}': steam-data — game not found on Steam and no IA fallback (contentUrl). " +
                             $"Own it on Steam (appid {SteamContentLocator.ResolveSteamDataAppId(SteamAppId, Repository)?.ToString() ?? "?"}) " +
                             "or add an Internet-Archive link to supply its data.");
                    return;
                }
                Log.Info($"'{Name}': steam-data — not owned on Steam; falling back to IA contentUrl for data.");
                // fall through to the IA logic below.
            }

            // [OWNER MODEL 2026-06-02] Marriages ALWAYS pull their data from the IA link — no Library, no own-data.
            // If the port has no contentUrl it's a bare GitHub binary → nothing to do here (the user provides any
            // data; the binary prompts). We never read a managed Library.
            if (string.IsNullOrWhiteSpace(ContentUrl)) return;

            var needs = (DataFiles ?? new List<DataFileNeed>()).Where(d => !string.IsNullOrWhiteSpace(d.Name)).ToList();
            // A 0-byte file is a FAILED IA-member fetch (IA returns an empty body when you request a member from a
            // .7z/.rar it can't extract) — it is NOT "placed", so the whole-archive fallback below still runs.
            bool Placed(string fileName)
            {
                if (!Directory.Exists(gamePath)) return false;
                var hit = Directory.EnumerateFiles(gamePath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                return hit != null && new FileInfo(hit).Length > 0;
            }

            // Declared dataFiles → fetch each one INDIVIDUALLY from the IA zip (contentUrl/<member>) so we grab only
            // the needed members (e.g. Diablo's diabdat + hellfire + hf*.mpq). No dataFiles → the whole content IS
            // the data → extract the full repack. Required-missing → error; optional-missing (Hellfire) → best-effort.
            string DataDir()
            {
                if (Directory.Exists(gamePath))
                {
                    foreach (var d in needs)
                    {
                        var hit = Directory.EnumerateFiles(gamePath, d.Name!, SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return Path.GetDirectoryName(hit)!;
                    }
                    var exe = Directory.EnumerateFiles(gamePath, "*.exe", SearchOption.AllDirectories)
                        .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }).FirstOrDefault();
                    if (exe != null) return Path.GetDirectoryName(exe)!;
                }
                return gamePath;
            }

            if (needs.Count > 0)
            {
                // Each declared file lands at <binaryDir>/<targetSubpath>/<name>. Perfect Dark's ROM is
                // 'pd.ntsc-final.z64' with targetSubpath "data" — the port mounts it from data/, so a flat
                // placement (next to the exe) = "failed to mount". Honor the subpath per file.
                var baseDir = DataDir();
                string DestDirFor(DataFileNeed d)
                {
                    var sub = (d.TargetSubpath ?? string.Empty).Trim().Trim('/', '\\');
                    if (string.IsNullOrEmpty(sub)) return baseDir;
                    // [double-nest guard] If a (rewired) card's subpath repeats the binary dir's own leaf
                    // folder — e.g. baseDir = ...\devilutionx and targetSubpath = "devilutionx" because the
                    // GitHub zip already nests the exe under devilutionx/ — the data belongs BESIDE the exe,
                    // not in ...\devilutionx\devilutionx\. Collapse the repeated leading segment so a card
                    // edited in the console can't silently break the marriage.
                    var leaf = new DirectoryInfo(baseDir).Name;
                    var segs = sub.Split('/', '\\');
                    if (segs.Length > 0 && string.Equals(segs[0], leaf, StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = string.Join(Path.DirectorySeparatorChar.ToString(), segs.Skip(1));
                        return string.IsNullOrEmpty(rest) ? baseDir : Path.Combine(baseDir, rest);
                    }
                    return Path.Combine(baseDir, sub);
                }
                // 0) RAW content file (pattern 1) — the contentUrl IS the data file, not an archive (e.g. Sonic
                //    Mania's Data.rsdk served directly by IA). Download it straight to the matching dataFile's spot.
                if (!Services.InternetArchiveInstallService.LooksLikeArchive(ContentUrl!))
                {
                    string urlName = ""; try { urlName = System.IO.Path.GetFileName(new Uri(ContentUrl!).AbsolutePath); } catch { }
                    var target = needs.FirstOrDefault(d => string.Equals(d.Name, urlName, StringComparison.OrdinalIgnoreCase))
                                 ?? needs.FirstOrDefault(d => !d.Optional) ?? needs[0];
                    if (!Placed(target.Name!))
                    {
                        var destDir = DestDirFor(target);
                        var dest = Path.Combine(destDir, target.Name!);
                        Log.Info($"Marriage '{Name}': contentUrl is a raw file — downloading directly -> {dest}.");
                        var progress0 = new Progress<double>(p => DownloadProgress = p);
                        try { await Services.InternetArchiveInstallService.DownloadFileAsync(httpClient, ContentUrl!, dest, progress0).ConfigureAwait(false); }
                        catch (Exception ex) { Log.Error($"Marriage '{Name}': raw content download failed", ex); }
                    }
                }
                // 1) Per-member fetch — works on IA .zip archives (grabs only what's needed, e.g. Diablo's hf*.mpq).
                foreach (var d in needs)
                {
                    if (Placed(d.Name!)) continue;
                    var destDir = DestDirFor(d);
                    Directory.CreateDirectory(destDir);
                    var dest = Path.Combine(destDir, d.Name!);
                    if (await TryFetchIaMemberAsync(httpClient, ContentUrl!, d.Name!, dest).ConfigureAwait(false))
                        Log.Info($"Marriage '{Name}': fetched IA member '{d.Name}' -> {dest}.");
                }
                // 2) Any REQUIRED file still missing → the content is a .7z/.rar (IA can't serve members from those)
                //    or the member name differs from what's inside. Download + extract the WHOLE archive once
                //    (SharpCompress: zip/7z/rar) — this is how the N64Recomp ROMs land (Banjo .7z, Bomberman, PD).
                //    ExtractMembersAsync now also does a ROM-extension fallback, so the ROM lands even when the
                //    archive's internal name differs from the declared dataFile name.
                var missingReq = needs.Where(d => !d.Optional && !Placed(d.Name!)).ToList();
                if (missingReq.Count > 0 && Services.InternetArchiveInstallService.LooksLikeArchive(ContentUrl!))
                {
                    var progress = new Progress<double>(p => DownloadProgress = p);
                    // Group the still-missing files by their destination dir so each subpath (e.g. Perfect Dark's
                    // data/) gets the right placement from a single archive download per group.
                    foreach (var grp in needs.Where(d => !Placed(d.Name!)).GroupBy(DestDirFor))
                    {
                        Directory.CreateDirectory(grp.Key);
                        var wanted = grp.Select(d => d.Name!).ToList();
                        Log.Info($"Marriage '{Name}': {wanted.Count} file(s) not member-fetchable — selective-extracting from {ContentUrl} -> {grp.Key}.");
                        try
                        {
                            await Services.InternetArchiveInstallService.ExtractMembersAsync(httpClient, ContentUrl!, wanted, grp.Key, Name, progress).ConfigureAwait(false);
                        }
                        catch (Exception ex) { Log.Error($"Marriage '{Name}': selective archive extract failed", ex); }
                    }
                }
                foreach (var d in needs.Where(d => !d.Optional && !Placed(d.Name!)))
                    Log.Error($"Marriage '{Name}': REQUIRED data '{d.Name}' still MISSING after IA fetch + extract.");
            }
            else
            {
                // [marriage-data fix] The IA content here IS the game DATA (no specific dataFiles) — e.g. Raze's
                // data package. It MUST be extracted on a FRESH install. The ONLY time we skip is a genuine
                // adopt-a-repack UPDATE: the data was extracted on a prior install and we're just refreshing the
                // GitHub binary on top. We judge "already extracted" by a SENTINEL the extract drops — NOT by
                // "an .exe exists", which is true the instant the GitHub binary installs, so a fresh
                // uninstall→reinstall SILENTLY SKIPPED the data and the game launched with nothing to mount
                // (the Raze bug). Uninstall removes the folder + sentinel, so a reinstall correctly re-extracts.
                var dataMarker = Path.Combine(gamePath, ".yabo-ia-data");
                if (File.Exists(dataMarker))
                {
                    Log.Info($"Marriage '{Name}': IA data already extracted (sentinel present) — keeping it, NOT re-extracting (binary refreshed from GitHub).");
                }
                else
                {
                    Log.Info($"Marriage '{Name}': no declared dataFiles — extracting full IA content {ContentUrl}.");
                    var progress = new Progress<double>(p => DownloadProgress = p);
                    await Services.InternetArchiveInstallService.InstallAsync(httpClient, ContentUrl!, gamePath, Name, progress).ConfigureAwait(false);
                    try { File.WriteAllText(dataMarker, DateTime.UtcNow.ToString("o")); } catch { /* sentinel best-effort */ }
                }
            }

            foreach (var d in needs)
                Log.Info($"Marriage '{Name}': data '{d.Name}' {(Placed(d.Name!) ? "present" : (d.Optional ? "absent (optional)" : "MISSING (required)"))}.");
        }

        /// <summary>
        /// [yabo-launcher fork] Steam-first DATA staging for a steam-data card: resolve the card's Steam appid
        /// (explicit <see cref="SteamAppId"/> else the repo->appid map in
        /// <see cref="Services.SteamContentLocator.ResolveSteamDataAppId"/>), and if the user OWNS the game on
        /// Steam, COPY each declared dataFile out of the Steam install into this port's install dir (honoring each
        /// file's TargetSubpath — id1\, baseq2\, Data\, …). Idempotent: a file already present (non-zero) is left
        /// as-is. Returns TRUE when the game is owned on Steam AND every REQUIRED (non-optional) dataFile is now in
        /// place (so the caller skips the IA fallback). Returns FALSE when the game isn't owned on Steam, OR when a
        /// required file couldn't be located inside the Steam copy (so the IA fallback gets a chance). Best-effort;
        /// a copy error logs and counts as "not satisfied". A card with NO declared dataFiles but a resolvable,
        /// OWNED appid counts as satisfied (the engine reads Steam in place — e.g. the DoomLauncher/Quake model).
        /// </summary>
        private bool TryStageSteamData(string gamePath)
        {
            var appId = SteamContentLocator.ResolveSteamDataAppId(SteamAppId, Repository);
            if (appId == null)
            {
                Log.Warn($"'{Name}': steam-data — no Steam appid resolved (set apps.json \"steamAppId\"); cannot stage from Steam.");
                return false;
            }

            if (!SteamContentLocator.IsAppInstalled(appId.Value))
            {
                Log.Info($"'{Name}': steam-data — Steam app {appId} not installed/owned; will try IA fallback.");
                return false;
            }

            var needs = (DataFiles ?? new List<DataFileNeed>()).Where(d => !string.IsNullOrWhiteSpace(d.Name)).ToList();
            if (needs.Count == 0)
            {
                // No specific files declared: the engine consumes the Steam install in place (the Doom/Quake
                // DoomLauncher model). Ownership alone satisfies it.
                Log.Info($"'{Name}': steam-data — owned on Steam (app {appId}); no dataFiles to stage (engine reads Steam in place).");
                return true;
            }

            if (!Directory.Exists(gamePath))
            {
                try { Directory.CreateDirectory(gamePath); } catch { }
            }

            bool Placed(string fileName)
            {
                if (!Directory.Exists(gamePath)) return false;
                var hit = Directory.EnumerateFiles(gamePath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                return hit != null && new FileInfo(hit).Length > 0;
            }

            foreach (var d in needs)
            {
                if (Placed(d.Name!)) continue; // idempotent — already staged

                var src = SteamContentLocator.FindSteamDataFile(appId.Value, d.Name!);
                if (src == null)
                {
                    if (d.Optional)
                        Log.Info($"'{Name}': steam-data — optional '{d.Name}' not in Steam copy; skipping.");
                    else
                        Log.Warn($"'{Name}': steam-data — REQUIRED '{d.Name}' not found in Steam app {appId}.");
                    continue;
                }

                var sub = (d.TargetSubpath ?? string.Empty).Trim().Trim('/', '\\');
                var destDir = string.IsNullOrEmpty(sub) ? gamePath : Path.Combine(gamePath, sub);
                var dest = Path.Combine(destDir, d.Name!);
                try
                {
                    Directory.CreateDirectory(destDir);
                    File.Copy(src, dest, overwrite: true);
                    Log.Info($"'{Name}': steam-data — staged '{d.Name}' from Steam ({src}) -> {dest}.");
                }
                catch (Exception ex)
                {
                    Log.Error($"'{Name}': steam-data — failed copying '{d.Name}' from Steam: {ex.Message}");
                }
            }

            // Satisfied only if every REQUIRED file is now present (optional misses are fine).
            var missingReq = needs.Where(d => !d.Optional && !Placed(d.Name!)).ToList();
            if (missingReq.Count > 0)
            {
                Log.Warn($"'{Name}': steam-data — owned on Steam but {missingReq.Count} required file(s) still missing " +
                         $"({string.Join(", ", missingReq.Select(d => d.Name))}); will try IA fallback if available.");
                return false;
            }
            return true;
        }

        /// <summary>[yabo-launcher fork] Download ONE file out of an IA zip via the on-the-fly member endpoint
        /// (contentUrl/&lt;member&gt;), trying the declared case then lower/upper (IA member names are case-sensitive;
        /// the Diablo zip is lowercase). Streams to a .part then renames. Returns false on any failure.</summary>
        private async Task<bool> TryFetchIaMemberAsync(HttpClient httpClient, string contentUrl, string member, string destPath)
        {
            var baseUrl = contentUrl.TrimEnd('/');
            foreach (var candidate in new[] { member, member.ToLowerInvariant(), member.ToUpperInvariant() }.Distinct())
            {
                var url = baseUrl + "/" + Uri.EscapeDataString(candidate);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) continue;
                    var total = resp.Content.Headers.ContentLength ?? 0;
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    var tmp = destPath + ".part";
                    long read = 0;
                    using (var cs = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buf = new byte[1 << 16]; int n;
                        while ((n = await cs.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buf, 0, n).ConfigureAwait(false);
                            read += n;
                            if (total > 0) DownloadProgress = (double)read / total * 100;
                        }
                    }
                    // 0 bytes = IA couldn't serve this member (e.g. a member out of a .7z/.rar it can't extract) — NOT
                    // a real file. Drop it so the whole-archive fallback runs instead of leaving an empty ROM.
                    if (read == 0)
                    {
                        try { File.Delete(tmp); } catch { }
                        Log.Warn($"Marriage '{Name}': IA member '{candidate}' returned 0 bytes (not member-fetchable) — falling back to full archive.");
                        continue;
                    }
                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(tmp, destPath);
                    Log.Info($"Marriage '{Name}': IA member '{candidate}' -> {destPath} ({read / 1024} KB).");
                    return true;
                }
                catch (Exception ex) { Log.Warn($"IA member fetch '{candidate}' failed: {ex.Message}"); }
            }
            return false;
        }

        private async Task DownloadAndInstallAsync(HttpClient httpClient, string gamesFolder, GitHubRelease? latestRelease, AppSettings settings, GameStatus status)
        {
            if (string.IsNullOrEmpty(FolderName))
            {
                await ShowMessageBoxAsync("App configuration is invalid (missing folder name).", "Configuration Error");
                return;
            }

            if (string.IsNullOrEmpty(Repository) && string.IsNullOrWhiteSpace(DownloadUrlTemplate))
            {
                await ShowMessageBoxAsync("App configuration is invalid (missing repository).", "Configuration Error");
                return;
            }

            try
            {
                Status = (status == GameStatus.UpdateAvailable) ? GameStatus.Updating : GameStatus.Downloading;
                DownloadProgress = 0;

                // Determine platform identifier (per-app override → global → Auto)
                string platformIdentifier = GetPlatformIdentifier(settings, this);
                var gamePath = GetInstallPath(gamesFolder);
                var versionFile = Path.Combine(gamePath, "version.txt");

                // Check for a cached release first
                if (latestRelease == null)
                {
                    // [yabo-launcher fork] Static direct-URL port (no GitHub repo, just a downloadUrlTemplate like
                    // rvgl.org/…/rvgl_launcher_win64.zip) — synthesise a release so the URL-template download path
                    // runs without hitting the GitHub API. "update" = re-download via Force Update.
                    if (string.IsNullOrEmpty(Repository) && !string.IsNullOrWhiteSpace(DownloadUrlTemplate))
                    {
                        latestRelease = new GitHubRelease { tag_name = "latest", assets = Array.Empty<GitHubAsset>() };
                    }
                    else if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache?.CachedRelease != null)
                    {
                        latestRelease = cache.CachedRelease;
                    }
                    else
                    {
                        DownloadProgress = 5;
                        var releaseResult = await GitHubReleaseService.FetchReleasesAsync(
                            httpClient,
                            Repository,
                            GetGitHubApiToken()).ConfigureAwait(false);

                        if (releaseResult.Releases.Count == 0)
                        {
                            // [yabo-launcher fork] Source-compile ports (snesrev/sm Super Metroid) have NO GitHub
                            // release — the binary is compiled locally from the user's ROM at PLAY time (the
                            // tcc-compile build step → CompileBuildService, see RunBuildStepIfNeeded). Don't bail:
                            // prepare the install dir + a sentinel version + mark installed, so data-staging can
                            // place the ROM and the build step can produce the exe. (Mirrors the downloadUrlTemplate
                            // special-case above — a no-GitHub-release install mode.)
                            if (string.Equals(BuildStep, "tcc-compile", StringComparison.OrdinalIgnoreCase))
                            {
                                Directory.CreateDirectory(gamePath);
                                await File.WriteAllTextAsync(versionFile, "source").ConfigureAwait(false);
                                InstalledVersion = "source";
                                LatestVersion = "source";
                                Status = GameStatus.Installed;
                                DownloadProgress = 100;
                                Log.Info($"Install '{Name}': source-compile port ({Repository}) — no release expected; install dir prepared, '{BuildTarget}' builds at play time from the ROM.");
                                return;
                            }

                            Log.Warn($"Download '{Name}': no releases or git tags found for {Repository}.");
                            await ShowMessageBoxAsync($"No releases found for {Name}.", "No Releases");
                            Status = GameStatus.NotInstalled;
                            DownloadProgress = 0;
                            return;
                        }

                        latestRelease = releaseResult.Releases.FirstOrDefault();

                        if (latestRelease == null)
                        {
                            await ShowMessageBoxAsync($"No valid releases found for {Name}.", "No Releases");
                            Status = GameStatus.NotInstalled;
                            DownloadProgress = 0;
                            return;
                        }

                        GitHubApiCache.SetCache(Repository, latestRelease.tag_name, releaseResult.ETag ?? string.Empty, latestRelease);
                    }
                }

                DownloadProgress = 10;

                // Check if the installed version is already the latest
                if (File.Exists(versionFile))
                {
                    var existingVersion = (await File.ReadAllTextAsync(versionFile).ConfigureAwait(false))?.Trim();
                    if (existingVersion == latestRelease.tag_name)
                    {
                        // [yabo-launcher fork] Binary already current — but still run the DATA marriage so a
                        // binary-only install (or owned data not yet armed) self-heals on a re-install, no need to
                        // uninstall first. No-op once the data's present.
                        if (HasDataNeeds || !string.IsNullOrWhiteSpace(ContentUrl) || IsSteamData)
                        {
                            try { await EnsureMarriedDataAsync(httpClient, gamesFolder, gamePath, settings).ConfigureAwait(false); }
                            catch (Exception marryEx) { Log.Error($"Marriage data step '{Name}' (already-installed) failed", marryEx); }
                        }
                        Status = GameStatus.Installed;
                        InstalledVersion = existingVersion;
                        LatestVersion = latestRelease.tag_name;
                        DownloadProgress = 0;
                        return;
                    }
                }

                // Get all available assets
                var availableAssets = GetDownloadableAssets(latestRelease);

                // [yabo-launcher fork] Direct-URL ports (binaries hosted off-GitHub, e.g. Doomseeker):
                // ignore GitHub assets and synthesise a single asset from the URL template, using the
                // resolved tag as {version}. The rest of the flow (download → extract) is unchanged.
                if (!string.IsNullOrWhiteSpace(DownloadUrlTemplate))
                {
                    var directUrl = BuildDirectDownloadUrl(latestRelease.tag_name);
                    var fileName = Path.GetFileName(new Uri(directUrl).LocalPath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = $"{FolderName}.zip";
                    availableAssets = new List<GitHubAsset>
                    {
                        new GitHubAsset { name = fileName, browser_download_url = directUrl }
                    };
                }

                // [yabo-launcher fork] The AssetPattern match (win64) IS the recommended asset, and the
                // recommended asset auto-downloads — so narrowing to it here keeps the primary Download
                // action frictionless ("recommended = the auto download"). This isn't hiding the others:
                // the Versions picker still lists ALL release binaries and flags the recommended one, so
                // the UI stays transparent and the user can override. A pattern that matches nothing is
                // ignored (fall back to the full list) so a bad/stale regex can't block a download.
                if (!string.IsNullOrWhiteSpace(AssetPattern))
                {
                    var narrowed = availableAssets.Where(a => AssetMatchesPattern(a.name, AssetPattern)).ToList();
                    if (narrowed.Count > 0)
                        availableAssets = narrowed;
                }

                if (availableAssets.Count == 0)
                {
                    Log.Warn($"Download '{Name}': no matching asset in release (assetPattern={AssetPattern ?? "none"}, downloadUrlTemplate={(string.IsNullOrEmpty(DownloadUrlTemplate) ? "none" : DownloadUrlTemplate)}).");
                    await ShowMessageBoxAsync($"No download files found for {Name}.", "No Assets");
                    Status = GameStatus.NotInstalled;
                    DownloadProgress = 0;
                    return;
                }

                // Store available downloads for potential UI display
                AvailableDownloads = availableAssets;

                GitHubAsset? asset = null;

                // If multiple downloads and no selection made, trigger selection UI
                if (availableAssets.Count > 1 && SelectedDownload == null)
                {
                    Log.Info($"Download '{Name}': {availableAssets.Count} assets match — awaiting user selection in the GUI (none auto-picked).");
                    // Signal to UI that selection is needed
                    OnPropertyChanged(nameof(HasMultipleDownloads));
                    OnPropertyChanged(nameof(AvailableDownloads));
                    Status = GameStatus.NotInstalled;
                    DownloadProgress = 0;
                    return;
                }

                // Use selected download or default to first one
                asset = SelectedDownload ?? availableAssets[0];

                // Check if Wine/Proton is needed on Linux
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Check if the selected asset is a Windows file
                    bool isWindowsFile = PlatformAssetMatcher.IsWindowsAsset(asset.name);

                    if (isWindowsFile)
                    {
                        if (!IsWindowsRunnerAvailable(settings))
                        {
                            bool shouldContinueAnyway = await ShowWineNotFoundWarning();
                            if (!shouldContinueAnyway)
                            {
                                Status = GameStatus.NotInstalled;
                                DownloadProgress = 0;
                                return;
                            }
                        }
                        else
                        {
                            bool shouldContinue = await ShowWineDownloadWarning();
                            if (!shouldContinue)
                            {
                                Status = GameStatus.NotInstalled;
                                DownloadProgress = 0;
                                return;
                            }
                        }
                    }
                }

                // Download the asset
                var downloadPath = Path.Combine(Path.GetTempPath(), asset.name);
                Log.Info($"Downloading '{Name}' {latestRelease.tag_name}: '{asset.name}' <- {asset.browser_download_url}");

                try
                {
                    // [resumable downloads] Route the GitHub-release asset download through the SAME resumable
                    // downloader the IA path uses (HTTP Range + a <dest>.ck "bytesSoFar;totalSize" checkpoint +
                    // periodic flush). A cancelled/crashed/network-dropped download now resumes from the saved
                    // offset on the next attempt instead of restarting the whole asset from byte 0. Progress is
                    // mapped from the helper's 0-100 onto this path's existing 10%→90% download band.
                    var dlProgress = new Progress<double>(pct => DownloadProgress = 10 + (pct / 100.0 * 80));
                    await InternetArchiveInstallService.DownloadResumableAsync(
                        httpClient, asset.browser_download_url, downloadPath, dlProgress).ConfigureAwait(false);

                    DownloadProgress = 90;

                    // Install or update the game
                    Status = GameStatus.Installing;
                    DownloadProgress = 95;

                    Log.Info($"Installing '{Name}' {latestRelease.tag_name} -> {gamePath} (asset '{asset.name}' from {asset.browser_download_url})");
                    await InstallOrUpdateGame(downloadPath, gamePath, asset.name, latestRelease.tag_name);
                    Log.Info($"Installed '{Name}' {latestRelease.tag_name} at {gamePath}");

                    // [yabo-launcher fork] THE MARRIAGE — a GitHub-binary port that also declares dataFiles and/or an
                    // IA contentUrl must end up with its DATA, not just the binary. Runs HERE (binary on disk, status
                    // still Installing) so the --download monitor — which returns the instant Status==Installed —
                    // can't cut it off. Own-data-first: arm from the user's Library; IA download only if not owned.
                    if (HasDataNeeds || !string.IsNullOrWhiteSpace(ContentUrl) || IsSteamData)
                    {
                        try { await EnsureMarriedDataAsync(httpClient, gamesFolder, gamePath, settings).ConfigureAwait(false); }
                        catch (Exception marryEx) { Log.Error($"Marriage data step '{Name}' failed", marryEx); }
                    }

                    DownloadProgress = 100;
                    await Task.Delay(500); // Brief pause to show completion

                    // Update status
                    InstalledVersion = latestRelease.tag_name;
                    if (string.IsNullOrWhiteSpace(LatestVersion) || IsNewerVersion(latestRelease.tag_name, LatestVersion))
                    {
                        LatestVersion = latestRelease.tag_name;
                    }
                    Status = GameStatus.Installed;
                    DownloadProgress = 0;
                    SelectedDownload = null;
                    AvailableDownloads = null;

                    // [yabo-launcher fork] Post-install category wiring: preconfigure DoomLauncher /
                    // QuakeInjector for the engine we just installed. Best-effort, never fatal.
                    try
                    {
                        CategoryWiring.OnAppInstalled(this, gamesFolder);
                    }
                    catch (Exception wiringEx)
                    {
                        Debug.WriteLine($"[CategoryWiring] post-install wiring failed for {Name}: {wiringEx.Message}");
                    }

                    // [yabo-launcher fork] Companion installs — "one staged card = the whole experience". A card may
                    // declare other cards' folderNames in apps.json "companions"; after IT finishes installing we pull
                    // those in too (the Doom 1 + 2 card uses this to auto-install its 3 source-port engines). Best-effort
                    // and never fatal: a companion failure must not fail the primary install.
                    try
                    {
                        await InstallCompanionsAsync(httpClient, gamesFolder, settings).ConfigureAwait(false);
                    }
                    catch (Exception companionEx)
                    {
                        Log.Error($"[Companions] post-install companion stage for '{Name}' failed (non-fatal): {companionEx.Message}");
                    }
                }
                finally
                {
                    // Clean up download file
                    bool wasSingleExecutable = asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                                asset.name.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase);

                    if (!wasSingleExecutable && File.Exists(downloadPath))
                    {
                        try
                        {
                            File.Delete(downloadPath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to delete temp file {downloadPath}: {ex.Message}");
                        }
                    }
                }

                // Refresh game list
                if (GameManager != null)
                {
                    GameManager.OnPropertyChanged(nameof(GameManager.Games));
                }
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"Download '{Name}' FAILED (network/HTTP): {ex.Message}", ex);
                // Check if it's a rate limit error
                if (ex.Message.Contains("403") || ex.Message.ToLower().Contains("rate limit"))
                {
                    await ShowRateLimitErrorAsync();
                }
                else
                {
                    await ShowMessageBoxAsync($"Network error installing {Name}: {ex.Message}\n\nPlease check your internet connection.", "Network Error");
                }
                Status = GameStatus.NotInstalled;
                DownloadProgress = 0;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error($"Install '{Name}' FAILED (permissions): {ex.Message}", ex);
                await ShowMessageBoxAsync($"Permission error installing {Name}: {ex.Message}\n\nPlease check folder permissions.", "Permission Error");
                Status = GameStatus.NotInstalled;
                DownloadProgress = 0;
            }
            catch (Exception ex)
            {
                Log.Error($"Install/download '{Name}' FAILED: {ex.Message}", ex);
                await ShowMessageBoxAsync($"Error installing {Name}: {ex.Message}", "Installation Error");
                Status = GameStatus.NotInstalled;
                DownloadProgress = 0;
            }
        }

        /// <summary>[yabo-launcher fork] Install this card's declared <see cref="Companions"/> (other cards' folderNames)
        /// right after THIS card finished installing — "one staged card = the whole experience". Each companion is
        /// resolved from the catalog by folderName and, if not already installed, installed through the SAME
        /// <see cref="PerformActionAsync"/> path (so it gets its release/version/data/own-wiring). Already-installed
        /// companions are skipped (idempotent re-stage). After the companions land we re-run
        /// <see cref="GithubLauncher.Services.CategoryWiring.WireDoomLauncher"/> once: each engine wires itself on its
        /// own install, but the GLOBAL default-port pin (Doom Retro Tier-1) only takes once Doom Retro is registered,
        /// so a final backfill pass guarantees the default lands no matter which engine installed last. Best-effort:
        /// any single companion failure is logged and skipped, never aborting the primary install. No-ops (and costs
        /// nothing) when the card declares no companions.</summary>
        private async Task InstallCompanionsAsync(HttpClient httpClient, string gamesFolder, AppSettings settings)
        {
            if (Companions == null || Companions.Count == 0)
                return;

            var manager = GameManager;
            if (manager == null)
            {
                Log.Warn($"[Companions] '{Name}' declares companions but has no GameManager; skipping companion installs.");
                return;
            }

            Log.Info($"[Companions] '{Name}' staging {Companions.Count} companion(s): {string.Join(", ", Companions)}");

            bool anyInstalled = false;
            foreach (var companionFolder in Companions)
            {
                if (string.IsNullOrWhiteSpace(companionFolder))
                    continue;

                // Don't pull in ourselves (defensive against a self-referential catalog entry).
                if (string.Equals(companionFolder, FolderName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var companion = manager.FindGameByFolderName(companionFolder);
                if (companion == null)
                {
                    Log.Warn($"[Companions] '{Name}': companion '{companionFolder}' not found in catalog; skipping.");
                    continue;
                }

                try
                {
                    // Refresh status so we don't re-download an already-staged engine. CheckStatusAsync flips
                    // Status to Installed iff the folder + a real launch exe are present (its robust detection).
                    await companion.CheckStatusAsync(httpClient, gamesFolder).ConfigureAwait(false);
                    if (companion.IsInstalled)
                    {
                        Log.Info($"[Companions] '{Name}': companion '{companionFolder}' already installed; skipping.");
                        continue;
                    }

                    Log.Info($"[Companions] '{Name}': installing companion '{companion.Name}' ({companionFolder})...");
                    await companion.PerformActionAsync(httpClient, gamesFolder, settings).ConfigureAwait(false);

                    if (companion.IsInstalled)
                    {
                        anyInstalled = true;
                        Log.Info($"[Companions] '{Name}': companion '{companion.Name}' installed.");
                    }
                    else
                    {
                        Log.Warn($"[Companions] '{Name}': companion '{companion.Name}' did not reach Installed status.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Companions] '{Name}': companion '{companionFolder}' install failed (non-fatal): {ex.Message}");
                }
            }

            // Final wiring backfill so the freshly-installed engines are all registered and the default port
            // (Doom Retro) is pinned — only meaningful once at least one engine landed, but it's idempotent.
            if (anyInstalled)
            {
                try
                {
                    CategoryWiring.WireDoomLauncher(manager, gamesFolder, null);
                }
                catch (Exception ex)
                {
                    Log.Error($"[Companions] '{Name}': post-companion WireDoomLauncher failed (non-fatal): {ex.Message}");
                }
            }
        }

        private static Task ShowRateLimitErrorAsync()
        {
            // [yabo-launcher fork] CLI-only engine — print the rate-limit guidance to the console/log instead of
            // the old Avalonia dialog (with its clickable token link). The Playnite plugin relays this to the user.
            Console.WriteLine();
            try { Console.ForegroundColor = ConsoleColor.Yellow; } catch { }
            Console.WriteLine("ERROR: GitHub API rate limit exceeded.");
            try { Console.ResetColor(); } catch { }
            Console.WriteLine("GitHub limits anonymous requests to 60 per hour (resets one hour after depletion).");
            Console.WriteLine("To avoid this, add a GitHub API token (Settings > githubtoken):");
            Console.WriteLine("  1. Create a token at https://github.com/settings/tokens");
            Console.WriteLine("  2. Click 'Generate new token (classic)'");
            Console.WriteLine("  3. Give it a name (no special permissions needed)");
            Console.WriteLine("  4. Click 'Generate token' at the bottom");
            Console.WriteLine("  5. Copy the token and set it via `--set-setting githubtoken <token>`");
            Console.WriteLine("Do not share your token with anyone.");
            Console.WriteLine();
            Log.Warn("GitHub API rate limit exceeded — advise setting a githubtoken.");
            return Task.CompletedTask;
        }

        public static string? GetPlatformIcon(string assetName)
        {
            var assetNameLower = assetName.ToLowerInvariant();

            // Check for Windows
            if (HasAnyOf(assetNameLower, "windows", "win64", "win32", "win-x64", "win-x86", "-win.", "_win.", ".exe", ".msi") ||
                System.Text.RegularExpressions.Regex.IsMatch(assetNameLower, @"[_-]win[_-]|[_-]win\d|^win[_-]"))
            {
                // Exclude false positives
                if (!HasAnyOf(assetNameLower, "linux", "macos", "darwin", ".deb", ".rpm", ".appimage", ".dmg"))
                {
                    return "avares://yabo-launcher/Assets/Icons/platform_win.png";
                }
            }

            // Check for macOS
            if (HasAnyOf(assetNameLower, "macos", "osx", "darwin", ".dmg", ".pkg") ||
                (assetNameLower.Contains("mac") && !assetNameLower.Contains("machin")))
            {
                // Exclude false positives
                if (!HasAnyOf(assetNameLower, "linux", "windows", "win32", "win64", ".exe"))
                {
                    return "avares://yabo-launcher/Assets/Icons/platform_mac.png";
                }
            }

            // Check for Linux
            if (HasAnyOf(assetNameLower, "linux", ".appimage", ".deb", ".rpm", "tar.gz", "tar.xz"))
            {
                // Exclude false positives
                if (!HasAnyOf(assetNameLower, "windows", "win32", "win64", "macos", "osx", "darwin", ".exe", ".dmg"))
                {
                    return "avares://yabo-launcher/Assets/Icons/platform_lin.png";
                }
            }

            return null; // No platform detected
        }

        public static bool MatchesPlatform(string assetName, string platformIdentifier)
        {
            return PlatformAssetMatcher.MatchesPlatform(assetName, platformIdentifier);
        }

        /// <summary>Higher = preferred when several assets match the platform (prefers 64-bit over 32-bit).</summary>
        public static int ArchScore(string assetName)
        {
            return PlatformAssetMatcher.ArchPreferenceScore(assetName);
        }
        private static bool HasAnyOf(string input, params string[] substrings)
        {
            foreach (var substring in substrings)
            {
                if (input.Contains(substring))
                {
                    return true;
                }
            }
            return false;
        }

        static async Task InstallOrUpdateGame(string downloadPath, string gamePath, string assetName, string version)
        {
            await GameInstallationService.InstallOrUpdateGameAsync(
                downloadPath,
                gamePath,
                assetName,
                version,
                GetInstallationOptions()).ConfigureAwait(false);
        }

        static GameInstallationOptions GetInstallationOptions()
        {
            return new GameInstallationOptions
            {
                Log = message => Debug.WriteLine(message)
            };
        }

        internal static void EnsureExecutableAtRoot(string gamePath)
        {
            GameInstallationService.EnsureExecutableAtRoot(gamePath, GetInstallationOptions());
        }

        internal static List<string> GetExecutableCandidates(string gamePath, SearchOption searchOption, out bool needsWine)
        {
            return GameInstallationService.FindExecutableCandidates(
                gamePath,
                searchOption,
                GetInstallationOptions(),
                out needsWine);
        }

        static void SetAttributesNormal(DirectoryInfo dir)
        {
            try
            {
                foreach (var subDir in dir.GetDirectories())
                {
                    SetAttributesNormal(subDir);
                }

                foreach (var file in dir.GetFiles())
                {
                    file.Attributes = FileAttributes.Normal;
                }

                dir.Attributes = FileAttributes.Normal;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Warning setting file attributes: {ex.Message}");
            }
        }

        static void TryDeleteDirectoryIfEmpty(string dir, string stopAt)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;

                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, false);
                    var parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent) &&
                        !Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
                            .Equals(Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar),
                                    StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteDirectoryIfEmpty(parent, stopAt);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed while cleaning up directory '{dir}': {ex.Message}");
            }
        }

        public static string GetPlatformIdentifier(AppSettings settings)
        {
            return PlatformAssetMatcher.GetPlatformIdentifier(settings.Platform);
        }

        /// <summary>
        /// [yabo-launcher fork] Resolves the effective <see cref="TargetOS"/> for a
        /// specific app's release-asset selection: per-app override (if set and not
        /// Auto) wins, otherwise the global <see cref="AppSettings.Platform"/>, which
        /// itself falls back to the running OS when Auto. Backward compatible: a null
        /// app or missing/unparseable override yields the global setting.
        /// </summary>
        public static TargetOS ResolvePlatform(AppSettings settings, GameInfo? app)
        {
            var folder = app?.FolderName;
            if (!string.IsNullOrWhiteSpace(folder) &&
                settings.PlatformOverrides != null &&
                settings.PlatformOverrides.TryGetValue(folder, out var raw) &&
                Enum.TryParse<TargetOS>(raw, ignoreCase: true, out var overridden) &&
                overridden != TargetOS.Auto)
            {
                return overridden;
            }

            return settings.Platform;
        }

        /// <summary>
        /// [yabo-launcher fork] Per-app platform identifier honoring any per-app
        /// override before falling back to the global setting / Auto.
        /// </summary>
        public static string GetPlatformIdentifier(AppSettings settings, GameInfo? app)
        {
            return PlatformAssetMatcher.GetPlatformIdentifier(ResolvePlatform(settings, app));
        }
        /// <summary>[yabo-launcher fork] Logs hard evidence of what the port had to work with at launch —
        /// which declared data files are actually present, and whether a libultraship/N64Recomp ingest
        /// cache (.o2r/.otr) already exists (proof a ROM was ingested). Replaces guessing from a bare
        /// "Launched OK" line. Non-fatal.</summary>
        /// <summary>[yabo-launcher fork] After a port exits, tail any *.log it wrote during this session into
        /// OUR log — closes the gap where launcher-style ports (zelda3/SNES) only write their own crash log
        /// (e.g. "Error extracting resources → see zelda3.log") and we'd otherwise just record "Launched OK".</summary>
        internal static void CapturePortLogs(string installDir, DateTime sinceUtc, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return;
                // [yabo-launcher fork] Broadened per dev/LOG-LOCATIONS.md #2: many ports log to *.txt, not
                // *.log (RSDK Sonic log.txt, Sonic 3 A.I.R. logfile.txt, rottexpr error.txt, Doom Retro
                // crash\*.txt, Exult stdout.txt/stderr.txt, OpenGothic log.txt). Scan both globs recursively,
                // which also covers the crash\ / console\ subfolders the spec calls out.
                var logs = EnumerateRecentLogs(installDir, sinceUtc, new[] { "*.log", "*.txt" });
                foreach (var fi in logs)
                    DumpLogTail(name, "port log", fi);
            }
            catch (Exception ex) { Log.Warn($"CapturePortLogs: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] Per dev/LOG-LOCATIONS.md #3: many FOSS engines write their log
        /// OUTSIDE the install dir (under %APPDATA% / Documents / Saved Games / LocalLow). Sweep that known
        /// set for files freshly written during this play session and dump the tail of the most recent few.</summary>
        internal static void CaptureExternalPortLogs(DateTime sinceUtc, string? name)
        {
            try
            {
                string Appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string Docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string LocalLow = Path.Combine(Profile, "AppData", "LocalLow");
                string SavedGames = Path.Combine(Profile, "Saved Games");

                // Roots to scan (per-family from the spec). Each is scanned recursively for *.log/*.txt/*.html.
                var roots = new[]
                {
                    Path.Combine(Docs, "My Games", "OpenMW"),
                    Path.Combine(Appdata, "OpenRA", "Logs"),
                    Path.Combine(Docs, "OpenRCT2"), Path.Combine(Local, "OpenRCT2"),
                    Path.Combine(Docs, "OpenLoco"),
                    Path.Combine(Docs, "My Games", "vcmi"),
                    Path.Combine(Appdata, "diasurgical", "devilution"),
                    Path.Combine(LocalLow, "Daggerfall Workshop", "Daggerfall Unity"),
                    Path.Combine(Appdata, "CorsixTH"),
                    Path.Combine(SavedGames, "Arx Libertatis"),
                    Path.Combine(Docs, "My Games", "OpenJK"),
                    Path.Combine(Docs, "TheForceEngine", "Logs"),
                    Path.Combine(Profile, ".runelite", "logs"),
                    Path.Combine(Appdata, "supertuxkart"),
                    Path.Combine(Appdata, "0ad", "logs"),
                    Path.Combine(Appdata, "HardLightProductions", "FreeSpaceOpen", "data"),
                    Path.Combine(Docs, "AlephOne", "Logs"),
                    Path.Combine(Appdata, "Return To The Roots", "LOGS"),
                    Path.Combine(Docs, "S.T.A.L.K.E.R.", "logs"),
                    Path.Combine(Appdata, "bibendovsky", "BStone"),
                    Path.Combine(SavedGames, "id Software", "DOOM BFA", "base"),
                    Path.Combine(Appdata, "TwilitRealm", "Dusklight", "logs"),
                    Path.Combine(Appdata, "Sonic3AIR"),
                    Path.Combine(Appdata, "SMB1R", "logs"),
                };

                var hits = new List<FileInfo>();
                foreach (var root in roots)
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        hits.AddRange(EnumerateRecentLogs(root, sinceUtc, new[] { "*.log", "*.txt", "*.html" }));
                    }
                    catch { }
                }
                foreach (var fi in hits.OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
                    DumpLogTail(name, "external log", fi);
            }
            catch (Exception ex) { Log.Warn($"CaptureExternalPortLogs: {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] Synchronously flushes this launch's captured stdout/stderr to the
        /// log, then sweeps the known external log dirs. Called by the headless CLI (`--run`) after the game
        /// session ends — its fire-and-forget exit handler is killed when `--run` returns, so the CLI must
        /// flush these itself. No-op'd parts are cheap; safe to call once after a launch.</summary>
        public void FlushCapturedConsoleAndExternalLogs()
        {
            try
            {
                var since = _lastLaunchUtc == default ? DateTime.UtcNow.AddMinutes(-5) : _lastLaunchUtc;
                if (_lastStdout != null || _lastStderr != null)
                {
                    string stdout = string.Empty, stderr = string.Empty;
                    if (_lastStdout != null) lock (_lastStdout) stdout = _lastStdout.ToString();
                    if (_lastStderr != null) lock (_lastStderr) stderr = _lastStderr.ToString();
                    LogCapturedConsole(Name, "stdout", stdout);
                    LogCapturedConsole(Name, "stderr", stderr);
                    if (stdout.Length == 0 && stderr.Length == 0)
                        Log.Info($"[console] '{Name}' produced no stdout/stderr (GUI-subsystem exe or silent engine).");
                }
                CaptureExternalPortLogs(since, Name);
            }
            catch (Exception ex) { Log.Warn($"FlushCapturedConsoleAndExternalLogs: {ex.Message}"); }
        }

        /// <summary>Finds log files under <paramref name="dir"/> matching any glob, written at/after the
        /// session start, newest first (capped at 3).</summary>
        private static List<FileInfo> EnumerateRecentLogs(string dir, DateTime sinceUtc, string[] globs)
        {
            var found = new List<FileInfo>();
            foreach (var g in globs)
            {
                try
                {
                    found.AddRange(Directory.EnumerateFiles(dir, g, SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(fi => fi.LastWriteTimeUtc >= sinceUtc.AddSeconds(-5)));
                }
                catch { }
            }
            return found
                .GroupBy(fi => fi.FullName, StringComparer.OrdinalIgnoreCase).Select(grp => grp.First())
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(3)
                .ToList();
        }

        /// <summary>Logs the last ~50 lines of a captured log file.</summary>
        private static void DumpLogTail(string? name, string kind, FileInfo fi)
        {
            try
            {
                var all = File.ReadAllLines(fi.FullName);
                var tail = all.Length > 50 ? all[^50..] : all;
                Log.Info($"[{kind}] '{name}' wrote {fi.Name} ({fi.Length} bytes, {fi.FullName}) — last {tail.Length} lines:\n    "
                         + string.Join("\n    ", tail));
            }
            catch (Exception ex) { Log.Warn($"{kind} read '{fi.Name}': {ex.Message}"); }
        }

        /// <summary>[yabo-launcher fork] Logs the tail (~40 lines) of captured child-process console output.</summary>
        private static void LogCapturedConsole(string? name, string stream, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            var lines = content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            var tail = lines.Length > 40 ? lines[^40..] : lines;
            Log.Info($"[console {stream}] '{name}' — last {tail.Length} lines:\n    " + string.Join("\n    ", tail));
        }

        private void LogDataEvidence(string installDir)
        {
            try
            {
                if (DataFiles != null && DataFiles.Count > 0)
                {
                    foreach (var need in DataFiles)
                    {
                        if (string.IsNullOrWhiteSpace(need?.Name)) continue;
                        var sub = (need.TargetSubpath ?? string.Empty)
                            .Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                        var dir = string.IsNullOrWhiteSpace(sub) ? installDir : Path.Combine(installDir, sub);
                        var path = Path.Combine(dir, need.Name!);
                        Log.Info($"  data: '{need.Name}' {(File.Exists(path) ? "present" : "MISSING")} ({path})");
                    }
                }
                if (Directory.Exists(installDir))
                {
                    var caches = Directory.EnumerateFiles(installDir, "*.o2r")
                        .Concat(Directory.EnumerateFiles(installDir, "*.otr"))
                        .Select(Path.GetFileName).Take(3).ToList();
                    if (caches.Count > 0)
                        Log.Info($"  ingest cache present: {string.Join(", ", caches)} (ROM previously ingested)");
                }
                Log.Info($"  data model: ingest={Ingest ?? "(untagged)"}, tested={Tested ?? "(untested)"}");
            }
            catch (Exception ex)
            {
                Log.Warn($"data-evidence logging failed for '{Name}': {ex.Message}");
            }
        }

        /// <summary>[yabo-launcher fork] Logs the shader picture at launch so a grep answers
        /// "what drew the CRT effect?": the port's OWN built-in GLSL shader if its config .ini has an active
        /// (uncommented) Shader= line (the snesrev OpenGL renderer's native shader path).</summary>
        private void LogShaderEvidence(string installDir)
        {
            try
            {
                // The port's own built-in GLSL shader (e.g. smw.ini / zelda3.ini `Shader = ...`).
                if (Directory.Exists(installDir))
                {
                    foreach (var ini in Directory.EnumerateFiles(installDir, "*.ini", SearchOption.AllDirectories).Take(40))
                    {
                        string[] lines;
                        try { lines = File.ReadAllLines(ini); } catch { continue; }
                        foreach (var raw in lines)
                        {
                            var line = raw.TrimStart();
                            // Skip commented lines (# or ; or //) — only ACTIVE assignments count.
                            if (line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("//"))
                                continue;
                            // Match a bare `Shader = <value>` key (not "ShaderXyz"); accept "= " or ":".
                            var m = System.Text.RegularExpressions.Regex.Match(
                                line, @"^Shader\s*[=:]\s*(.+?)\s*$",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (!m.Success) continue;
                            var val = m.Groups[1].Value.Trim().Trim('"');
                            if (string.IsNullOrWhiteSpace(val) ||
                                val.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                                val.Equals("0", StringComparison.OrdinalIgnoreCase))
                                continue;
                            Log.Info($"'{Name}': port built-in GLSL shader = {val} (from {Path.GetFileName(ini)})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"shader-evidence logging failed for '{Name}': {ex.Message}");
            }
        }

        private async Task LaunchAsync(string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName))
            {
                await ShowMessageBoxAsync("Cannot launch game: folder name is not configured.", "Configuration Error");
                return;
            }

            try
            {
                string gamePath = GetInstallPath(gamesFolder);

                if (!Directory.Exists(gamePath))
                {
                    await ShowMessageBoxAsync($"App directory not found: {gamePath}", "Directory Not Found");
                    return;
                }

                // [yabo-launcher fork] Run any declared post-install build step (e.g. zelda3's bps-patch that
                // turns the user's ROM into zelda3_assets.dat) BEFORE we locate/launch the prebuilt exe. The
                // exe loads that data file at startup, so it must be staged first. If the build can't run
                // (ROM/patch missing) we abort with a clear stderr error rather than launching a broken exe.
                if (!RunBuildStepIfNeeded(gamePath))
                {
                    var msg = $"Couldn't build {BuildTarget ?? "the required data file"} for {Name} — provide the ROM first " +
                              $"(--import-data \"{Name}\" <rom>), then launch again.";
                    Console.Error.WriteLine($"ERROR: {msg}");
                    await ShowMessageBoxAsync(msg, "Missing ROM / data");
                    return;
                }

                GameInstallationService.EnsureExecutableAtRoot(gamePath, GetInstallationOptions());

                // Find all available executables
                var executables = GameInstallationService.FindExecutableCandidates(
                    gamePath,
                    SearchOption.TopDirectoryOnly,
                    GetInstallationOptions(),
                    out bool needsWine);

                if (executables.Count == 0)
                {
                    executables = GameInstallationService.FindExecutableCandidates(
                        gamePath,
                        SearchOption.AllDirectories,
                        GetInstallationOptions(),
                        out needsWine);
                }

                if (executables.Count == 0)
                {
                    await ShowMessageBoxAsync(
                        $"No executable found for {Name} in:\n{gamePath}\n\nThe game may not have installed correctly.",
                        "Executable Not Found");
                    return;
                }

                var settings = AppSettings.Load();

                if (needsWine && !IsWindowsRunnerAvailable(settings))
                {
                    await ShowMessageBoxAsync(
                        "Only a Windows executable was found, but no Linux Windows-runner is configured or detected.\n\n" +
                        "Install Wine/Proton or set a custom command in Settings to launch Windows apps.",
                        "Windows Runner Not Found");
                    return;
                }

                // Store executables for potential UI display
                AvailableExecutables = executables;

                string? executablePath = null;

                // Try to load previously selected executable
                if (string.IsNullOrEmpty(SelectedExecutable))
                {
                    SelectedExecutable = LoadSelectedExecutable(gamesFolder);
                }

                // If multiple executables and no valid selection, trigger selection UI
                if (executables.Count > 1 && (string.IsNullOrEmpty(SelectedExecutable) || !executables.Contains(SelectedExecutable)))
                {
                    SelectedExecutable = null; // Reset if saved exe no longer exists
                    // Signal to UI that selection is needed
                    OnPropertyChanged(nameof(HasMultipleExecutables));
                    OnPropertyChanged(nameof(AvailableExecutables));
                    return;
                }

                // Use selected executable or default to first one
                executablePath = !string.IsNullOrEmpty(SelectedExecutable) && executables.Contains(SelectedExecutable)
                    ? SelectedExecutable
                    : executables[0];

                // [per-game override sidecar — gamevault-exec style, no-rebuild like favorite-artists.json]
                // If the game has a .yabo-override.json at its install root, honor a launchExe override here
                // (prefer it over the auto pick). launchArgs/workingDir from the same sidecar are applied in the
                // direct-launch branch below. Absent/malformed → null (load warns once, behaves as today).
                var overrideSidecar = GameOverrideSidecar.TryLoad(gamePath, Name);
                if (overrideSidecar != null && !string.IsNullOrWhiteSpace(overrideSidecar.LaunchExe))
                {
                    var resolvedExe = overrideSidecar.ResolveLaunchExe(gamePath);
                    if (resolvedExe != null)
                    {
                        Log.Info($"'{Name}': override sidecar launchExe '{overrideSidecar.LaunchExe}' -> {resolvedExe}");
                        executablePath = resolvedExe;
                    }
                    else
                    {
                        Log.Warn($"'{Name}': override sidecar launchExe '{overrideSidecar.LaunchExe}' didn't resolve under {gamePath} — using normal pick '{executablePath}'.");
                    }
                }

                // Make executable on Unix systems
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    !executablePath.EndsWith(".app") && !needsWine)
                {
                    await MakeExecutableAsync(executablePath);
                }

                // Launch the game
                var startInfo = new ProcessStartInfo();
                // [yabo-launcher fork] True when we redirect the child's stdout/stderr into our log (the
                // standard Windows direct-launch path). Set in the branch below.
                bool redirectOutput = false;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && executablePath.EndsWith(".app"))
                {
                    startInfo.FileName = "open";
                    startInfo.Arguments = $"\"{executablePath}\"";
                    startInfo.UseShellExecute = false;
                    startInfo.WorkingDirectory = gamePath;
                }
                else if (needsWine && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var runnerCommand = GetWindowsRunnerCommand(settings, executablePath, gamePath);
                    if (runnerCommand == null)
                    {
                        await ShowMessageBoxAsync("A Linux Windows-runner was detected earlier but is no longer available.", "Windows Runner Error");
                        return;
                    }

                    startInfo.UseShellExecute = false;
                    startInfo.WorkingDirectory = gamePath;
                    startInfo.FileName = runnerCommand.FileName;

                    foreach (var argument in runnerCommand.Arguments)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }

                    foreach (var variable in runnerCommand.EnvironmentVariables)
                    {
                        startInfo.Environment[variable.Key] = variable.Value;
                    }
                }
                else
                {
                    startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? gamePath;
                    // [yabo-launcher fork — regression fix vs upstream] A .bat/.cmd target can't be Process.Start'd
                    // directly when UseShellExecute=false (Win32 "not a valid application"); upstream launched them
                    // via the shell (UseShellExecute=true). Route batch files through `cmd.exe /c` so they launch AND
                    // we still capture stdout/stderr. FindExecutableCandidates lists *.bat/*.cmd on Windows, so a port
                    // whose launch target is a batch file would otherwise silently fail to start.
                    bool isBatch = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        && (executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                            || executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
                    if (isBatch)
                    {
                        startInfo.FileName = "cmd.exe";
                        startInfo.ArgumentList.Add("/c");
                        startInfo.ArgumentList.Add(executablePath);
                    }
                    else
                    {
                        startInfo.FileName = executablePath;
                    }
                    // [yabo-launcher fork] Append any catalog-declared launch argument(s) — e.g. the native SMAS
                    // entry passes "smb1.sfc" so the snesrev/smw engine boots that ROM. Split on whitespace into
                    // the ArgumentList so each token is quoted correctly by the runtime.
                    if (!string.IsNullOrWhiteSpace(LaunchArgs))
                        foreach (var a in LaunchArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            startInfo.ArgumentList.Add(a);
                    // [per-game override sidecar — gamevault-exec style, no-rebuild like favorite-artists.json]
                    // Append the sidecar's extra launchArgs after the catalog args (so the user can add flags on
                    // top of the catalog default), and apply a workingDir override if the user set one.
                    if (overrideSidecar != null && !string.IsNullOrWhiteSpace(overrideSidecar.LaunchArgs))
                        foreach (var a in overrideSidecar.LaunchArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            startInfo.ArgumentList.Add(a);
                    var overrideWd = overrideSidecar?.ResolveWorkingDir(gamePath);
                    if (!string.IsNullOrWhiteSpace(overrideWd))
                    {
                        Log.Info($"'{Name}': override sidecar workingDir -> {overrideWd}");
                        startInfo.WorkingDirectory = overrideWd;
                    }
                    // [yabo-launcher fork] Capture the child's stdout/stderr — the biggest log gap, since most
                    // engines write NO log file and only print diagnostics to the console. Redirecting requires
                    // UseShellExecute=false. This is safe for GUI-subsystem exes (they just produce empty pipes)
                    // and the overlay/double-launch guards read MainWindow/MainModule, which work either way.
                    startInfo.UseShellExecute = false;
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                    redirectOutput = true;
                }

                UpdateLastPlayedTime(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && executablePath.EndsWith(".app")
                    ? gamePath
                    : (Path.GetDirectoryName(executablePath) ?? gamePath));

                // [yabo-launcher fork] Point Doom engines at the Steam IWADs in place (no copy). DSDA-Doom,
                // Crispy, GZDoom and Doom Retro all honor DOOMWADDIR / DOOMWADPATH; we set them on OUR
                // process so the engine we spawn inherits them. Without this a standalone Doom engine can't
                // find the IWADs — we'd only wired DoomLauncher's DB, not the engines run directly.
                if (string.Equals(Category, "Doom", StringComparison.OrdinalIgnoreCase))
                {
                    var iwadDirs = GithubLauncher.Services.SteamContentLocator.FindDoomIWadDirs();
                    if (iwadDirs.Count > 0)
                    {
                        Environment.SetEnvironmentVariable("DOOMWADDIR", iwadDirs[0]);
                        Environment.SetEnvironmentVariable("DOOMWADPATH", string.Join(Path.PathSeparator, iwadDirs));
                        Log.Info($"  Doom IWADs -> DOOMWADDIR={iwadDirs[0]}" + (iwadDirs.Count > 1 ? $" (+{iwadDirs.Count - 1} more via DOOMWADPATH)" : ""));
                    }
                    else
                    {
                        Log.Warn($"  '{Name}': no Steam Doom IWADs detected — the engine may not find an IWAD (own Doom on Steam, or drop an IWAD next to the engine exe).");
                    }
                }

                Log.Info($"Launching '{Name}': {startInfo.FileName} (cwd={startInfo.WorkingDirectory})"
                         + (startInfo.ArgumentList.Count > 0 ? $" args=[{string.Join(' ', startInfo.ArgumentList)}]" : (string.IsNullOrEmpty(startInfo.Arguments) ? "" : $" args={startInfo.Arguments}")));
                LogDataEvidence(gamePath);
                // [yabo-launcher fork] Narrate the shader story on EVERY launch so a grep for "shader ="
                // always answers "what drew this?": (a) the overlay preset WE assigned (or "none"), and
                // (b) the port's OWN built-in GLSL shader if its config .ini has an active Shader= line.
                LogShaderEvidence(gamePath);

                // [yabo-launcher fork] Double-launch guard: if this exe is already running, don't spawn a
                // duplicate (an accidental double-click was racing Python provisioning + opening two windows).
                try
                {
                    var runningExe = Path.GetFileNameWithoutExtension(startInfo.FileName);
                    var already = System.Diagnostics.Process.GetProcessesByName(runningExe)
                        .FirstOrDefault(p => { try { return string.Equals(p.MainModule?.FileName, startInfo.FileName, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                    if (already != null && !already.HasExited)
                    {
                        Log.Info($"'{Name}' already running (PID {already.Id}) — skipping duplicate launch.");
                        GameProcessStarted?.Invoke(already);
                        return;
                    }
                }
                catch (Exception ex) { Log.Warn($"double-launch check failed (continuing): {ex.Message}"); }
                // [yabo-launcher fork] Ports whose tooling shells out to `python` get our managed Python ahead
                // of the system one on PATH, so version mismatches (e.g. pillow/PIL) don't break them.
                if (PythonTooling)
                {
                    var pyReady = await PythonRuntimeService.EnsureAsync();
                    if (pyReady != null)
                    {
                        // Setting EnvironmentVariables requires UseShellExecute=false (it was true for the
                        // normal Windows launch). Launching the exe directly is fine. Only do this if Python
                        // actually provisioned, so a Python hiccup can never break the launch itself.
                        startInfo.UseShellExecute = false;
                        PythonRuntimeService.InjectIntoPath(startInfo);
                    }
                }
                // [yabo-launcher fork] Point SDL-based ports at the bundled community controller mapping DB.
                // The bundled SDL 2.26.5's built-in SDL_GameController DB predates modern pads (8BitDo
                // Ultimate 2C = VID 2dc8 / PID 301d, 8BitDo M30, etc.), so SDL_IsGameController() returns
                // false and the game sees no controller even though Windows enumerates the HID device. SDL
                // honors SDL_GAMECONTROLLERCONFIG_FILE (since 2.0.10) — set it on the child so SDL loads the
                // shipped gamecontrollerdb.txt and recognizes the pad. Non-SDL ports ignore the var (harmless).
                // startInfo.Environment requires UseShellExecute=false (the SNES launch already sets it false to
                // capture child stdout). SCOPED TO SNES (the snesrev SDL ports) per owner: the mature ports
                // (libultraship/Doom/Quake) handle controllers natively, so don't feed them an SDL override.
                if (string.Equals(Category, "SNES", StringComparison.OrdinalIgnoreCase))
                try
                {
                    var sdlDb = Path.Combine(AppContext.BaseDirectory, "gamecontrollerdb.txt");
                    if (File.Exists(sdlDb))
                    {
                        startInfo.UseShellExecute = false;
                        startInfo.Environment["SDL_GAMECONTROLLERCONFIG_FILE"] = sdlDb;
                        // XInput/Xbox pads (e.g. an 8BitDo M30 in XInput mode) can mis-report the d-pad through
                        // SDL's RAWINPUT driver — force plain XInput polling. Likely fix for the in-game dpad
                        // up/left miss; harmless to HID pads (the 2C). Confirm via --sniff-gamepad afterward.
                        startInfo.Environment["SDL_JOYSTICK_RAWINPUT"] = "0";
                        Log.Info($"  SDL controller DB -> {sdlDb}");
                    }
                    else
                    {
                        Log.Warn($"  SDL controller DB not found beside exe ({sdlDb}); modern 8BitDo pads may not be recognized by SDL ports.");
                    }
                }
                catch (Exception ex) { Log.Warn($"setting SDL_GAMECONTROLLERCONFIG_FILE failed (continuing): {ex.Message}"); }

                var gameProcess = Process.Start(startInfo);
                Log.Info(gameProcess != null
                    ? $"Launched '{Name}' OK — PID {gameProcess.Id}" + (redirectOutput ? " (capturing stdout/stderr)" : "")
                    : $"Launched '{Name}' — process handle null (shell-exec); assume started");
                // [yabo-launcher fork] Auto-log the launch (exe + args) to the structured action log.
                GithubLauncher.Services.ActionLog.Write("launch", Name, new
                {
                    exe = startInfo.FileName,
                    args = startInfo.ArgumentList.Count > 0
                        ? string.Join(' ', startInfo.ArgumentList)
                        : (startInfo.Arguments ?? ""),
                    pid = gameProcess?.Id ?? -1,
                });
                GameProcessStarted?.Invoke(gameProcess);

                // [yabo-launcher fork] Drain the child's stdout/stderr into a bounded buffer so we can dump
                // the tail to our log on exit — covers the stdout-only majority of engines that write no log
                // file at all (N64Recomp, GZDoom default, Quake family, RSDK Sonic, etc. — see dev/LOG-LOCATIONS.md).
                var stdoutBuf = new StringBuilder();
                var stderrBuf = new StringBuilder();
                _lastStdout = stdoutBuf;
                _lastStderr = stderrBuf;
                _lastLaunchUtc = DateTime.UtcNow;
                if (redirectOutput && gameProcess != null)
                {
                    try
                    {
                        const int CapChars = 64_000; // bound memory; we only ever log the tail anyway
                        gameProcess.OutputDataReceived += (_, e) =>
                        { if (e.Data != null) lock (stdoutBuf) { if (stdoutBuf.Length < CapChars) stdoutBuf.AppendLine(e.Data); } };
                        gameProcess.ErrorDataReceived += (_, e) =>
                        { if (e.Data != null) lock (stderrBuf) { if (stderrBuf.Length < CapChars) stderrBuf.AppendLine(e.Data); } };
                        gameProcess.BeginOutputReadLine();
                        gameProcess.BeginErrorReadLine();
                    }
                    catch (Exception ex) { Log.Warn($"'{Name}': couldn't attach stdout/stderr capture: {ex.Message}"); }
                }

                // [yabo-launcher fork] Close the "log gap": launcher-style ports (zelda3 + the SNES launchers)
                // write their OWN crash log and just pop a "see X.log" dialog. On exit, capture any *.log the
                // port wrote into OURS so failures are visible in yabo-launcher.log without hunting.
                if (gameProcess != null)
                {
                    var capDir = gamePath;
                    var capName = Name;
                    var capSince = DateTime.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        try { await gameProcess.WaitForExitAsync().ConfigureAwait(false); } catch { }
                        try { await Task.Delay(400).ConfigureAwait(false); } catch { }

                        // [yabo-launcher fork] Report the exit and dump the tail of the captured console output.
                        int? exit = null;
                        try { exit = gameProcess.ExitCode; } catch { }
                        Log.Info($"'{capName}' exited" + (exit.HasValue ? $" (code {exit.Value})" : " (exit code unavailable)") + ".");
                        if (redirectOutput)
                        {
                            string stdout, stderr;
                            lock (stdoutBuf) stdout = stdoutBuf.ToString();
                            lock (stderrBuf) stderr = stderrBuf.ToString();
                            LogCapturedConsole(capName, "stdout", stdout);
                            LogCapturedConsole(capName, "stderr", stderr);
                            if (stdout.Length == 0 && stderr.Length == 0)
                                Log.Info($"[console] '{capName}' produced no stdout/stderr (GUI-subsystem exe or silent engine).");
                        }

                        CapturePortLogs(capDir, capSince, capName);
                        // [yabo-launcher fork] Also sweep the known external log dirs (%APPDATA%/Documents/
                        // Saved Games/LocalLow) for FOSS engines that log OUTSIDE the install dir.
                        CaptureExternalPortLogs(capSince, capName);
                    });
                }

                if (GameManager != null)
                {
                    GameManager.OnPropertyChanged(nameof(GameManager.Games));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Launch '{Name}' FAILED", ex);
                await ShowMessageBoxAsync($"Error launching {Name}: {ex.Message}", "Launch Error");
            }
        }

        private async Task MakeExecutableAsync(string executablePath)
        {
            try
            {
                var chmodProcess = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{executablePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(chmodProcess);
                if (process != null)
                {
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0)
                    {
                        string errorOutput = await process.StandardError.ReadToEndAsync();
                        System.Diagnostics.Debug.WriteLine($"chmod failed for {executablePath}: {errorOutput}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to make file executable {executablePath}: {ex.Message}");
            }
        }

        private void UpdateLastPlayedTime(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath))
                return;

            try
            {
                if (!Directory.Exists(gamePath))
                {
                    System.Diagnostics.Debug.WriteLine($"Cannot update LastPlayed: directory does not exist: {gamePath}");
                    return;
                }

                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");
                var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(lastPlayedPath, currentTime);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Permission denied updating LastPlayed.txt for {Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update LastPlayed.txt for {Name}: {ex.Message}");
            }
        }

        private static bool IsWindowsRunnerAvailable(AppSettings? settings = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            if (!string.IsNullOrWhiteSpace(settings?.LinuxWindowsLaunchCommand))
                return true;

            return IsWineOrProtonAvailable();
        }

        private static bool IsWineOrProtonAvailable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            if (IsCommandAvailable("wine") || IsCommandAvailable("wine64"))
                return true;

            foreach (var protonInstallation in GetProtonInstallations())
            {
                if (File.Exists(protonInstallation.ProtonExecutable))
                    return true;
            }

            return false;
        }

        private sealed class RunnerCommandSpec
        {
            public required string FileName { get; init; }
            public required List<string> Arguments { get; init; }
            public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.Ordinal);
        }

        private sealed class ProtonInstallation
        {
            public required string ProtonExecutable { get; init; }
            public required string SteamRoot { get; init; }
        }

        private static readonly Dictionary<string, Func<string, string, string>> RunnerPlaceholderResolvers = new(StringComparer.Ordinal)
        {
            ["{exe}"] = (executablePath, _) => executablePath,
            ["{gamePath}"] = (_, gamePath) => gamePath,
            ["{exeDir}"] = (executablePath, gamePath) => Path.GetDirectoryName(executablePath) ?? gamePath
        };

        private static RunnerCommandSpec BuildWindowsRunnerCommand(string commandTemplate, string executablePath, string gamePath)
        {
            var resolvedCommand = commandTemplate.Trim();

            if (!resolvedCommand.Contains("{exe}", StringComparison.Ordinal) &&
                !resolvedCommand.Contains("{gamePath}", StringComparison.Ordinal) &&
                !resolvedCommand.Contains("{exeDir}", StringComparison.Ordinal))
            {
                resolvedCommand += " {exe}";
            }

            var tokens = SplitRunnerCommand(resolvedCommand);
            if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
            {
                throw new InvalidOperationException("The Linux Windows-runner command is empty.");
            }

            var resolvedTokens = tokens
                .Select(token => ReplaceRunnerPlaceholders(token, executablePath, gamePath))
                .ToList();

            return new RunnerCommandSpec
            {
                FileName = resolvedTokens[0],
                Arguments = resolvedTokens.Skip(1).ToList()
            };
        }

        private static List<string> SplitRunnerCommand(string command)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inSingleQuotes = false;
            bool inDoubleQuotes = false;
            bool escaping = false;

            foreach (var character in command)
            {
                if (escaping)
                {
                    current.Append(character);
                    escaping = false;
                    continue;
                }

                if (character == '\\' && !inSingleQuotes)
                {
                    escaping = true;
                    continue;
                }

                if (character == '"' && !inSingleQuotes)
                {
                    inDoubleQuotes = !inDoubleQuotes;
                    continue;
                }

                if (character == '\'' && !inDoubleQuotes)
                {
                    inSingleQuotes = !inSingleQuotes;
                    continue;
                }

                if (char.IsWhiteSpace(character) && !inSingleQuotes && !inDoubleQuotes)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }

                    continue;
                }

                current.Append(character);
            }

            if (escaping || inSingleQuotes || inDoubleQuotes)
            {
                throw new InvalidOperationException("The Linux Windows-runner command contains an unmatched quote or trailing escape character.");
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }

        private static string ReplaceRunnerPlaceholders(string token, string executablePath, string gamePath)
        {
            var resolvedToken = token;

            foreach (var placeholder in RunnerPlaceholderResolvers)
            {
                if (resolvedToken.Contains(placeholder.Key, StringComparison.Ordinal))
                {
                    resolvedToken = resolvedToken.Replace(
                        placeholder.Key,
                        placeholder.Value(executablePath, gamePath),
                        StringComparison.Ordinal);
                }
            }

            return resolvedToken;
        }

        private static RunnerCommandSpec? GetWindowsRunnerCommand(AppSettings settings, string executablePath, string gamePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return null;

            if (!string.IsNullOrWhiteSpace(settings.LinuxWindowsLaunchCommand))
            {
                return BuildWindowsRunnerCommand(settings.LinuxWindowsLaunchCommand, executablePath, gamePath);
            }

            if (IsCommandAvailable("wine64"))
                return BuildWindowsRunnerCommand("wine64 {exe}", executablePath, gamePath);

            if (IsCommandAvailable("wine"))
                return BuildWindowsRunnerCommand("wine {exe}", executablePath, gamePath);

            foreach (var protonInstallation in GetProtonInstallations())
            {
                if (!File.Exists(protonInstallation.ProtonExecutable))
                    continue;

                var compatDataPath = GetProtonCompatDataPath(gamePath);
                var compatAppId = GetStableCompatAppId(executablePath);
                Directory.CreateDirectory(compatDataPath);

                return new RunnerCommandSpec
                {
                    FileName = protonInstallation.ProtonExecutable,
                    Arguments = ["waitforexitandrun", executablePath],
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = protonInstallation.SteamRoot,
                        ["STEAM_COMPAT_DATA_PATH"] = compatDataPath,
                        ["STEAM_COMPAT_APP_ID"] = compatAppId,
                        ["SteamAppId"] = compatAppId,
                        ["SteamGameId"] = compatAppId
                    }
                };
            }

            return null;
        }

        private static IEnumerable<ProtonInstallation> GetProtonInstallations()
        {
            foreach (var steamRoot in GetSteamRoots())
            {
                var commonPath = Path.Combine(steamRoot, "steamapps", "common");
                foreach (var protonDir in GetExistingDirectories(commonPath, "Proton*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var protonExe = Path.Combine(protonDir, "proton");
                    if (File.Exists(protonExe))
                    {
                        yield return new ProtonInstallation
                        {
                            ProtonExecutable = protonExe,
                            SteamRoot = steamRoot
                        };
                    }
                }

                var compatibilityToolsPath = Path.Combine(steamRoot, "compatibilitytools.d");
                foreach (var protonDir in GetExistingDirectories(compatibilityToolsPath, "*Proton*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var protonExe = Path.Combine(protonDir, "proton");
                    if (File.Exists(protonExe))
                    {
                        yield return new ProtonInstallation
                        {
                            ProtonExecutable = protonExe,
                            SteamRoot = steamRoot
                        };
                    }
                }
            }
        }

        private static IEnumerable<string> GetSteamRoots()
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var steamRoots = new[]
            {
                Path.Combine(homePath, ".steam", "root"),
                Path.Combine(homePath, ".steam", "steam"),
                Path.Combine(homePath, ".local", "share", "Steam"),
                Path.Combine(homePath, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
            };

            return steamRoots
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetExistingDirectories(string parentPath, string searchPattern)
        {
            if (!Directory.Exists(parentPath))
                return [];

            try
            {
                return Directory.GetDirectories(parentPath, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return [];
            }
        }

        private static string GetProtonCompatDataPath(string gamePath)
        {
            return Path.Combine(gamePath, ".steam-compat-data");
        }

        private static string GetStableCompatAppId(string executablePath)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in executablePath)
                {
                    hash ^= character;
                    hash *= 16777619;
                }

                return (hash & 0x7FFFFFFF).ToString();
            }
        }

        private static bool IsCommandAvailable(string command)
        {
            try
            {
                var process = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(process);
                if (proc != null)
                {
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch { }

            return false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

