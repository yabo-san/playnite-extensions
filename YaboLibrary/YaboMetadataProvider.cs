using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace YaboLibrary
{
    /// <summary>
    /// The library's own metadata provider (the "Store" source). By re-emitting the curated
    /// <c>cover</c> from <c>--list-json</c> for our games, this keeps our box at the top of the
    /// Cover field's source list so a metadata download / re-download never clobbers it with
    /// SteamGridDB. Returns nothing for games we don't own or whose cover we can't resolve.
    /// </summary>
    public class YaboMetadataProvider : LibraryMetadataProvider
    {
        private readonly YaboLibraryPlugin plugin;

        public YaboMetadataProvider(YaboLibraryPlugin plugin)
        {
            this.plugin = plugin;
        }

        public override GameMetadata GetMetadata(Game game)
        {
            // game.GameId == our folderName (the GameId we set in GetGames). Look the cover back up
            // from the live catalog so it stays the authoritative box.
            var exe = plugin.SettingsViewModel?.Settings?.ExePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return null;
            }

            var catalog = YaboCli.GetCatalog(exe, CancellationToken.None);
            foreach (var entry in catalog)
            {
                if (string.Equals(entry.FolderName, game.GameId, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(entry.Cover))
                    {
                        return null;
                    }

                    return new GameMetadata
                    {
                        CoverImage = new MetadataFile(entry.Cover)
                    };
                }
            }

            return null;
        }
    }
}
