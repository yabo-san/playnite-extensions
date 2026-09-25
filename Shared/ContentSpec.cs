using System;
using System.Collections.Generic;
using System.Linq;

namespace Yabo.Shared
{
    /// <summary>
    /// Points at the bytes to download. The syntax is the one already used by
    /// `dev/ia-matches.json`, so existing owner-confirmed matches carry over verbatim
    /// rather than needing a migration:
    ///
    ///   "identifier"                       pick the largest archive in the item
    ///   "identifier::exact/file/path"      one named file inside a bundle item
    ///
    /// The second form exists because some items are bundles. `pd-i686-windows`
    /// carries several builds, and "Bomberman 64: Recompiled" keeps its archive at
    /// "Bomberman 64 Recompiled/Bomberman 64.zip". Largest-wins would pick wrong.
    /// </summary>
    public class ContentSpec
    {
        private const string Separator = "::";

        /// <summary>Archive formats worth auto-selecting, in no particular order.</summary>
        private static readonly string[] ArchiveExtensions = { ".7z", ".zip", ".rar" };

        public string Identifier { get; private set; }

        /// <summary>The exact file inside the item, or null to auto-select.</summary>
        public string FilePath { get; private set; }

        public bool IsExplicit => FilePath != null;

        public static ContentSpec Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("A content spec cannot be empty.", nameof(raw));
            }

            var text = raw.Trim();
            var cut = text.IndexOf(Separator, StringComparison.Ordinal);

            if (cut < 0)
            {
                return new ContentSpec { Identifier = text, FilePath = null };
            }

            var identifier = text.Substring(0, cut).Trim();
            // Only the FIRST separator splits. A path may legitimately contain "::"
            // and the identifier never can, so anything after the first one is path.
            var path = text.Substring(cut + Separator.Length).Trim();

            if (identifier.Length == 0)
            {
                throw new ArgumentException($"Content spec '{raw}' has no identifier before '::'.", nameof(raw));
            }

            if (path.Length == 0)
            {
                // "identifier::" is a truncated edit, not a request to auto-select.
                // Treating it as auto-select would silently download the wrong file.
                throw new ArgumentException($"Content spec '{raw}' ends with '::' but names no file.", nameof(raw));
            }

            return new ContentSpec { Identifier = identifier, FilePath = path };
        }

        /// <summary>
        /// Resolves this spec against an item's real file list.
        ///
        /// Returns null rather than throwing when nothing matches: an item whose
        /// archives were removed is a normal state to report in the UI, not an
        /// exceptional one. Two items in the curated set match no archive at all
        /// ("fav-rohankar", "RAZE_CM"), and the shell script this replaces created an
        /// empty directory and logged a failure for each.
        /// </summary>
        public string Resolve(IEnumerable<FileCandidate> files)
        {
            var list = (files ?? Enumerable.Empty<FileCandidate>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .ToList();

            if (IsExplicit)
            {
                // archive.org paths are case-sensitive in principle but are routinely
                // transcribed by hand into the match file, so compare loosely and
                // normalise the separator.
                return list
                    .FirstOrDefault(f => Normalize(f.Name).Equals(Normalize(FilePath), StringComparison.OrdinalIgnoreCase))
                    ?.Name;
            }

            return LargestArchive(list);
        }

        /// <summary>
        /// The biggest archive in a file list, or null if there is none.
        ///
        /// Exposed because the health checker needs it independently: when an explicit
        /// spec stops resolving, the question is whether SOME archive is still there,
        /// which distinguishes "upstream renamed the file" from "the archives are
        /// gone". Those have different remedies.
        /// </summary>
        public static string LargestArchive(IEnumerable<FileCandidate> files)
        {
            return (files ?? Enumerable.Empty<FileCandidate>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .Where(f => ArchiveExtensions.Any(e => f.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.SizeBytes)
                .FirstOrDefault()
                ?.Name;
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/').Trim('/');
        }

        public override string ToString()
        {
            return IsExplicit ? Identifier + Separator + FilePath : Identifier;
        }
    }

    /// <summary>A file as the source reported it. Deliberately not tied to archive.org.</summary>
    public class FileCandidate
    {
        public string Name { get; set; }
        public long SizeBytes { get; set; }
    }
}
