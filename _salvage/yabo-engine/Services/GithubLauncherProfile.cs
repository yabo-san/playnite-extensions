using GitHubLauncher.Core.Services;

namespace GithubLauncher.Services
{
    public sealed class GithubLauncherProfile : LauncherProfile
    {
        public static GithubLauncherProfile Instance { get; } = new();

        public override string DisplayName => "Github Launcher";
        public override string ApplicationId => "GithubLauncher";
        public override string Repository => "yabo-san/yabo-launcher";
        public override string ExecutableName => "GithubLauncher";
        public override string DefaultInstallFolderName => "Apps";
        public override string UserAgent => "GithubLauncher/1.0";
        public override string CliUserAgent => "GithubLauncher-CLI";
        public override string UpdaterUserAgent => "GithubLauncher-Updater";
        public override string SteamTag => "Github Launcher";
    }
}
