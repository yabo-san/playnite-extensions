using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yabo.Shared;

namespace RohanKarPlaynite
{
    /// <summary>A game this plugin already has in Playnite, reduced to what matching needs.</summary>
    public class KnownGame
    {
        public string GameId { get; set; }
        public string Name { get; set; }
        public string InstallDirectory { get; set; }
        public bool HasCover { get; set; }
        public bool HasBackground { get; set; }
        public bool IsInstalled { get; set; }
    }

    /// <summary>
    /// Which Playnite GameId each export record lands on. The GameId is the launcher's
    /// library id, which is the export's id, so a record normally keeps its own id.
    /// Records the folder-scanning version of this plugin imported are keyed by folder
    /// instead ("(123)Title" gave 123), so a record without an exact match takes, in order:
    ///   1. the GameId it was given before (kept in the plugin's game-ids.json);
    ///   2. a known GameId equal to its id;
    ///   3. a known game with the same install folder, when only one has it;
    ///   4. a known game with the same name, ignoring case and punctuation, when only one has it;
    ///   5. its id.
    /// Steps 3 and 4 only claim games no other record has, so two records never share one.
    /// </summary>
    public static class GameIdResolver
    {
        public static Dictionary<string, string> Resolve(
            IEnumerable<LauncherGame> records,
            IEnumerable<KnownGame> known,
            IDictionary<string, string> previous)
        {
            var list = records.ToList();
            var knownList = known.Where(k => !string.IsNullOrEmpty(k.GameId)).ToList();
            var knownIds = new HashSet<string>(knownList.Select(k => k.GameId), StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            void Take(LauncherGame r, string gameId)
            {
                result[r.Id] = gameId;
                claimed.Add(gameId);
            }

            // 1 and 2 first for every record, so a fuzzy match later never steals an exact one
            foreach (var r in list)
            {
                if (previous != null && previous.TryGetValue(r.Id, out var before) && !string.IsNullOrEmpty(before) && !claimed.Contains(before))
                {
                    Take(r, before);
                }
            }
            foreach (var r in list.Where(r => !result.ContainsKey(r.Id)))
            {
                if (knownIds.Contains(r.Id) && !claimed.Contains(r.Id)) Take(r, r.Id);
            }

            foreach (var r in list.Where(r => !result.ContainsKey(r.Id)))
            {
                var unclaimed = knownList.Where(k => !claimed.Contains(k.GameId)).ToList();
                var match = SameFolder(unclaimed, r.InstallDir) ?? SameName(unclaimed, r.Name);
                if (match != null) Take(r, match.GameId);
            }

            foreach (var r in list.Where(r => !result.ContainsKey(r.Id)))
            {
                // Ids are unique in the export, but one could equal a GameId a match above took
                result[r.Id] = claimed.Contains(r.Id) ? r.Id + "#" + r.Source : r.Id;
                claimed.Add(result[r.Id]);
            }
            return result;
        }

        private static KnownGame SameFolder(IEnumerable<KnownGame> games, string installDir)
        {
            var key = FolderKey(installDir);
            if (key == null) return null;
            var hits = games.Where(g => FolderKey(g.InstallDirectory) == key).Take(2).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }

        /// <summary>A folder path compared the way Windows does: case-insensitive, either slash, no trailing one.</summary>
        public static string FolderKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return path.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        private static KnownGame SameName(IEnumerable<KnownGame> games, string name)
        {
            var key = NameKey(name);
            if (key.Length == 0) return null;
            var hits = games.Where(g => NameKey(g.Name) == key).Take(2).ToList();
            return hits.Count == 1 ? hits[0] : null;   // two games with that name: no guess
        }

        /// <summary>Letters and digits only, lowercase: "Ship of Harkinian!" and "ship-of-harkinian" match.</summary>
        public static string NameKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Known games whose GameId no export record landed on: marked not installed,
        /// never removed (the user's metadata stays).
        /// </summary>
        public static List<string> NoLongerExported(IEnumerable<KnownGame> known, IEnumerable<string> exportedGameIds)
        {
            var exported = new HashSet<string>(exportedGameIds, StringComparer.Ordinal);
            return known.Where(k => k.IsInstalled && !string.IsNullOrEmpty(k.GameId) && !exported.Contains(k.GameId))
                        .Select(k => k.GameId).ToList();
        }
    }
}
