using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] The CURATION GATE (allow-list).
    ///
    /// <para>
    /// <c>gate.json</c> sits beside the engine exe and is a plain JSON array of <c>folderName</c> strings
    /// that are "active". It is an active-filter layered ON TOP of the full canon (apps.json + user-apps.json) —
    /// the canon is never touched, so the gate is fully reversible.
    /// </para>
    ///
    /// <para>Semantics (used by <c>--list-json</c>):</para>
    /// <list type="bullet">
    ///   <item><description>gate.json ABSENT  → no gate; show the whole catalog (backward-compatible).</description></item>
    ///   <item><description>gate.json PRESENT → show ONLY cards whose folderName is in the array.
    ///   An empty array (<c>[]</c>) therefore wipes the library (0 cards).</description></item>
    /// </list>
    ///
    /// Matching is by <c>folderName</c> only and case-insensitive. The file is a deduplicated, sorted
    /// JSON array of strings.
    /// </summary>
    public static class GateService
    {
        public const string FileName = "gate.json";

        /// <summary>Absolute path to gate.json beside the engine exe.</summary>
        public static string GatePath =>
            Path.Combine(AppContext.BaseDirectory, FileName);

        /// <summary>True when a gate.json exists (i.e. the curation gate is active at all).</summary>
        public static bool Exists() => File.Exists(GatePath);

        /// <summary>
        /// Load the gate's folderNames. Returns an EMPTY (but non-null) set when the file is missing or
        /// unreadable — callers use <see cref="Exists"/> to distinguish "no gate" from "empty gate".
        /// </summary>
        public static HashSet<string> Load()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(GatePath)) return set;
                var json = File.ReadAllText(GatePath);
                if (string.IsNullOrWhiteSpace(json)) return set;
                var arr = JsonSerializer.Deserialize<List<string>>(json);
                if (arr != null)
                    foreach (var f in arr)
                        if (!string.IsNullOrWhiteSpace(f)) set.Add(f.Trim());
            }
            catch { /* corrupt gate.json → treat as empty (wiped) rather than crash --list-json */ }
            return set;
        }

        /// <summary>Persist the gate as a deduplicated, sorted JSON array of strings (creates the file if missing).</summary>
        public static void Save(IEnumerable<string> folderNames)
        {
            var ordered = folderNames
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GatePath, json);
        }

        /// <summary>Is this folderName allowed through the gate? When no gate exists, EVERYTHING passes.</summary>
        public static bool IsAllowed(string? folderName)
        {
            if (!Exists()) return true;                       // no gate → all visible
            if (string.IsNullOrWhiteSpace(folderName)) return false;
            return Load().Contains(folderName);
        }
    }
}
