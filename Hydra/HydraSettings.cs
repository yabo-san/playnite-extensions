using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace HydraPlaynite
{
    /// <summary>
    /// One setting: where Node is. Empty means look in the usual places (PATH, the
    /// nodejs.org installer's folders, the scoop shim). The library reader is a
    /// Node script, so without Node there is no import, and "the system cannot find
    /// the file specified" is the error that used to hide that.
    /// </summary>
    public class HydraSettings : ObservableObject
    {
        private string nodePath = string.Empty;

        /// <summary>Absolute path to node.exe. Empty = auto-detect.</summary>
        public string NodePath
        {
            get => nodePath;
            set => SetValue(ref nodePath, value);
        }
    }

    public class HydraSettingsViewModel : ObservableObject, ISettings
    {
        private readonly HydraLibraryPlugin plugin;
        private HydraSettings editingClone;
        private HydraSettings settings;

        public HydraSettings Settings
        {
            get => settings;
            set => SetValue(ref settings, value);
        }

        // Parameterless ctor for the serializer.
        public HydraSettingsViewModel() { }

        public HydraSettingsViewModel(HydraLibraryPlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<HydraSettings>() ?? new HydraSettings();
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
            if (!string.IsNullOrWhiteSpace(Settings.NodePath) && !File.Exists(Settings.NodePath))
            {
                errors.Add($"node.exe not found at: {Settings.NodePath}. Leave it blank to auto-detect.");
            }
            return errors.Count == 0;
        }
    }
}
