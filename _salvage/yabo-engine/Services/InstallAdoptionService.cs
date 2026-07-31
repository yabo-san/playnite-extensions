using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GithubLauncher.Models;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Folder-scan install adoption, ported from RohanKar's `scan-for-games` IPC handler
    /// (src/main/main.js). Lets a user point yabo at a folder they ALREADY downloaded (or a parent folder of
    /// many game folders) and ADOPT matching catalog entries as "installed" — wiring the card to the existing
    /// folder + detected exe, with NO download and NO file move.
    ///
    /// This service is pure matching/inspection: it finds the best catalog match for a folder (RohanKar's
    /// 3-pass approach) and locates a launch exe inside it, returning an <see cref="AdoptionMatch"/>. The
    /// CALLER (CLIHandler) performs the actual persistence, reusing the engine's existing mechanisms
    /// (GameInfo.InstallPath → apps.json, GameInfo.SaveSelectedExecutable → selected_executable.txt), so the
    /// card shows Installed exactly like `--locate-install` + `--set-exe` would.
    /// </summary>
    public static class InstallAdoptionService
    {
        /// <summary>One matched (folder → catalog game) candidate produced by a scan/adopt pass.</summary>
        public sealed class AdoptionMatch
        {
            /// <summary>The catalog game this folder was matched to.</summary>
            public GameInfo Game { get; init; } = null!;

            /// <summary>The existing install folder (the folder the user already has).</summary>
            public string FolderPath { get; init; } = "";

            /// <summary>The launch exe detected inside the folder, or null when none / ambiguous.</summary>
            public string? ExePath { get; init; }

            /// <summary>How the match was made: "folderName", "name", "folderName-sanitized",
            /// "name-sanitized", or "title".</summary>
            public string MatchedBy { get; init; } = "";
        }

        /// <summary>
        /// Sanitize a name into the safe Windows folder name a browser download would produce — mirrors
        /// RohanKar's sanitizeTitle/sanitizeFolderName: replace Windows-illegal chars with '_', strip trailing
        /// dots/spaces, trim. Returns lowercase for case-insensitive comparison.
        /// </summary>
        public static string Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var replaced = new char[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                replaced[i] = (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') ? '_' : c;
            }
            var s = new string(replaced).TrimEnd('.', ' ').Trim();
            return s.ToLowerInvariant();
        }

        /// <summary>
        /// Match a single folder NAME against the catalog using RohanKar's 3-pass approach and return the best
        /// match (or null). Passes, in priority order:
        ///   1. EXACT — folder name equals a game's FolderName or Name (case-insensitive).
        ///   2. SANITIZED — sanitized folder name equals a sanitized FolderName or Name.
        ///   3. TITLE — sanitized folder name equals a sanitized display Name (handles archive.org folders
        ///      named after the game title rather than the identifier).
        /// </summary>
        public static GameInfo? MatchFolder(IEnumerable<GameInfo> catalog, string folderName, out string matchedBy)
        {
            matchedBy = "";
            if (catalog == null || string.IsNullOrWhiteSpace(folderName)) return null;
            var games = catalog.Where(g => g != null).ToList();
            var sanitizedFolder = Sanitize(folderName);

            // ── Pass 1: exact identifier (FolderName) / name match ──────────────────────────────
            var hit = games.FirstOrDefault(g =>
                g.FolderName != null && g.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { matchedBy = "folderName"; return hit; }

            hit = games.FirstOrDefault(g =>
                g.Name != null && g.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { matchedBy = "name"; return hit; }

            // ── Pass 2: sanitized identifier (FolderName) / name match ──────────────────────────
            if (!string.IsNullOrEmpty(sanitizedFolder))
            {
                hit = games.FirstOrDefault(g =>
                    !string.IsNullOrWhiteSpace(g.FolderName) && Sanitize(g.FolderName) == sanitizedFolder);
                if (hit != null) { matchedBy = "folderName-sanitized"; return hit; }

                hit = games.FirstOrDefault(g =>
                    !string.IsNullOrWhiteSpace(g.Name) && Sanitize(g.Name) == sanitizedFolder);
                if (hit != null) { matchedBy = "name-sanitized"; return hit; }
            }

            // ── Pass 3: game title match (case-insensitive, sanitized) ──────────────────────────
            // Folders named "Zoo Tycoon - Complete Collection" downloaded directly from archive.org mirror the
            // game title, not the identifier. (Pass 2's name-sanitized already covers most of this; this pass
            // additionally tries the ArtName/title alias when present.)
            if (!string.IsNullOrEmpty(sanitizedFolder))
            {
                hit = games.FirstOrDefault(g =>
                    !string.IsNullOrWhiteSpace(g.ArtName) && Sanitize(g.ArtName) == sanitizedFolder);
                if (hit != null) { matchedBy = "title"; return hit; }
            }

            return null;
        }

        /// <summary>
        /// Locate the launch exe inside <paramref name="folderPath"/>, reusing the IA install service's
        /// recursive finder + largest-exe heuristic so adopted folders resolve their exe identically to an
        /// IA-installed port. Returns null when none can be found.
        /// </summary>
        public static string? FindExe(string folderPath)
        {
            try
            {
                var exes = InternetArchiveInstallService.FindExesInDir(folderPath, 0);
                return InternetArchiveInstallService.ChooseLaunchExe(exes);
            }
            catch (Exception ex)
            {
                Log.Warn($"adopt: exe scan failed for '{folderPath}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adopt a SINGLE folder: match it against the catalog and, on a hit, build an <see cref="AdoptionMatch"/>
        /// with the detected exe. Does NOT persist anything (the caller does). Returns null on no match or a
        /// non-existent folder. <paramref name="explicitGameName"/> forces the target game (skips matching) when
        /// the user named one — the folder is still validated and the exe located.
        /// </summary>
        public static AdoptionMatch? AdoptFolder(IEnumerable<GameInfo> catalog, string folderPath, string? explicitGameName = null)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                Log.Warn($"adopt: folder not found: {folderPath}");
                return null;
            }

            var full = Path.GetFullPath(folderPath);
            var folderName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            GameInfo? game;
            string matchedBy;
            if (!string.IsNullOrWhiteSpace(explicitGameName))
            {
                game = catalog?.FirstOrDefault(g => g != null &&
                    ((g.Name != null && g.Name.Equals(explicitGameName, StringComparison.OrdinalIgnoreCase)) ||
                     (g.FolderName != null && g.FolderName.Equals(explicitGameName, StringComparison.OrdinalIgnoreCase))));
                matchedBy = "explicit";
                if (game == null)
                {
                    Log.Warn($"adopt: named game not found in catalog: '{explicitGameName}'");
                    return null;
                }
            }
            else
            {
                game = MatchFolder(catalog!, folderName, out matchedBy);
                if (game == null)
                {
                    Log.Info($"adopt: no catalog match for folder '{folderName}' ({full}).");
                    return null;
                }
            }

            var exe = FindExe(full);
            Log.Info($"adopt: matched '{folderName}' → '{game.Name}' ({matchedBy}); exe: {(exe ?? "(none found)")}");
            return new AdoptionMatch { Game = game, FolderPath = full, ExePath = exe, MatchedBy = matchedBy };
        }

        /// <summary>
        /// Scan a PARENT folder: enumerate immediate subfolders and attempt to adopt each. Returns one
        /// <see cref="AdoptionMatch"/> per matched subfolder (unmatched subfolders are skipped). Like RohanKar's
        /// `scan-for-games`, this never throws on an unreadable dir — it just returns what it could match.
        /// </summary>
        public static List<AdoptionMatch> ScanParent(IEnumerable<GameInfo> catalog, string parentFolder)
        {
            var results = new List<AdoptionMatch>();
            if (string.IsNullOrWhiteSpace(parentFolder) || !Directory.Exists(parentFolder))
            {
                Log.Warn($"adopt-scan: parent folder not found: {parentFolder}");
                return results;
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(parentFolder); }
            catch (Exception ex)
            {
                Log.Warn($"adopt-scan: cannot enumerate '{parentFolder}': {ex.Message}");
                return results;
            }

            // Snapshot the catalog once so each subfolder matches against the same list.
            var games = (catalog ?? Enumerable.Empty<GameInfo>()).Where(g => g != null).ToList();
            foreach (var sub in subdirs)
            {
                var match = AdoptFolder(games, sub);
                if (match != null) results.Add(match);
            }

            Log.Info($"adopt-scan: '{parentFolder}' — {results.Count} of {subdirs.Length} subfolder(s) matched.");
            return results;
        }
    }
}
