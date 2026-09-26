using System.Windows;
using System.Windows.Controls;

namespace DropPlaynite
{
    /// <summary>
    /// WPF settings view. Its DataContext is the <see cref="DropSettingsViewModel"/>;
    /// the buttons call into the plugin because sign-in is a network flow, not a field.
    /// </summary>
    public partial class DropSettingsView : UserControl
    {
        public DropSettingsView()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                var vm = DataContext as DropSettingsViewModel;
                if (vm?.Settings == null) return;
                // PasswordBox cannot bind; seed it once and push changes back by hand.
                AdminTokenBox.Password = vm.Settings.AdminToken ?? string.Empty;
                RefreshUploadedCount();
            };
        }

        private DropSettingsViewModel Model => DataContext as DropSettingsViewModel;

        private void SignIn_Click(object sender, RoutedEventArgs e)
        {
            var vm = Model;
            if (vm == null) return;
            vm.Plugin.SignInInteractive(vm);
            vm.RefreshStatus();
        }

        private void SignOut_Click(object sender, RoutedEventArgs e)
        {
            var vm = Model;
            if (vm == null) return;
            vm.Settings.ClientId = string.Empty;
            vm.Settings.PrivateKeyPem = string.Empty;
            vm.Save();
            vm.RefreshStatus();
        }

        private void AdminToken_Changed(object sender, RoutedEventArgs e)
        {
            var vm = Model;
            if (vm?.Settings == null) return;
            vm.Settings.AdminToken = AdminTokenBox.Password;
        }

        private void ClearUploaded_Click(object sender, RoutedEventArgs e)
        {
            var vm = Model;
            if (vm?.Settings == null) return;
            vm.Settings.UploadedGames.Clear();
            vm.Save();
            RefreshUploadedCount();
        }

        private void RefreshUploadedCount()
        {
            var n = Model?.Settings?.UploadedGames?.Count ?? 0;
            UploadedCount.Text = n == 0 ? "Nothing sent yet" : n + " game(s) sent from here";
        }
    }
}
