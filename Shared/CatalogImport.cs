using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// Converts the existing curation files into feed entries.
    ///
    /// The feed was never something to author. `dev/_collisions.json` already holds
    /// owner-resolved pairings with checksums, and `apps.json` holds 515 catalogue
    /// entries of which 358 are IA-wired. This turns that into <see cref="FeedEntry"/>
    /// so nothing is retyped and nothing is lost in a migration.
    ///
    /// Field names come from the real files, not from a spec:
    ///   repository + assetPattern        the port, from GitHub releases
    ///   iaIdentifier / contentUrl        the data, from archive.org
    ///   dataFiles[].targetSubpath/sha1   where it stages and how to verify it
    ///   ingest                           how the data is obtained at all
    ///   folderName                       a stable id, unlike the display name
    /// </summary>
    public static class CatalogImport
    {
        /// <summary>
        /// `ingest` is an acquisition strategy, not a source. Half its values mean
        /// "the user provides this", which is the constraint a ports catalogue lives
        /// under: it ships binaries and never game data.
        /// </summary>
        public static AcquisitionKind ParseIngest(string ingest)
        {
            switch ((ingest ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "internet-archive":
                case "itch":
                    return AcquisitionKind.Fetch;
                case "data-folder":
                    return AcquisitionKind.UserFolder;
                case "place-file":
                    return AcquisitionKind.UserFile;
                case "picker":
                    return AcquisitionKind.UserPick;
                case "steam-data":
                    return AcquisitionKind.SteamInstall;
                case "none":
                    return AcquisitionKind.None;
                case "generated":
                    return AcquisitionKind.Generated;
                default:
                    // Unknown or absent. Fetch is the safe reading: it produces a
                    // locator-driven part that validation can then reject, rather than
                    // silently claiming the user will supply something.
                    return AcquisitionKind.Fetch;
            }
        }

        /// <summary>
        /// One catalogue row to a feed entry, or null when the row carries nothing
        /// downloadable. Tolerates both the collisions shape and the apps.json shape,
        /// which overlap but are not identical.
        /// </summary>
        public static FeedEntry FromCatalogRow(JObject row)
        {
            if (row == null)
            {
                return null;
            }

            var id = Str(row, "folderName") ?? Str(row, "name");
            if (id == null)
            {
                return null;
            }

            var entry = new FeedEntry
            {
                Id = id,
                Name = Str(row, "name") ?? id,
                Version = Str(row, "version"),
                Notes = Str(row, "devNote"),
                Install = InstallKind.Portable,
                ExeHint = Str(row, "executableName"),
            };

            // A `parts` array, when present, replaces the flat fields entirely.
            //
            // The flat shape carries exactly one `repository`, which cannot express a
            // collision needing two GitHub upstreams: Doom is DoomLauncher PLUS an
            // engine plus the IWADs. Rather than inventing a second repository field,
            // an entry that needs more than one upstream declares them explicitly and
            // the flat reader is skipped. Every existing entry is untouched.
            var parts = row["parts"] as JArray;
            if (parts != null && parts.Count > 0)
            {
                foreach (var p in parts.OfType<JObject>())
                {
                    var part = ReadPart(p);
                    if (part != null)
                    {
                        entry.Parts.Add(part);
                    }
                }

                return entry.Parts.Count == 0 ? null : entry;
            }

            // The port half. `assetPattern` picks which release asset, and is carried
            // on the locator so the part stays self-describing.
            var repository = Str(row, "repository");
            if (repository != null)
            {
                entry.Parts.Add(new FeedPart
                {
                    Role = PartRole.Port,
                    Source = SourceKind.GitHub,
                    Locator = repository,
                    Acquire = AcquisitionKind.Fetch,
                });
            }

            // The data half. Prefer the identifier: contentUrl is a rendered download
            // link and goes stale when a file is renamed upstream, which is exactly
            // the failure the health checker exists to catch.
            var ingest = ParseIngest(Str(row, "ingest"));
            var identifier = Str(row, "iaIdentifier");
            var contentUrl = Str(row, "contentUrl");
            var dataFiles = row["dataFiles"] as JArray;

            if (dataFiles != null && dataFiles.Count > 0)
            {
                foreach (var f in dataFiles.OfType<JObject>())
                {
                    entry.Parts.Add(new FeedPart
                    {
                        Role = PartRole.Data,
                        Source = identifier != null ? SourceKind.InternetArchive : SourceKind.Unknown,
                        Locator = identifier != null
                            ? BuildSpec(identifier, contentUrl)
                            : Str(f, "name"),
                        StageTo = Str(f, "targetSubpath"),
                        // The file this part becomes. Dropping it made five MPQ parts
                        // indistinguishable from each other.
                        StageAs = Str(f, "name"),
                        Sha1 = Str(f, "sha1"),
                        Acquire = ingest,
                        // Per-FILE optionality: Hellfire's four MPQs are optional while
                        // diabdat.mpq is not. Losing this makes every install demand
                        // expansion data the user may not have.
                        Optional = (bool?)f["optional"] ?? false,
                    });
                }
            }
            else if (identifier != null)
            {
                // IA-wired with no declared data files: the item IS the game.
                entry.Parts.Add(new FeedPart
                {
                    Role = repository != null ? PartRole.Data : PartRole.Game,
                    Source = SourceKind.InternetArchive,
                    Locator = BuildSpec(identifier, contentUrl),
                    Acquire = ingest,
                });
            }

            return entry.Parts.Count == 0 ? null : entry;
        }

        /// <summary>
        /// Recovers a <see cref="ContentSpec"/> from an identifier plus a rendered
        /// download URL, so an explicit file survives the conversion.
        ///
        /// contentUrl looks like
        ///   https://archive.org/download/&lt;identifier&gt;/&lt;url-encoded path&gt;
        /// and the path after the identifier is exactly the spec's file half.
        /// </summary>
        public static string BuildSpec(string identifier, string contentUrl)
        {
            if (string.IsNullOrWhiteSpace(contentUrl))
            {
                return identifier;
            }

            var marker = "/download/" + identifier + "/";
            var at = contentUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return identifier;
            }

            var tail = contentUrl.Substring(at + marker.Length);
            if (string.IsNullOrWhiteSpace(tail))
            {
                return identifier;
            }

            return identifier + "::" + Uri.UnescapeDataString(tail);
        }

        /// <summary>
        /// One element of an explicit `parts` array.
        ///
        /// `role` and `source` are spelled as the catalogue spells them, lowercase with
        /// hyphens, so the file stays readable by hand: that is where the owner edits
        /// collisions, and a JSON file full of PascalCase enum names is not editable.
        /// </summary>
        private static FeedPart ReadPart(JObject p)
        {
            var locator = Str(p, "locator") ?? Str(p, "repository") ?? Str(p, "iaIdentifier");
            // A generated part legitimately has no locator; it carries content instead.
            if (locator == null && Str(p, "content") == null)
            {
                return null;
            }

            return new FeedPart
            {
                Role = ParseRole(Str(p, "role")),
                Source = ParseSource(Str(p, "source")),
                Locator = locator,
                StageTo = Str(p, "targetSubpath") ?? Str(p, "stageTo"),
                StageAs = Str(p, "name") ?? Str(p, "stageAs"),
                Content = Str(p, "content"),
                Sha1 = Str(p, "sha1"),
                Acquire = ParseIngest(Str(p, "ingest")),
                Optional = (bool?)p["optional"] ?? false,
            };
        }

        public static PartRole ParseRole(string role)
        {
            switch ((role ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "launcher": return PartRole.Launcher;
                case "port":
                case "engine": return PartRole.Port;
                case "data": return PartRole.Data;
                case "patch": return PartRole.Patch;
                case "config": return PartRole.Config;
                default: return PartRole.Game;
            }
        }

        public static SourceKind ParseSource(string source)
        {
            switch ((source ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "github": return SourceKind.GitHub;
                case "gitlab": return SourceKind.GitLab;
                case "internet-archive":
                case "ia": return SourceKind.InternetArchive;
                case "itch": return SourceKind.Itch;
                case "url":
                case "direct": return SourceKind.DirectUrl;
                case "local":
                case "steam": return SourceKind.Local;
                default: return SourceKind.Unknown;
            }
        }

        public static IReadOnlyList<FeedEntry> FromCatalog(JArray rows)
        {
            return (rows ?? new JArray())
                .OfType<JObject>()
                .Select(FromCatalogRow)
                .Where(e => e != null)
                .ToList();
        }

        private static string Str(JToken token, string name)
        {
            var v = token?[name];
            if (v == null || v.Type == JTokenType.Null || v.Type == JTokenType.Object || v.Type == JTokenType.Array)
            {
                return null;
            }

            var s = v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }
}
