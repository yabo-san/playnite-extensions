using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace QuiverPlaynite
{
    /// <summary>
    /// Persisted plugin settings, stored through Playnite's
    /// <c>SavePluginSettings</c> / <c>LoadPluginSettings</c> via
    /// <see cref="QuiverSettingsViewModel"/>.
    /// </summary>
    public class QuiverSettings : ObservableObject
    {
        private string quiverRoot = string.Empty;
        private bool launchThroughQuiver = false;

        /// <summary>
        /// Quiver's data root: the folder holding apps.json and Apps\. Empty means
        /// auto-detect, which covers the Velopack install at
        /// %LOCALAPPDATA%\QuiverLauncher. A portable build keeps its data beside its
        /// exe, and that is what this override is for.
        /// </summary>
        public string QuiverRoot
        {
            get => quiverRoot;
            set => SetValue(ref quiverRoot, value);
        }

        /// <summary>
        /// When true, Play runs <c>QuiverLauncher --run "&lt;name&gt;"</c>, which
        /// updates the app first if a newer release exists. When false (the default),
        /// Play runs the app's executable directly, which is faster and works with
        /// Playnite's own playtime tracking and overlays.
        /// </summary>
        public bool LaunchThroughQuiver
        {
            get => launchThroughQuiver;
            set => SetValue(ref launchThroughQuiver, value);
        }
    }

    /// <summary>
    /// The <see cref="ISettings"/> view-model Playnite edits through. Snapshots for
    /// cancel, persists on save, validates the override if one is given.
    /// </summary>
    public class QuiverSettingsViewModel : ObservableObject, ISettings
    {
        private readonly QuiverLibraryPlugin plugin;
        private QuiverSettings editingClone;
        private QuiverSettings settings;

        public QuiverSettings Settings
        {
            get => settings;
            set => SetValue(ref settings, value);
        }

        // Parameterless ctor for the serializer.
        public QuiverSettingsViewModel() { }

        public QuiverSettingsViewModel(QuiverLibraryPlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<QuiverSettings>() ?? new QuiverSettings();
        }

        public void BeginEdit()
        {
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

            // Blank is fine (auto-detect). A path that is set has to be Quiver's root,
            // and apps.json is the one file that proves it.
            if (!string.IsNullOrWhiteSpace(Settings.QuiverRoot)
                && !File.Exists(Path.Combine(Settings.QuiverRoot, "apps.json")))
            {
                errors.Add($"No apps.json under: {Settings.QuiverRoot}. Point this at Quiver's data folder, or leave it blank to auto-detect.");
            }

            return errors.Count == 0;
        }
    }
}
