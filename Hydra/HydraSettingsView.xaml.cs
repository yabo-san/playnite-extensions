using System.Windows.Controls;

namespace HydraPlaynite
{
    /// <summary>
    /// WPF settings view returned from <see cref="HydraLibraryPlugin.GetSettingsView"/>.
    /// Playnite sets its DataContext to the <see cref="HydraSettingsViewModel"/>.
    /// </summary>
    public partial class HydraSettingsView : UserControl
    {
        public HydraSettingsView()
        {
            InitializeComponent();
        }
    }
}
