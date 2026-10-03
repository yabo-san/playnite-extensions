using System;
using System.Text.RegularExpressions;

namespace YaboLibrary
{
    /// <summary>
    /// Keeps capability URLs out of Playnite's logs.
    ///
    /// The feed is distributed as an unlisted gist, so the URL IS the credential:
    /// anyone holding it can read the feed without authenticating. That makes
    /// `extensions.log` a disclosure surface, because it is the first thing anyone
    /// pastes into a bug report or a screenshot.
    ///
    /// The host is kept because it is what makes a log line useful (DNS failure,
    /// TLS failure, wrong service). The path and query are what identify the
    /// specific secret, so they go.
    /// </summary>
    internal static class Redact
    {
        private static readonly Regex UrlPattern = new Regex(
            @"\b(?<scheme>https?)://(?<host>[^\s/?#""']+)(?<rest>[^\s""']*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Replaces the path and query of every URL in <paramref name="text"/> with
        /// a marker, leaving scheme and host intact. Returns the input unchanged if
        /// it contains no URL, so it is safe to wrap any log argument.
        /// </summary>
        public static string Urls(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return UrlPattern.Replace(text, m =>
            {
                var rest = m.Groups["rest"].Value;

                // "https://host" and "https://host/" carry nothing secret, so
                // rewriting them would only make logs harder to read.
                if (rest.Length == 0 || rest == "/")
                {
                    return m.Value;
                }

                return m.Groups["scheme"].Value + "://" + m.Groups["host"].Value + "/<redacted>";
            });
        }

        /// <summary>
        /// Describes a feed location for a log line. A local path is safe to print
        /// in full; a URL is not, because only the remote form is a capability.
        /// </summary>
        public static string Location(string pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
            {
                return "(none configured)";
            }

            return pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? Urls(pathOrUrl)
                : pathOrUrl;
        }
    }
}
