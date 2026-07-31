using GithubLauncher.Models;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Post-install "category wiring".
    ///
    /// After the launcher installs a Doom/Quake category app, this service preconfigures the
    /// relevant front-end so the user doesn't have to wire it up by hand:
    ///
    ///   * Doom engines (DSDA-Doom, GZDoom, Doom Retro, Crispy Doom) are registered as source
    ///     ports in DoomLauncher's SQLite "SourcePorts" table.
    ///   * ironwail (the Quake engine) is written into QuakeInjector's config.properties.
    ///
    /// Everything here is best-effort, idempotent and non-fatal: a wiring failure must never
    /// abort or crash an install.
    /// </summary>
    public static class CategoryWiring
    {
        // folderName -> engine identity. These mirror the folderName values in apps.json.
        private const string FolderDsdaDoom    = "kraflab.dsda-doom";
        private const string FolderGzDoom      = "ZDoom.gzdoom";
        private const string FolderDoomRetro   = "bradharding.doomretro";
        private const string FolderCrispyDoom  = "fabiangreffrath.crispy-doom";
        private const string FolderUzDoom      = "UZDoom.UZDoom";
        private const string FolderIronwail    = "andrei-drexler.ironwail";
        private const string FolderQuakeInjector = "hrehfeld.QuakeInjector";
        private const string FolderDoomLauncher  = "nstlaurent.DoomLauncher";
        private const string FolderDoomseeker    = "DoomseekerTeam.Doomseeker";

        // The set of Doom engines we register as DoomLauncher source ports.
        private static readonly Dictionary<string, string> DoomEngineNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [FolderDsdaDoom]   = "DSDA-Doom",
                [FolderGzDoom]     = "GZDoom",
                [FolderDoomRetro]  = "Doom Retro",
                [FolderCrispyDoom] = "Crispy Doom",
                [FolderUzDoom]     = "UZDoom",
            };

        // Default source-port supported extensions, matching DoomLauncher's
        // SourcePortEditForm defaults (.wad + dehacked + pk extensions).
        private const string SourcePortExtensions = ".wad,.deh,.bex,.pk3,.ipk3,.pk7,.pke";

        /// <summary>
        /// Entry point invoked after an app finishes installing. Dispatches to the right wiring
        /// routine based on which app was installed. Never throws.
        /// </summary>
        public static void OnAppInstalled(GameInfo game, string gamesFolder)
        {
            if (game?.FolderName == null || string.IsNullOrWhiteSpace(gamesFolder))
                return;

            try
            {
                var folder = game.FolderName;

                if (DoomEngineNames.ContainsKey(folder))
                {
                    // A Doom engine was installed. Register it (if DoomLauncher is present).
                    WireDoomLauncher(game.GameManager, gamesFolder, folder);
                }
                else if (string.Equals(folder, FolderDoomLauncher, StringComparison.OrdinalIgnoreCase))
                {
                    // DoomLauncher itself was just installed: backfill any engines already present.
                    WireDoomLauncher(game.GameManager, gamesFolder, null);
                }
                else if (string.Equals(folder, FolderIronwail, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(folder, FolderQuakeInjector, StringComparison.OrdinalIgnoreCase))
                {
                    // Either ironwail or QuakeInjector was installed: (re)write QuakeInjector config.
                    WireQuakeInjector(game.GameManager, gamesFolder);
                }
                else if (string.Equals(folder, FolderDoomseeker, StringComparison.OrdinalIgnoreCase))
                {
                    // Doomseeker installed: point its WAD search path at the user's Steam IWADs.
                    WireDoomseeker(gamesFolder);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[CategoryWiring] OnAppInstalled failed for {game?.FolderName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Self-healing for a moved/renamed portable folder. DoomLauncher and QuakeInjector store
        /// ABSOLUTE paths to our engines in their own configs, so relocating the run folder strands
        /// them. We record the base dir each run; if it changed since last time, re-run the wiring
        /// (which now upserts/repairs paths) so the user never has to do it manually. First run just
        /// records the location (the install-time wiring already ran). Best-effort, never throws.
        /// </summary>
        public static void AutoHealIfMoved(GameManager? manager, string gamesFolder)
        {
            try
            {
                var current = AppContext.BaseDirectory;
                // Dedicated marker file (not settings.json — the GUI keeps its own AppSettings instance
                // in memory and would clobber our write on exit). The marker travels inside the portable
                // folder, so after a move its stored path != the new base dir, which is our move signal.
                var marker = Path.Combine(current, ".base-location");
                var stored = File.Exists(marker) ? File.ReadAllText(marker).Trim() : string.Empty;

                if (string.Equals(stored, current, StringComparison.OrdinalIgnoreCase))
                    return; // unchanged since last run

                if (!string.IsNullOrEmpty(stored))
                {
                    Log.Info($"[CategoryWiring] Run folder moved ('{stored}' -> '{current}'); auto-repairing Doom/Quake wiring.");
                    WireDoomLauncher(manager, gamesFolder, null);
                    WireQuakeInjector(manager, gamesFolder);
                    WireDoomseeker(gamesFolder);
                }
                else
                {
                    Log.Info($"[CategoryWiring] Recording base dir for move-detection: {current}");
                }

                File.WriteAllText(marker, current);
            }
            catch (Exception ex)
            {
                Log.Error($"[CategoryWiring] AutoHealIfMoved failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers installed Doom engines as source ports in DoomLauncher's SQLite database.
        /// If <paramref name="onlyFolder"/> is set, only that engine is wired; otherwise every
        /// installed Doom engine is backfilled (used when DoomLauncher itself is installed).
        /// Idempotent: an engine already present (matched by Name) is left untouched.
        /// </summary>
        public static void WireDoomLauncher(GameManager? manager, string gamesFolder, string? onlyFolder)
        {
            try
            {
                // DoomLauncher must be installed for its database to exist / be meaningful.
                var dlPath = Path.Combine(gamesFolder, FolderDoomLauncher);
                if (!Directory.Exists(dlPath))
                {
                    Log.Info("[CategoryWiring] DoomLauncher not installed; skipping source-port wiring.");
                    return;
                }

                var dbPath = ResolveDoomLauncherDbPath(dlPath);
                if (dbPath == null)
                {
                    Log.Info("[CategoryWiring] Could not resolve DoomLauncher database path; skipping.");
                    return;
                }

                var engines = onlyFolder != null
                    ? new[] { onlyFolder }.Where(DoomEngineNames.ContainsKey)
                    : DoomEngineNames.Keys.AsEnumerable();

                using var connection = new SqliteConnection($"Data Source={dbPath}");
                connection.Open();

                EnsureSourcePortsTable(connection);

                foreach (var folder in engines)
                {
                    var exe = ResolveInstalledExecutable(manager, gamesFolder, folder);
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    {
                        Log.Info($"[CategoryWiring] No executable resolved for {folder}; skipping.");
                        continue;
                    }

                    var name = DoomEngineNames[folder];
                    InsertSourcePortIfMissing(connection, name, exe);
                }

                // [yabo-launcher fork] Make Doom Retro the Tier-1 / default source port. DoomLauncher picks the
                // default for a game with no per-game profile from the "DefaultSourcePort" Configuration row
                // (GameProfile.ApplyDefaultsToProfile -> appConfig.GetTypedConfigValue(DefaultSourcePort)); when
                // that row is absent it falls back to int 0 and the combo lands on the alphabetically-first port
                // (DSDA-Doom). Pin Doom Retro's SourcePortID into that row IF Doom Retro is registered. Idempotent
                // (re-points an existing row); if Doom Retro isn't installed, leave any existing default untouched.
                EnsureDefaultSourcePort(connection, DoomEngineNames[FolderDoomRetro]);

                // Bonus: if the user owns Doom on Steam, register those IWADs in-place so DoomLauncher
                // doesn't prompt for them. Best-effort — never blocks engine wiring.
                RegisterSteamIWads(connection);
            }
            catch (Exception ex)
            {
                Log.Error($"[CategoryWiring] WireDoomLauncher failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes/merges QuakeInjector's config.properties so its engine points at the installed
        /// ironwail and the user's Quake directory. Non-fatal and idempotent (merge-by-key).
        /// </summary>
        public static void WireQuakeInjector(GameManager? manager, string gamesFolder)
        {
            try
            {
                var qiPath = Path.Combine(gamesFolder, FolderQuakeInjector);
                if (!Directory.Exists(qiPath))
                {
                    Log.Info("[CategoryWiring] QuakeInjector not installed; skipping config wiring.");
                    return;
                }

                var ironwailExe = ResolveInstalledExecutable(manager, gamesFolder, FolderIronwail);
                if (string.IsNullOrEmpty(ironwailExe) || !File.Exists(ironwailExe))
                {
                    Log.Info("[CategoryWiring] ironwail executable not resolved; skipping QuakeInjector config.");
                    return;
                }

                // QuakeInjector's base dir must be the folder that HOLDS id1\ (it sets that as the
                // engine's working directory and unzips installed maps there). ironwail itself has no
                // id1, so pointing at ironwail's folder was the bug. For users who own Quake on Steam,
                // resolve the real Steam install (any drive) without hardcoding; fall back to ironwail's
                // folder so at least the engine is set.
                var quakeBaseDir = SteamContentLocator.FindQuakeBaseDir()
                                   ?? Path.GetDirectoryName(ironwailExe)
                                   ?? Path.Combine(gamesFolder, FolderIronwail);

                // QuakeInjector reads config.properties from its working directory (its install folder).
                var configPath = Path.Combine(qiPath, "config.properties");

                // QuakeInjector resolves the engine as new File(EnginePath + separator + EngineExecutable)
                // — a STRING CONCATENATION (QuakeInjector.java:261). So EngineExecutable MUST be a path
                // RELATIVE to EnginePath; an absolute path produces "…\Quake\D:\…\ironwail.exe" and the
                // engine is never found. (Also: keys are the Java FIELD names — PascalCase — not the
                // super("…") strings, so lowercase keys are silently ignored.)
                string engineRel;
                try
                {
                    engineRel = Path.GetRelativePath(quakeBaseDir, ironwailExe);
                    // Different drives can't be relativized (GetRelativePath returns a rooted path) — QI
                    // can't handle that layout; keep the absolute as a last resort and warn.
                    if (Path.IsPathRooted(engineRel))
                        Log.Warn($"[CategoryWiring] QuakeInjector: ironwail ({ironwailExe}) is on a different drive than the Quake base ({quakeBaseDir}); QI may not resolve it.");
                }
                catch
                {
                    engineRel = ironwailExe;
                }

                var props = LoadProperties(configPath);
                props["EnginePath"] = quakeBaseDir;        // base dir holding id1\ (+ where maps install)
                props["EngineExecutable"] = engineRel;     // RELATIVE to EnginePath (QI concatenates them)
                // EngineCommandLine left untouched: QI appends -game <mod> per map.

                SaveProperties(configPath, props);
                Log.Info($"[CategoryWiring] Wrote QuakeInjector config at {configPath} (EnginePath={quakeBaseDir}, EngineExecutable[rel]={engineRel}).");
            }
            catch (Exception ex)
            {
                Log.Error($"[CategoryWiring] WireQuakeInjector failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Points Doomseeker at the user's Steam-owned Doom IWADs by writing its WAD search path.
        /// Doomseeker isn't installable via this launcher (it's distributed off-GitHub), so this is a
        /// best-effort convenience: it only runs if Doomseeker has been seen on this machine
        /// (%APPDATA%\Doomseeker exists). Config is doomseeker.ini, [Doomseeker] WadPaths — a
        /// semicolon-separated directory list (Doomseeker's loader splits the string form). We use
        /// forward-slash paths to avoid INI backslash-escaping. Idempotent / non-fatal.
        /// </summary>
        public static void WireDoomseeker(string? gamesFolder = null)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return;

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData))
                    return;

                var doomseekerDir = Path.Combine(appData, "Doomseeker");

                // Fire if Doomseeker has run before (%APPDATA% config exists) OR we installed it
                // ourselves (Apps\…\Doomseeker). Pre-first-run writes are safe — Doomseeker only fills
                // defaults for ABSENT keys, so our values are preserved.
                var installedByUs = !string.IsNullOrEmpty(gamesFolder) &&
                                    Directory.Exists(Path.Combine(gamesFolder, FolderDoomseeker));
                if (!Directory.Exists(doomseekerDir) && !installedByUs)
                {
                    Log.Info("[CategoryWiring] Doomseeker not detected (no %APPDATA%\\Doomseeker and not installed); skipping.");
                    return;
                }
                Directory.CreateDirectory(doomseekerDir);

                var wadDirs = SteamContentLocator.FindDoomIWadDirs();
                if (wadDirs.Count == 0)
                {
                    Log.Info("[CategoryWiring] No Steam Doom IWAD dirs found; skipping Doomseeker WAD path.");
                    return;
                }

                var iniPath = Path.Combine(doomseekerDir, "doomseeker.ini");
                // Quote the value so QSettings round-trips the spaces/semicolons transparently.
                var value = "\"" + string.Join(";", wadDirs) + "\"";
                SetIniValue(iniPath, "Doomseeker", "WadPaths", value);
                Log.Info($"[CategoryWiring] Wrote Doomseeker WadPaths -> {string.Join(";", wadDirs)}");
            }
            catch (Exception ex)
            {
                Log.Error($"[CategoryWiring] WireDoomseeker failed (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Section-aware INI upsert that preserves all other content: sets key=value under [section],
        /// replacing an existing key (anywhere in that section) or appending it, creating the section
        /// if absent. Creates the file/dir if needed. Tolerant of a missing file.
        /// </summary>
        private static void SetIniValue(string path, string section, string key, string value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var lines = File.Exists(path)
                ? new List<string>(File.ReadAllLines(path))
                : new List<string>();

            int sectionStart = -1, sectionEnd = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    var name = t[1..^1].Trim();
                    if (sectionStart >= 0) { sectionEnd = i; break; } // end of our section
                    if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                        sectionStart = i;
                }
            }

            if (sectionStart < 0)
            {
                // Append a new section at end.
                if (lines.Count > 0 && lines[^1].Trim().Length != 0)
                    lines.Add(string.Empty);
                lines.Add($"[{section}]");
                lines.Add($"{key}={value}");
            }
            else
            {
                // Replace existing key within [sectionStart, sectionEnd), else insert after header.
                bool replaced = false;
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var t = lines[i].TrimStart();
                    var eq = t.IndexOf('=');
                    if (eq > 0 && string.Equals(t[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        replaced = true;
                        break;
                    }
                }
                if (!replaced)
                    lines.Insert(sectionStart + 1, $"{key}={value}");
            }

            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        }

        /// <summary>
        /// Resolves an installed engine's executable using the same precedence as `--run`:
        /// the saved selected_executable.txt first, then the shallowest executable candidate.
        /// </summary>
        private static string? ResolveInstalledExecutable(GameManager? manager, string gamesFolder, string folderName)
        {
            var game = manager?.FindGameByFolderName(folderName);
            var gamePath = Path.Combine(gamesFolder, folderName);

            if (game != null)
            {
                var stored = game.LoadSelectedExecutable(gamesFolder);
                // Only trust the saved path if it still exists — a moved/renamed run folder leaves a
                // stale absolute path here; fall through to re-derive from the current install dir.
                if (!string.IsNullOrEmpty(stored) && File.Exists(stored))
                    return stored;
                gamePath = game.GetInstallPath(gamesFolder);
            }

            if (!Directory.Exists(gamePath))
                return null;

            GameInfo.EnsureExecutableAtRoot(gamePath);

            var candidates = GameInfo.GetExecutableCandidates(gamePath, SearchOption.TopDirectoryOnly, out _);
            if (candidates.Count == 0)
                candidates = GameInfo.GetExecutableCandidates(gamePath, SearchOption.AllDirectories, out _);

            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>
        /// Resolves the DoomLauncher SQLite path that DoomLauncher will actually READ.
        ///
        /// DoomLauncher runs in portable mode when a DoomLauncher.sqlite sits next to DoomLauncher.exe,
        /// and the exe is frequently NOT at the install root — its release zip extracts into a nested
        /// "DoomLauncher\" subfolder, so the real DB is at &lt;install&gt;\DoomLauncher\DoomLauncher.sqlite.
        /// We therefore anchor on the exe's directory, not the install root. Preference order:
        ///   1. DoomLauncher.sqlite next to the resolved exe (the portable DB it actually uses)
        ///   2. seed it from the DoomLauncher_.sqlite init template if only that shipped
        ///   3. any existing DoomLauncher.sqlite found anywhere under the install dir
        ///   4. the %APPDATA%\DoomLauncher DB (non-portable installs)
        ///   5. create a portable DB next to the exe so DoomLauncher starts in portable mode with our ports
        /// </summary>
        private static string? ResolveDoomLauncherDbPath(string dlInstallPath)
        {
            const string dbFile = "DoomLauncher.sqlite";
            const string initFile = "DoomLauncher_.sqlite";

            // Anchor on where DoomLauncher.exe actually lives (handles the nested-folder zip layout).
            var exeDir = dlInstallPath;
            var exe = Directory.EnumerateFiles(dlInstallPath, "DoomLauncher.exe", SearchOption.AllDirectories)
                               .OrderBy(p => p.Length) // shallowest wins
                               .FirstOrDefault();
            if (!string.IsNullOrEmpty(exe))
                exeDir = Path.GetDirectoryName(exe) ?? dlInstallPath;

            var portableDb = Path.Combine(exeDir, dbFile);
            var portableInit = Path.Combine(exeDir, initFile);

            // 1. Existing portable DB next to the exe — the one DoomLauncher reads.
            if (File.Exists(portableDb))
                return portableDb;

            // 2. Only the init template shipped: DoomLauncher copies init -> db on first launch.
            //    Seed it now so our source ports survive that first launch.
            if (File.Exists(portableInit))
            {
                try
                {
                    File.Copy(portableInit, portableDb, overwrite: false);
                    return portableDb;
                }
                catch (Exception ex)
                {
                    Log.Info($"[CategoryWiring] Failed to seed portable DoomLauncher DB: {ex.Message}");
                }
            }

            // 3. Any existing DB anywhere under the install dir (defensive against layout changes).
            var found = Directory.EnumerateFiles(dlInstallPath, dbFile, SearchOption.AllDirectories)
                                 .OrderBy(p => p.Length)
                                 .FirstOrDefault();
            if (!string.IsNullOrEmpty(found))
                return found;

            // 4. Installed (non-portable) location.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                var installedDb = Path.Combine(appData, "DoomLauncher", dbFile);
                if (File.Exists(installedDb))
                    return installedDb;
            }

            // 5. Nothing yet: create a portable DB next to the exe so DoomLauncher runs portable with our ports.
            return portableDb;
        }

        /// <summary>
        /// Creates the SourcePorts table (matching DoomLauncher's schema) if it doesn't exist.
        /// This lets us seed a fresh portable DB; on an existing DoomLauncher DB this is a no-op.
        /// </summary>
        private static void EnsureSourcePortsTable(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""SourcePorts"" (
    `SourcePortID` INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    `Name` TEXT NOT NULL,
    `Executable` TEXT NOT NULL,
    `SupportedExtensions` TEXT NOT NULL,
    `Directory` TEXT NOT NULL,
    'SettingsFiles' TEXT,
    'LaunchType' TEXT,
    'FileOption' TEXT,
    'ExtraParameters' TEXT,
    'AltSaveDirectory' TEXT,
    'Archived' INTEGER);";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Upserts a source-port row mirroring DoomLauncher's InsertSourcePort:
        /// Executable = filename only, Directory = the containing directory, LaunchType = 0
        /// (SourcePort), FileOption = "-file". If a row with the same Name already exists, its
        /// Executable/Directory are UPDATED to the current path (repairs a moved run folder or an
        /// engine version bump); only the location columns are touched so user edits elsewhere survive.
        /// </summary>
        private static void InsertSourcePortIfMissing(SqliteConnection connection, string name, string exePath)
        {
            var newExe = Path.GetFileName(exePath);
            var newDir = Path.GetDirectoryName(exePath) ?? string.Empty;

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT Directory, Executable FROM SourcePorts WHERE Name = @Name COLLATE NOCASE LIMIT 1";
                check.Parameters.AddWithValue("@Name", name);
                using var reader = check.ExecuteReader();
                if (reader.Read())
                {
                    var curDir = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var curExe = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    reader.Close();
                    if (string.Equals(curDir, newDir, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(curExe, newExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Info($"[CategoryWiring] DoomLauncher source port '{name}' already current; skipping.");
                        return;
                    }
                    using var upd = connection.CreateCommand();
                    upd.CommandText = "UPDATE SourcePorts SET Executable=@Exe, Directory=@Dir WHERE Name=@Name COLLATE NOCASE";
                    upd.Parameters.AddWithValue("@Exe", newExe);
                    upd.Parameters.AddWithValue("@Dir", newDir);
                    upd.Parameters.AddWithValue("@Name", name);
                    upd.ExecuteNonQuery();
                    Log.Info($"[CategoryWiring] Updated DoomLauncher source port '{name}' path -> {exePath} (was {curDir}).");
                    return;
                }
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = @"
INSERT INTO SourcePorts
    (Name, Executable, SupportedExtensions, Directory, SettingsFiles, LaunchType, FileOption, ExtraParameters, AltSaveDirectory, Archived)
VALUES
    (@Name, @Executable, @SupportedExtensions, @Directory, @SettingsFiles, @LaunchType, @FileOption, @ExtraParameters, @AltSaveDirectory, @Archived)";

            insert.Parameters.AddWithValue("@Name", name);
            insert.Parameters.AddWithValue("@Executable", Path.GetFileName(exePath));
            insert.Parameters.AddWithValue("@SupportedExtensions", SourcePortExtensions);
            insert.Parameters.AddWithValue("@Directory", Path.GetDirectoryName(exePath) ?? string.Empty);
            insert.Parameters.AddWithValue("@SettingsFiles", string.Empty);
            insert.Parameters.AddWithValue("@LaunchType", (int)0); // SourcePortLaunchType.SourcePort
            insert.Parameters.AddWithValue("@FileOption", "-file");
            insert.Parameters.AddWithValue("@ExtraParameters", string.Empty);
            insert.Parameters.AddWithValue("@AltSaveDirectory", string.Empty);
            insert.Parameters.AddWithValue("@Archived", 0);
            insert.ExecuteNonQuery();

            Log.Info($"[CategoryWiring] Registered DoomLauncher source port '{name}' -> {exePath}.");
        }

        /// <summary>
        /// [yabo-launcher fork] Pins DoomLauncher's GLOBAL default source port to the named port (Doom Retro)
        /// by upserting the "DefaultSourcePort" Configuration row to that port's SourcePortID. DoomLauncher reads
        /// this via AppConfiguration.GetTypedConfigValue(ConfigType.DefaultSourcePort) and applies it to any game
        /// without a per-game profile (GameProfile.ApplyDefaultsToProfile). Idempotent: no-ops if the row already
        /// points at the right port. If the named port isn't registered (engine not installed) this does NOTHING,
        /// so an existing user-chosen default is never clobbered. Also creates the Configuration table if a fresh
        /// portable DB doesn't have it yet. Best-effort; never throws out.
        /// </summary>
        private static void EnsureDefaultSourcePort(SqliteConnection connection, string portName)
        {
            try
            {
                // Resolve the SourcePortID of the target port (e.g. Doom Retro). Absent => engine not installed =>
                // leave any existing default alone.
                long portId;
                using (var find = connection.CreateCommand())
                {
                    find.CommandText = "SELECT SourcePortID FROM SourcePorts WHERE Name = @Name COLLATE NOCASE LIMIT 1";
                    find.Parameters.AddWithValue("@Name", portName);
                    var r = find.ExecuteScalar();
                    if (r == null || r is DBNull)
                    {
                        Log.Info($"[CategoryWiring] Default source port '{portName}' not registered; leaving existing default untouched.");
                        return;
                    }
                    portId = Convert.ToInt64(r);
                }

                // DoomLauncher's Configuration schema: (ConfigID PK, Name, Value, AvailableValues, UserCanModify).
                // Create it on a fresh portable DB so the seed works even before DoomLauncher's first launch.
                using (var ensure = connection.CreateCommand())
                {
                    ensure.CommandText = @"
CREATE TABLE IF NOT EXISTS 'Configuration' (
    'ConfigID' INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    'Name' TEXT NOT NULL,
    'Value' TEXT NOT NULL,
    'AvailableValues' TEXT NOT NULL,
    'UserCanModify' INTEGER);";
                    ensure.ExecuteNonQuery();
                }

                // Idempotent upsert of the DefaultSourcePort row (Value = SourcePortID as a string int).
                var newValue = portId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using (var check = connection.CreateCommand())
                {
                    check.CommandText = "SELECT Value FROM Configuration WHERE Name = 'DefaultSourcePort' LIMIT 1";
                    var cur = check.ExecuteScalar();
                    if (cur != null && cur is not DBNull)
                    {
                        if (string.Equals(cur.ToString(), newValue, StringComparison.Ordinal))
                        {
                            Log.Info($"[CategoryWiring] DoomLauncher default source port already '{portName}' (id {portId}); skipping.");
                            return;
                        }
                        using var upd = connection.CreateCommand();
                        upd.CommandText = "UPDATE Configuration SET Value = @Value WHERE Name = 'DefaultSourcePort'";
                        upd.Parameters.AddWithValue("@Value", newValue);
                        upd.ExecuteNonQuery();
                        Log.Info($"[CategoryWiring] DoomLauncher default source port -> '{portName}' (id {portId}, was '{cur}').");
                        return;
                    }
                }

                using (var ins = connection.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO Configuration (Name, Value, AvailableValues, UserCanModify) VALUES ('DefaultSourcePort', @Value, '', 0)";
                    ins.Parameters.AddWithValue("@Value", newValue);
                    ins.ExecuteNonQuery();
                }
                Log.Info($"[CategoryWiring] DoomLauncher default source port set to '{portName}' (id {portId}).");
            }
            catch (Exception ex)
            {
                Log.Error($"[CategoryWiring] EnsureDefaultSourcePort('{portName}') failed (non-fatal): {ex.Message}");
            }
        }

        // Friendly titles for the IWADs DoomLauncher recognises. Falls back to UPPERCASED filename.
        private static readonly Dictionary<string, string> IWadTitles =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["doom.wad"]         = "Ultimate Doom",
                ["doom1.wad"]        = "Doom (Shareware)",
                ["doom2.wad"]        = "Doom II: Hell on Earth",
                ["plutonia.wad"]     = "Final Doom: The Plutonia Experiment",
                ["tnt.wad"]          = "Final Doom: TNT: Evilution",
                ["nerve.wad"]        = "No Rest for the Living",
                ["sigil.wad"]        = "SIGIL",
                ["sigil2.wad"]       = "SIGIL II",
                ["masterlevels.wad"] = "Master Levels for Doom II",
                ["heretic.wad"]      = "Heretic",
                ["hexen.wad"]        = "Hexen",
                ["hexdd.wad"]        = "Hexen: Deathkings of the Dark Citadel",
                ["strife1.wad"]      = "Strife",
                ["freedoom1.wad"]    = "Freedoom: Phase 1",
                ["freedoom2.wad"]    = "Freedoom: Phase 2",
            };

        // True base-game IWADs that source ports accept as an -iwad. Everything else Steam ships
        // (SIGIL, SIGIL II, NERVE, Master Levels, Hexen Deathkings) is a PWAD that loads ON TOP of one
        // of these — registering those as IWADs makes Doom Retro / DSDA / etc. reject them.
        private static readonly HashSet<string> TrueIwads = new(StringComparer.OrdinalIgnoreCase)
        {
            "doom.wad", "doom1.wad", "doom2.wad", "plutonia.wad", "tnt.wad",
            "heretic.wad", "hexen.wad", "strife1.wad", "freedoom1.wad", "freedoom2.wad",
        };

        // Expansion PWAD -> the base IWAD it must launch with (-iwad <base> -file <pwad>).
        private static readonly Dictionary<string, string> PwadParents = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sigil.wad"]        = "doom.wad",
            ["sigil2.wad"]       = "doom.wad",
            ["nerve.wad"]        = "doom2.wad",
            ["masterlevels.wad"] = "doom2.wad",
            ["hexdd.wad"]        = "hexen.wad",
        };

        /// <summary>
        /// Registers Steam-owned Doom content into DoomLauncher by ABSOLUTE PATH (unmanaged / in place —
        /// no copying). True IWADs get a GameFiles + IWads row. Expansion PWADs (SIGIL, NERVE, Master
        /// Levels, …) are registered as plain GameFiles whose IWadID points at their base IWAD — NOT as
        /// IWADs (source ports reject those). Self-repairs a PWAD previously mis-registered as an IWAD
        /// (removes its bogus IWads row, re-points it at the base). Idempotent; best-effort.
        /// </summary>
        private static void RegisterSteamIWads(SqliteConnection connection)
        {
            try
            {
                var wads = SteamContentLocator.FindDoomIWads();
                if (wads.Count == 0)
                {
                    Log.Info("[CategoryWiring] No Steam Doom content found; skipping.");
                    return;
                }

                // 1. True IWADs first; record basename -> IWadID so PWADs can link to their base.
                var iwadIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in wads)
                {
                    var name = Path.GetFileName(path);
                    if (!TrueIwads.Contains(name)) continue;
                    var id = EnsureIwadRegistered(connection, path, TitleFor(name));
                    if (id > 0) iwadIds[name] = id;
                }

                // 2. Expansion PWADs, linked to (and repaired against) their base IWAD.
                foreach (var path in wads)
                {
                    var name = Path.GetFileName(path);
                    if (!PwadParents.TryGetValue(name, out var parentName)) continue;

                    long parentId = iwadIds.TryGetValue(parentName, out var pid)
                        ? pid
                        : LookupIwadIdByPath(connection, wads.FirstOrDefault(w =>
                            string.Equals(Path.GetFileName(w), parentName, StringComparison.OrdinalIgnoreCase)));
                    if (parentId <= 0)
                    {
                        Log.Warn($"[CategoryWiring] PWAD '{name}': base IWAD '{parentName}' not present; skipping.");
                        continue;
                    }
                    EnsurePwadRegistered(connection, path, TitleFor(name), parentId);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[CategoryWiring] RegisterSteamIWads failed (non-fatal): {ex.Message}");
            }
        }

        private static string TitleFor(string basename) =>
            IWadTitles.TryGetValue(basename, out var t) ? t : Path.GetFileNameWithoutExtension(basename).ToUpperInvariant();

        private static long ScalarLong(SqliteConnection conn, string sql, string param, object value)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue(param, value);
            var r = cmd.ExecuteScalar();
            return (r == null || r is DBNull) ? 0 : Convert.ToInt64(r);
        }

        private static long LookupIwadIdByPath(SqliteConnection conn, string? path) =>
            string.IsNullOrEmpty(path) ? 0 : ScalarLong(conn, "SELECT IWadID FROM IWads WHERE FileName=@p COLLATE NOCASE LIMIT 1", "@p", path);

        /// <summary>Ensures a true IWAD has a GameFiles + IWads row; returns its IWadID. Idempotent.</summary>
        private static long EnsureIwadRegistered(SqliteConnection conn, string path, string title)
        {
            var gameFileId = ScalarLong(conn, "SELECT GameFileID FROM GameFiles WHERE FileName=@p COLLATE NOCASE LIMIT 1", "@p", path);
            if (gameFileId > 0)
            {
                var existing = ScalarLong(conn, "SELECT IWadID FROM IWads WHERE GameFileID=@g LIMIT 1", "@g", gameFileId);
                if (existing > 0) return existing; // already a registered IWAD
            }
            else
            {
                using var insGame = conn.CreateCommand();
                insGame.CommandText = "INSERT INTO GameFiles (FileName, Title) VALUES (@p,@t); SELECT last_insert_rowid();";
                insGame.Parameters.AddWithValue("@p", path);
                insGame.Parameters.AddWithValue("@t", title);
                gameFileId = Convert.ToInt64(insGame.ExecuteScalar());
            }

            long iwadId;
            using (var insIwad = conn.CreateCommand())
            {
                insIwad.CommandText = "INSERT INTO IWads (Name, FileName, GameFileID) VALUES (@p,@p,@g); SELECT last_insert_rowid();";
                insIwad.Parameters.AddWithValue("@p", path);
                insIwad.Parameters.AddWithValue("@g", gameFileId);
                iwadId = Convert.ToInt64(insIwad.ExecuteScalar());
            }
            using (var link = conn.CreateCommand())
            {
                link.CommandText = "UPDATE GameFiles SET IWadID=@i, Title=@t WHERE GameFileID=@g";
                link.Parameters.AddWithValue("@i", iwadId);
                link.Parameters.AddWithValue("@t", title);
                link.Parameters.AddWithValue("@g", gameFileId);
                link.ExecuteNonQuery();
            }
            Log.Info($"[CategoryWiring] Registered IWAD '{title}' -> {path}");
            return iwadId;
        }

        /// <summary>Registers a PWAD as a GameFiles row linked to its base IWAD (no IWads row), repairing
        /// a PWAD that was previously mis-registered as an IWAD. Idempotent.</summary>
        private static void EnsurePwadRegistered(SqliteConnection conn, string path, string title, long parentIwadId)
        {
            var gameFileId = ScalarLong(conn, "SELECT GameFileID FROM GameFiles WHERE FileName=@p COLLATE NOCASE LIMIT 1", "@p", path);
            if (gameFileId > 0)
            {
                var stray = ScalarLong(conn, "SELECT IWadID FROM IWads WHERE GameFileID=@g LIMIT 1", "@g", gameFileId);
                if (stray > 0)
                {
                    using var del = conn.CreateCommand();
                    del.CommandText = "DELETE FROM IWads WHERE GameFileID=@g";
                    del.Parameters.AddWithValue("@g", gameFileId);
                    del.ExecuteNonQuery();
                    Log.Info($"[CategoryWiring] Repaired '{title}': removed bogus IWAD entry (it's a PWAD).");
                }
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE GameFiles SET IWadID=@i, Title=@t WHERE GameFileID=@g";
                upd.Parameters.AddWithValue("@i", parentIwadId);
                upd.Parameters.AddWithValue("@t", title);
                upd.Parameters.AddWithValue("@g", gameFileId);
                upd.ExecuteNonQuery();
                Log.Info($"[CategoryWiring] PWAD '{title}' linked to base IWAD (IWadID {parentIwadId}).");
                return;
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO GameFiles (FileName, Title, IWadID) VALUES (@p,@t,@i)";
            ins.Parameters.AddWithValue("@p", path);
            ins.Parameters.AddWithValue("@t", title);
            ins.Parameters.AddWithValue("@i", parentIwadId);
            ins.ExecuteNonQuery();
            Log.Info($"[CategoryWiring] Registered PWAD '{title}' -> {path} (base IWadID {parentIwadId}).");
        }

        /// <summary>Reads a Java-style .properties file (key=value), tolerating comments/blank lines.</summary>
        private static Dictionary<string, string> LoadProperties(string path)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path))
                return props;

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.TrimStart();
                if (line.Length == 0 || line[0] == '#' || line[0] == '!')
                    continue;

                var sep = line.IndexOf('=');
                if (sep < 0)
                    sep = line.IndexOf(':');
                if (sep <= 0)
                    continue;

                var key = line[..sep].Trim();
                var value = UnescapePropertyValue(line[(sep + 1)..].Trim());
                props[key] = value;
            }

            return props;
        }

        /// <summary>Writes a Java-style .properties file with proper escaping (backslashes, colons, =).</summary>
        private static void SaveProperties(string path, Dictionary<string, string> props)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("# Written by yabo-launcher category wiring\n");
            foreach (var kvp in props)
                sb.Append(kvp.Key).Append('=').Append(EscapePropertyValue(kvp.Value)).Append('\n');

            File.WriteAllText(path, sb.ToString());
        }

        // java.util.Properties escapes \, and leading whitespace; on store it also escapes
        // ':' and '=' in keys (not required for values). We escape backslashes so Windows paths
        // round-trip, which is what QuakeInjector's Properties.load expects.
        private static string EscapePropertyValue(string value) =>
            value.Replace("\\", "\\\\");

        private static string UnescapePropertyValue(string value)
        {
            if (!value.Contains('\\'))
                return value;

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    i++;
                    sb.Append(value[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => value[i],
                    });
                }
                else
                {
                    sb.Append(value[i]);
                }
            }

            return sb.ToString();
        }
    }
}
