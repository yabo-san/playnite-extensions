using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace YaboLibrary
{
    /// <summary>
    /// Persisted plugin settings. Stored/loaded via Playnite's
    /// <see cref="Playnite.SDK.Plugins.Plugin.SavePluginSettings{T}"/> /
    /// <c>LoadPluginSettings&lt;T&gt;</c> using <see cref="YaboLibrarySettingsViewModel"/>.
    /// </summary>
    public class YaboLibrarySettings : ObservableObject
    {
        private string exePath = string.Empty;
        private string gamesPath = string.Empty;
        private string isnesrevPath = string.Empty;
        private bool importHidden = false;

        /// <summary>Absolute path to <c>yabo-launcher.exe</c> (the engine CLI we shell out to).</summary>
        public string ExePath
        {
            get => exePath;
            set => SetValue(ref exePath, value);
        }

        /// <summary>
        /// Absolute path to <c>isnesrev.exe</c> — the snesrev companion app that opens the per-game
        /// GLSL-shader and controls editors (launched with <c>--game &lt;folderName&gt; --tab shaders|controls</c>).
        /// Empty = use the default beside the engine (<c>&lt;engineDir&gt;\isnesrev\isnesrev.exe</c>), resolved
        /// at launch time. Only consumed by snesrev-native cards; other cards never touch it.
        /// </summary>
        public string IsnesrevPath
        {
            get => isnesrevPath;
            set => SetValue(ref isnesrevPath, value);
        }

        /// <summary>
        /// Where downloaded games install. Empty = use a sensible default — a "yabo-games" folder NEXT TO
        /// Playnite (PlayniteApi.Paths.ApplicationPath), NOT inside the Extensions folder (so a stranger's
        /// 50&#160;GB of games don't land buried under Extensions\YaboLibrary\). The plugin passes this to the
        /// engine as its AppsPath. Set per-machine; the bundle's first run defaults + can prompt for a drive.
        /// </summary>
        public string GamesPath
        {
            get => gamesPath;
            set => SetValue(ref gamesPath, value);
        }

        /// <summary>When true, ports the engine reports as hidden are still imported as Playnite games.</summary>
        public bool ImportHidden
        {
            get => importHidden;
            set => SetValue(ref importHidden, value);
        }
    }

    /// <summary>
    /// The <see cref="ISettings"/> view-model Playnite edits through. Wraps a plain
    /// <see cref="YaboLibrarySettings"/>, snapshots it for edit/cancel, persists on save,
    /// and validates the configured exe path.
    /// </summary>
    public class YaboLibrarySettingsViewModel : ObservableObject, ISettings
    {
        private readonly YaboLibraryPlugin plugin;
        private YaboLibrarySettings editingClone;
        private YaboLibrarySettings settings;

        public YaboLibrarySettings Settings
        {
            get => settings;
            set => SetValue(ref settings, value);
        }

        // Parameterless ctor for the serializer.
        public YaboLibrarySettingsViewModel() { }

        public YaboLibrarySettingsViewModel(YaboLibraryPlugin plugin)
        {
            this.plugin = plugin;

            var saved = plugin.LoadPluginSettings<YaboLibrarySettings>();
            Settings = saved ?? new YaboLibrarySettings();
        }

        public void BeginEdit()
        {
            // Snapshot so CancelEdit can restore.
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Settings.ExePath))
            {
                errors.Add("Set the path to yabo-launcher.exe.");
            }
            else if (!File.Exists(Settings.ExePath))
            {
                errors.Add($"yabo-launcher.exe not found at: {Settings.ExePath}");
            }

            return errors.Count == 0;
        }
    }
}
