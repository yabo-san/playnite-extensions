using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yabo.Shared;

namespace YaboLauncherLibrary
{
    /// <summary>A game this plugin already has in Playnite, reduced to what matching needs.</summary>
    public class KnownGame
    {
        public string GameId { get; set; }
        public string Name { get; set; }
        public List<string> LinkUrls { get; set; } = new List<string>();
        public bool HasCover { get; set; }
        public bool HasBackground { get; set; }
        public bool IsInstalled { get; set; }
    }

    /// <summary>
    /// Which Playnite GameId each export record lands on. The plugin keeps the old
    /// YaboLibrary Guid, so the records that plugin imported (GameId = the engine's
    /// folderName, with the hand-picked art on them) are this plugin's records too.
    /// A record takes, in order:
    ///   1. the GameId it was given before (kept in the plugin's game-ids.json);
    ///   2. a known GameId equal to its id;
    ///   3. a known GameId equal to its folderName (ports);
    ///   4. a known game linking to its repository (github.com/owner/repo);
    ///   5. a known game with the same name, ignoring case and punctuation;
    ///   6. its id.
    /// Steps 3 to 5 only claim games no other record has, so two records never share one.
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

            var unclaimed = new Func<IEnumerable<KnownGame>>(() => knownList.Where(k => !claimed.Contains(k.GameId)));
            foreach (var r in list.Where(r => !result.ContainsKey(r.Id)))
            {
                var match =
                    (string.IsNullOrEmpty(r.FolderName) ? null : unclaimed().FirstOrDefault(k => k.GameId == r.FolderName)) ??
                    (r.Repository == null ? null : unclaimed().FirstOrDefault(k => LinksTo(k, r.Repository))) ??
                    SameName(unclaimed(), r.Name);
                if (match != null) Take(r, match.GameId);
            }

            foreach (var r in list.Where(r => !result.ContainsKey(r.Id)))
            {
                // Ids are unique in the export, but one could equal a GameId a legacy match took
                result[r.Id] = claimed.Contains(r.Id) ? r.Id + "#" + r.Source : r.Id;
                claimed.Add(result[r.Id]);
            }
            return result;
        }

        private static bool LinksTo(KnownGame game, string repository)
        {
            return game.LinkUrls.Any(url =>
            {
                if (string.IsNullOrEmpty(url)) return false;
                var u = url.Trim().TrimEnd('/').ToLowerInvariant();
                if (u.EndsWith(".git")) u = u.Substring(0, u.Length - 4);
                return u.EndsWith("github.com/" + repository) || u.EndsWith("gitlab.com/" + repository);
            });
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
