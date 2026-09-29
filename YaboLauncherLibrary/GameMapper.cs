using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK.Models;
using Yabo.Shared;

namespace YaboLauncherLibrary
{
    /// <summary>
    /// One export record as Playnite GameMetadata. Pure: file checks come in as a
    /// function, so tests run without Playnite or the files.
    /// </summary>
    public static class GameMapper
    {
        public const string SourceName = "y4bo";
        public const string PlayActionName = "Play";
        public const string UpdateAvailableTag = "Update available";

        /// <param name="gameId">The GameId from <see cref="GameIdResolver"/>.</param>
        /// <param name="existing">This plugin's game with that GameId, or null for a new one.
        /// A cover or background already on it is never replaced.</param>
        public static GameMetadata ToMetadata(LauncherGame record, string gameId, KnownGame existing, Func<string, bool> fileExists = null)
        {
            fileExists = fileExists ?? File.Exists;
            var meta = new GameMetadata
            {
                GameId = gameId,
                Name = record.Name,
                Source = new MetadataNameProperty(SourceName),
                IsInstalled = record.Installed,
                InstallDirectory = record.Installed ? record.InstallDir : null,
                GameActions = PlayActions(record),
                Platforms = new HashSet<MetadataProperty> { Platform(record.Platform) },
                Playtime = record.PlaytimeSeconds,
                LastActivity = record.LastPlayed?.ToLocalTime(),
            };

            var tags = record.Tags.Select(t => (MetadataProperty)new MetadataNameProperty(t)).ToList();
            if (record.UpdateAvailable) tags.Add(new MetadataNameProperty(UpdateAvailableTag));
            if (tags.Count > 0) meta.Tags = new HashSet<MetadataProperty>(tags);

            if (existing?.HasCover != true && Exists(record.CoverPath, fileExists)) meta.CoverImage = new MetadataFile(record.CoverPath);
            if (existing?.HasBackground != true && Exists(record.HeroPath, fileExists)) meta.BackgroundImage = new MetadataFile(record.HeroPath);
            if (!string.IsNullOrWhiteSpace(record.Version)) meta.Version = record.Version;
            return meta;
        }

        /// <summary>The Play action: exe with args in workingDir. None until there is an exe.</summary>
        public static List<GameAction> PlayActions(LauncherGame record)
        {
            if (!record.Playable) return new List<GameAction>();
            return new List<GameAction>
            {
                new GameAction
                {
                    Name = PlayActionName,
                    Type = GameActionType.File,
                    Path = record.Exe,
                    Arguments = string.IsNullOrWhiteSpace(record.Args) ? null : record.Args,
                    WorkingDir = string.IsNullOrWhiteSpace(record.WorkingDir) ? Path.GetDirectoryName(record.Exe) : record.WorkingDir,
                    IsPlayAction = true,
                },
            };
        }

        /// <summary>PC is Playnite's pc_windows platform; the consoles are shelves, named as the launcher names them.</summary>
        public static MetadataProperty Platform(string platform)
        {
            switch (platform)
            {
                case "Nintendo":
                case "PlayStation":
                case "Xbox":
                    return new MetadataNameProperty(platform);
                case null:
                case "":
                case "PC":
                    return new MetadataSpecProperty("pc_windows");
                default:
                    return new MetadataNameProperty("Other");
            }
        }

        private static bool Exists(string path, Func<string, bool> fileExists)
        {
            return !string.IsNullOrWhiteSpace(path) && fileExists(path);
        }
    }
}
