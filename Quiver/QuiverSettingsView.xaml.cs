using System.Windows.Controls;

namespace QuiverPlaynite
{
    /// <summary>
    /// WPF settings view returned from <see cref="QuiverLibraryPlugin.GetSettingsView"/>.
    /// Playnite sets its DataContext to the <see cref="QuiverSettingsViewModel"/>.
    /// </summary>
    public partial class QuiverSettingsView : UserControl
    {
        public QuiverSettingsView()
        {
            InitializeComponent();
        }
    }
}
