using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace YaboLauncherLibrary
{
    /// <summary>The two paths the plugin needs. Empty means the default.</summary>
    public class YaboLauncherLibrarySettings : ObservableObject
    {
        private string exportPath = string.Empty;
        private string launcherExePath = string.Empty;

        /// <summary>playnite-export.json. Default: next to library.db in the launcher's app data.</summary>
        public string ExportPath
        {
            get => exportPath;
            set => SetValue(ref exportPath, value);
        }

        /// <summary>The launcher's exe, run with --install / --uninstall / --launch.</summary>
        public string LauncherExePath
        {
            get => launcherExePath;
            set => SetValue(ref launcherExePath, value);
        }

        /// <summary>%APPDATA%\rohankar-launcher\playnite-export.json, where the launcher writes it.</summary>
        public static string DefaultExportPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rohankar-launcher", "playnite-export.json");

        /// <summary>Where the installer puts the launcher for one user.</summary>
        public static string DefaultLauncherExePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "rohankar-launcher", "RohanKar Launcher.exe");

        public string EffectiveExportPath => string.IsNullOrWhiteSpace(ExportPath) ? DefaultExportPath : ExportPath.Trim();
        public string EffectiveLauncherExePath => string.IsNullOrWhiteSpace(LauncherExePath) ? DefaultLauncherExePath : LauncherExePath.Trim();
    }

    public class YaboLauncherLibrarySettingsViewModel : ObservableObject, ISettings
    {
        private readonly YaboLauncherLibraryPlugin plugin;
        private YaboLauncherLibrarySettings editingClone;
        private YaboLauncherLibrarySettings settings;

        public YaboLauncherLibrarySettings Settings
        {
            get => settings;
            set => SetValue(ref settings, value);
        }

        public YaboLauncherLibrarySettingsViewModel() { }

        public YaboLauncherLibrarySettingsViewModel(YaboLauncherLibraryPlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<YaboLauncherLibrarySettings>() ?? new YaboLauncherLibrarySettings();
        }

        public void BeginEdit() => editingClone = Serialization.GetClone(Settings);

        public void CancelEdit() => Settings = editingClone;

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
            plugin.OnSettingsSaved();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            var exe = Settings.EffectiveLauncherExePath;
            if (!File.Exists(exe))
            {
                errors.Add($"The y4bo launcher was not found at {exe}. Set the launcher exe path.");
            }
            var dir = Path.GetDirectoryName(Settings.EffectiveExportPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                errors.Add($"The folder for playnite-export.json does not exist: {dir}");
            }
            return errors.Count == 0;
        }
    }
}
