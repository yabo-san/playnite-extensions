using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace Yabo.Shared
{
    /// <summary>
    /// playnite-export.json as the y4bo launcher writes it (schema version 1,
    /// documented in the launcher's docs/PLAYNITE-EXPORT.md). No Playnite
    /// dependency, so the parser runs in tests against a fixture file.
    /// </summary>
    public class LauncherExport
    {
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("generatedAt")] public DateTime? GeneratedAt { get; set; }
        [JsonProperty("launcherVersion")] public string LauncherVersion { get; set; }
        [JsonProperty("games")] public List<LauncherGame> Games { get; set; } = new List<LauncherGame>();
    }

    public class LauncherGame
    {
        /// <summary>Stable across renames and re-imports: the archive.org identifier,
        /// quiver:&lt;repository&gt;, or a UUID for a manual entry.</summary>
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>archive.org:&lt;uploader&gt;, quiver:&lt;catalog url&gt; or manual.</summary>
        [JsonProperty("source")] public string Source { get; set; }
        /// <summary>PC, Nintendo, PlayStation, Xbox or Other.</summary>
        [JsonProperty("platform")] public string Platform { get; set; }
        [JsonProperty("installed")] public bool Installed { get; set; }
        [JsonProperty("installDir")] public string InstallDir { get; set; }
        [JsonProperty("exe")] public string Exe { get; set; }
        [JsonProperty("args")] public string Args { get; set; }
        [JsonProperty("workingDir")] public string WorkingDir { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("updateAvailable")] public bool UpdateAvailable { get; set; }
        [JsonProperty("coverPath")] public string CoverPath { get; set; }
        [JsonProperty("heroPath")] public string HeroPath { get; set; }
        [JsonProperty("tags")] public List<string> Tags { get; set; } = new List<string>();
        [JsonProperty("lastPlayed")] public DateTime? LastPlayed { get; set; }
        [JsonProperty("playtimeSeconds")] public ulong PlaytimeSeconds { get; set; }
        [JsonProperty("favorite")] public bool Favorite { get; set; }
        /// <summary>Ports only: the catalog entry's folderName, which the older
        /// YaboLibrary plugin used as its GameId.</summary>
        [JsonProperty("folderName")] public string FolderName { get; set; }

        /// <summary>"archive.org", "quiver" or "manual": the source before its colon.</summary>
        [JsonIgnore]
        public string SourceKind
        {
            get
            {
                if (string.IsNullOrEmpty(Source)) return null;
                var colon = Source.IndexOf(':');
                return colon < 0 ? Source : Source.Substring(0, colon);
            }
        }

        /// <summary>Lowercase owner/repo of a port, from its quiver:&lt;repository&gt; id.</summary>
        [JsonIgnore]
        public string Repository
        {
            get
            {
                if (Id == null || !Id.StartsWith("quiver:", StringComparison.Ordinal)) return null;
                var repo = Id.Substring("quiver:".Length);
                return repo.Length == 0 || repo.StartsWith("name:", StringComparison.Ordinal) ? null : repo;
            }
        }

        /// <summary>Installed with something to run.</summary>
        [JsonIgnore]
        public bool Playable => Installed && !string.IsNullOrWhiteSpace(Exe);
    }

    public static class LauncherExportParser
    {
        /// <summary>
        /// Parses the export. Records without an id are dropped, and a repeated id keeps
        /// its first record, so every GameId is unique. A newer schemaVersion throws:
        /// a breaking change means this plugin needs updating before it can trust the file.
        /// </summary>
        public static LauncherExport Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("playnite-export.json is empty.");
            LauncherExport export;
            try
            {
                export = JsonConvert.DeserializeObject<LauncherExport>(json);
            }
            catch (JsonException e)
            {
                throw new FormatException("playnite-export.json is not valid JSON: " + e.Message, e);
            }
            if (export == null) throw new FormatException("playnite-export.json is empty.");
            if (export.SchemaVersion < 1) throw new FormatException("playnite-export.json has no schemaVersion.");
            if (export.SchemaVersion > LauncherExport.SupportedSchemaVersion)
            {
                throw new NotSupportedException(
                    $"playnite-export.json is schema version {export.SchemaVersion}; this plugin reads version " +
                    $"{LauncherExport.SupportedSchemaVersion}. Update the y4bo Launcher plugin.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            export.Games = (export.Games ?? new List<LauncherGame>())
                .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Id) && seen.Add(g.Id))
                .ToList();
            foreach (var g in export.Games)
            {
                if (string.IsNullOrWhiteSpace(g.Name)) g.Name = g.Id;
                g.Tags = (g.Tags ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            }
            return export;
        }

        /// <summary>
        /// Reads and parses the file. The launcher replaces it by rename, so a read can
        /// meet a sharing violation for a moment; those are retried a few times.
        /// </summary>
        public static LauncherExport Read(string path, int attempts = 5, int delayMs = 200)
        {
            for (var i = 1; ; i++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream))
                    {
                        return Parse(reader.ReadToEnd());
                    }
                }
                catch (IOException) when (i < attempts && File.Exists(path))
                {
                    Thread.Sleep(delayMs);
                }
            }
        }
    }
}
