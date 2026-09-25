using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Yabo.Shared
{
    /// <summary>
    /// Reads a version out of a filename so an Internet Archive item can behave like
    /// GitHub's `releases/latest`.
    ///
    /// GitHub has a real latest-release endpoint; an archive.org item does not. It is a
    /// flat bag of files, so "keep this up to date" has to be inferred from names:
    ///
    ///     Dusklight (v1.4.1).zip     -> 1.4.1
    ///     Dusklight (v1.0).zip       -> 1.0
    ///
    /// RepackAdopter already strips these tokens to clean up display names. This reads
    /// the same tokens for the opposite purpose, so the two stay consistent about what
    /// counts as a version.
    ///
    /// Size is NOT a proxy for recency. It works for Dusklight by coincidence (977 MB
    /// vs 973 MB) and fails silently the first time a newer build compresses better.
    /// </summary>
    public static class FileVersionToken
    {
        // Ordered: the most specific pattern wins, so "v1.4.1" is not read as "1.4".
        //
        // Every pattern ends in a LOOKAHEAD rather than consuming a delimiter. An
        // earlier version padded the input with a space so a delimiter always
        // followed, which then broke any pattern anchored on end-of-string: "Thing v3"
        // matched nothing. Lookahead lets "delimiter or end" be one condition.
        private static readonly Regex[] Patterns =
        {
            // v1.4.1 / v1.4 / 1.4.1, optionally parenthesised or bracketed.
            new Regex(@"[\(\[\s_\-]v?(?<v>\d+(?:\.\d+){1,3})(?=[\)\]\s_\-\.]|$)",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // A bare v-prefixed integer: "Thing v3". Requires the `v`, so a stray
            // number in a title is not read as a version.
            new Regex(@"[\(\[\s_\-]v(?<v>\d+)(?=[\)\]\s_\-\.]|$)",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Revision style: r123, rev12, build 45
            new Regex(@"[\(\[\s_\-](?:r|rev|build)[\s_\-]?(?<r>\d+)(?=[\)\]\s_\-\.]|$)",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase),
        };

        // A date stamp, which several curated identifiers use (_202601, _20260115).
        // Kept separate because it must never outrank a real semantic version.
        private static readonly Regex DateStamp =
            new Regex(@"[\(\[\s_\-](?<d>20\d{2}(?:0[1-9]|1[0-2])(?:[0-3]\d)?)(?![\d])", RegexOptions.Compiled);

        /// <summary>
        /// A comparable version, or null when the name carries none. Returns null
        /// rather than a zero version so callers can tell "no version" from "v0".
        /// </summary>
        public static Version Parse(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            // Compare against the stem: an extension like ".7z" must not be read as a
            // version fragment, and a trailing-version pattern needs a real end anchor.
            var stem = StripExtension(fileName);

            foreach (var pattern in Patterns)
            {
                var m = pattern.Match(stem);
                if (!m.Success)
                {
                    continue;
                }

                if (m.Groups["v"].Success && TryVersion(m.Groups["v"].Value, out var v))
                {
                    return v;
                }

                if (m.Groups["r"].Success && int.TryParse(m.Groups["r"].Value, out var rev))
                {
                    // A bare revision is ordered among other revisions only. Putting it
                    // in the build field keeps it below any real major.minor.
                    return new Version(0, 0, 0, rev);
                }
            }

            var date = DateStamp.Match(stem);
            if (date.Success)
            {
                var raw = date.Groups["d"].Value;
                var year = int.Parse(raw.Substring(0, 4), CultureInfo.InvariantCulture);
                var month = int.Parse(raw.Substring(4, 2), CultureInfo.InvariantCulture);
                var day = raw.Length >= 8 ? int.Parse(raw.Substring(6, 2), CultureInfo.InvariantCulture) : 0;
                return new Version(year, month, day);
            }

            return null;
        }

        /// <summary>
        /// The newest of a set of candidates, or null when none carries a version.
        ///
        /// Ties break on size, because two files at the same version are usually the
        /// same build packaged twice and the larger is the more complete one.
        /// </summary>
        public static FileCandidate Newest(IEnumerable<FileCandidate> files)
        {
            return (files ?? Enumerable.Empty<FileCandidate>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .Select(f => new { File = f, Version = Parse(f.Name) })
                .Where(x => x.Version != null)
                .OrderByDescending(x => x.Version)
                .ThenByDescending(x => x.File.SizeBytes)
                .FirstOrDefault()
                ?.File;
        }

        private static bool TryVersion(string raw, out Version version)
        {
            // System.Version needs at least major.minor; "2" alone is legal in a
            // filename but not to the parser.
            var text = raw.Contains(".") ? raw : raw + ".0";
            return Version.TryParse(text, out version);
        }

        private static string StripExtension(string name)
        {
            var dot = name.LastIndexOf('.');
            // Only treat a short trailing run as an extension: "Dusklight (v1.4.1)"
            // ends in a dot-number and must keep it.
            return dot > 0 && name.Length - dot <= 5 && !char.IsDigit(name[dot + 1])
                ? name.Substring(0, dot)
                : name;
        }
    }
}
