using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace DropPlaynite
{
    /// <summary>
    /// Persisted plugin settings, stored through Playnite's plugin settings file.
    ///
    /// The private key lives here too. That is the same place the Drop desktop app
    /// keeps it (its own database in the user's profile); a Playnite plugin has no
    /// better vault than the profile it runs in, and the key only signs ten-second
    /// tokens for this one instance.
    /// </summary>
    public class DropSettings : ObservableObject
    {
        private string baseUrl = string.Empty;
        private string clientId = string.Empty;
        private string privateKeyPem = string.Empty;
        private string installRoot = string.Empty;
        private string adminToken = string.Empty;
        private string librarySharePath = string.Empty;
        private string libraryId = string.Empty;
        private bool hideUploaded = true;
        private List<string> uploadedGames = new List<string>();

        /// <summary>The instance, e.g. <c>https://drop.example.com</c>. No trailing path.</summary>
        public string BaseUrl { get => baseUrl; set => SetValue(ref baseUrl, value); }

        /// <summary>Handed out at handshake; the first half of the JWT header.</summary>
        public string ClientId { get => clientId; set => SetValue(ref clientId, value); }

        /// <summary>EC private key PEM from the handshake. Never shown in the UI.</summary>
        public string PrivateKeyPem { get => privateKeyPem; set => SetValue(ref privateKeyPem, value); }

        /// <summary>Where games install. Empty = %LOCALAPPDATA%\Playnite\Drop.</summary>
        public string InstallRoot { get => installRoot; set => SetValue(ref installRoot, value); }

        /// <summary>Optional. A system-mode API token with the import ACLs; enables "Send to Drop".</summary>
        public string AdminToken { get => adminToken; set => SetValue(ref adminToken, value); }

        private string authScheme = string.Empty;
        /// <summary>"Nonce" (Drop 0.3.x, default) or "JWT" (newer Drop); learned automatically.</summary>
        public string AuthScheme { get => authScheme; set => SetValue(ref authScheme, value); }

        /// <summary>Optional. The Drop library root as this machine sees it, e.g. a UNC share.</summary>
        public string LibrarySharePath { get => librarySharePath; set => SetValue(ref librarySharePath, value); }

        /// <summary>Optional. Pins which Drop library the share is; otherwise looked up from the unimported list.</summary>
        public string LibraryId { get => libraryId; set => SetValue(ref libraryId, value); }

        /// <summary>
        /// Owner's rule: the admin who sent a game already has it as a Playnite card,
        /// so the Drop copy must not import as a second one.
        /// </summary>
        public bool HideUploaded { get => hideUploaded; set => SetValue(ref hideUploaded, value); }

        /// <summary>Names and, once known, Drop ids of games sent from this Playnite.</summary>
        public List<string> UploadedGames { get => uploadedGames; set => SetValue(ref uploadedGames, value); }

        public bool IsSignedIn => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(PrivateKeyPem);

        public string EffectiveInstallRoot =>
            string.IsNullOrWhiteSpace(InstallRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite", "Drop")
                : InstallRoot;

        public bool CanSend => !string.IsNullOrWhiteSpace(AdminToken) && !string.IsNullOrWhiteSpace(LibrarySharePath);
    }

    /// <summary>
    /// The <see cref="ISettings"/> view-model Playnite edits through: snapshot for
    /// cancel, persist on save, validate the URL. The view's buttons reach the plugin
    /// through <see cref="Plugin"/>.
    /// </summary>
    public class DropSettingsViewModel : ObservableObject, ISettings
    {
        private DropSettings editingClone;
        private DropSettings settings;

        public DropLibraryPlugin Plugin { get; }

        public DropSettings Settings
        {
            get => settings;
            set { SetValue(ref settings, value); OnPropertyChanged(nameof(SignInStatus)); }
        }

        public string SignInStatus => Settings != null && Settings.IsSignedIn
            ? "Signed in as client " + Settings.ClientId
            : "Not signed in";

        // Parameterless ctor for the serializer.
        public DropSettingsViewModel() { }

        public DropSettingsViewModel(DropLibraryPlugin plugin)
        {
            Plugin = plugin;
            Settings = plugin.LoadPluginSettings<DropSettings>() ?? new DropSettings();
        }

        public void RefreshStatus() => OnPropertyChanged(nameof(SignInStatus));

        public void BeginEdit() => editingClone = Serialization.GetClone(Settings);

        public void CancelEdit() => Settings = editingClone;

        public void EndEdit() => Plugin.SavePluginSettings(Settings);

        /// <summary>Persist outside an edit session, for the sign-in flow and the uploaded list.</summary>
        public void Save() => Plugin.SavePluginSettings(Settings);

        public bool VerifySettings(out List<string> errors)
        {
            // Pasted values often carry a stray space; a path with one never exists,
            // the check fails and Playnite discards every change in the dialog.
            Settings.BaseUrl = Settings.BaseUrl?.Trim();
            Settings.InstallRoot = Settings.InstallRoot?.Trim();
            Settings.LibrarySharePath = Settings.LibrarySharePath?.Trim();
            Settings.AdminToken = Settings.AdminToken?.Trim();
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
            {
                errors.Add("Set the Drop instance URL.");
            }
            else if (!Uri.TryCreate(Settings.BaseUrl.Trim(), UriKind.Absolute, out var uri)
                     || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                errors.Add("The Drop URL must start with https:// (or http:// on a LAN).");
            }
            if (!string.IsNullOrWhiteSpace(Settings.LibrarySharePath) && !Directory.Exists(Settings.LibrarySharePath))
            {
                errors.Add("The library share path does not exist from this machine: " + Settings.LibrarySharePath);
            }
            return errors.Count == 0;
        }
    }
}
