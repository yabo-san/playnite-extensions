using System.Windows.Controls;

namespace YaboLibrary
{
    /// <summary>
    /// WPF settings view returned from <see cref="YaboLibraryPlugin.GetSettingsView"/>.
    /// Its DataContext is set by Playnite to the <see cref="YaboLibrarySettingsViewModel"/>.
    /// </summary>
    public partial class YaboLibrarySettingsView : UserControl
    {
        public YaboLibrarySettingsView()
        {
            InitializeComponent();
        }
    }
}
