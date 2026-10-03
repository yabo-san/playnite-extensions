using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace RohanKarPlaynite
{
    /// <summary>The two paths the plugin needs. Empty means the default.</summary>
    public class RohanKarSettings : ObservableObject
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

        /// <summary>
        /// Where the installer puts the launcher for one user. The fork's exe is y4bo.exe
        /// (productName "y4bo"); the chezmoi install on the owner's PC keeps the upstream folder
        /// name, and an upstream install is still "RohanKar Launcher.exe". First one that exists wins;
        /// if none does, the first candidate is reported so the error names a real path.
        /// </summary>
        public static string DefaultLauncherExePath
        {
            get
            {
                var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
                var candidates = new[]
                {
                    Path.Combine(programs, "rohankar-launcher", "y4bo.exe"),
                    Path.Combine(programs, "y4bo", "y4bo.exe"),
                    Path.Combine(programs, "rohankar-launcher", "RohanKar Launcher.exe"),
                };
                foreach (var c in candidates) if (File.Exists(c)) return c;
                return candidates[0];
            }
        }

        public string EffectiveExportPath => string.IsNullOrWhiteSpace(ExportPath) ? DefaultExportPath : ExportPath.Trim();
        public string EffectiveLauncherExePath => string.IsNullOrWhiteSpace(LauncherExePath) ? DefaultLauncherExePath : LauncherExePath.Trim();
    }

    public class RohanKarSettingsViewModel : ObservableObject, ISettings
    {
        private readonly RohanKarLibraryPlugin plugin;
        private RohanKarSettings editingClone;
        private RohanKarSettings settings;

        public RohanKarSettings Settings
        {
            get => settings;
            set => SetValue(ref settings, value);
        }

        public RohanKarSettingsViewModel() { }

        public RohanKarSettingsViewModel(RohanKarLibraryPlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<RohanKarSettings>() ?? new RohanKarSettings();
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
