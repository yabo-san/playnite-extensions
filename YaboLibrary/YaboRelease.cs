using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace YaboLibrary
{
    /// <summary>
    /// The top-level <c>release</c> block the engine writes into its feed file (<c>apps.json</c>,
    /// which sits beside <c>yabo-launcher.exe</c>) after the owner publishes. Shape:
    /// <c>{ version, date, title, notes, added: [names] }</c>. Used to raise a one-shot
    /// "patch-note bell" notification in Playnite when a new release version appears.
    /// </summary>
    public class YaboReleaseInfo
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("date")] public string Date { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("notes")] public string Notes { get; set; }
        [JsonProperty("added")] public System.Collections.Generic.List<string> Added { get; set; }
            = new System.Collections.Generic.List<string>();
    }

    /// <summary>
    /// Reads the engine feed's <c>release</c> block and tracks the last version we already
    /// surfaced to the user, so the patch-note notification fires exactly once per release.
    /// </summary>
    public static class YaboReleaseFeed
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Small marker file under GetPluginUserDataPath() recording the last release.version we showed.
        private const string LastShownFileName = "last-release-shown.txt";

        /// <summary>
        /// Read the <c>release</c> block from the <c>apps.json</c> that sits beside the engine exe.
        /// Returns null when the engine exe path is unset/missing, the feed file is absent, the
        /// <c>release</c> block isn't present (engine hasn't published), or parsing fails — every
        /// failure is non-fatal and yields "no release to show".
        /// </summary>
        public static YaboReleaseInfo ReadRelease(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return null;
            }

            string feedPath;
            try
            {
                var dir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return null;
                }
                feedPath = Path.Combine(dir, "apps.json");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: could not derive apps.json path from engine exe path.");
                return null;
            }

            if (!File.Exists(feedPath))
            {
                return null;
            }

            try
            {
                var text = File.ReadAllText(feedPath);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var root = JToken.Parse(text);
                var releaseToken = root.Type == JTokenType.Object ? root["release"] : null;
                if (releaseToken == null || releaseToken.Type != JTokenType.Object)
                {
                    return null; // engine hasn't published a release block yet
                }

                var release = releaseToken.ToObject<YaboReleaseInfo>();
                if (release == null || string.IsNullOrWhiteSpace(release.Version))
                {
                    return null; // a release block with no version can't be gated/de-duped
                }

                return release;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Yabo: failed to read/parse release block from '{feedPath}'.");
                return null;
            }
        }

        /// <summary>The version string of the last release we already showed a notification for (or null).</summary>
        public static string GetLastShownVersion(string pluginUserDataPath)
        {
            try
            {
                var path = Path.Combine(pluginUserDataPath, LastShownFileName);
                if (!File.Exists(path))
                {
                    return null;
                }
                var v = File.ReadAllText(path).Trim();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: failed to read last-shown release marker.");
                return null;
            }
        }

        /// <summary>Persist the version we just showed so the same release never notifies twice.</summary>
        public static void SetLastShownVersion(string pluginUserDataPath, string version)
        {
            try
            {
                Directory.CreateDirectory(pluginUserDataPath);
                var path = Path.Combine(pluginUserDataPath, LastShownFileName);
                File.WriteAllText(path, version ?? string.Empty);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Yabo: failed to persist last-shown release marker.");
            }
        }
    }
}
