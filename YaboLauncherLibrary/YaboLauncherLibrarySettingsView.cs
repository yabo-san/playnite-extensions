using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace YaboLauncherLibrary
{
    /// <summary>
    /// Two paths, each with a Browse button. Built in code rather than XAML: it is
    /// small, and it keeps the project buildable without the WPF markup compiler.
    /// Playnite sets the DataContext to the settings view model.
    /// </summary>
    public class YaboLauncherLibrarySettingsView : UserControl
    {
        public YaboLauncherLibrarySettingsView(Playnite.SDK.IPlayniteAPI api)
        {
            var panel = new StackPanel { Margin = new Thickness(20) };
            AddPath(panel, api, "playnite-export.json (empty: the launcher's app data folder)", "Settings.ExportPath",
                () => api.Dialogs.SelectFile("playnite-export.json|playnite-export.json|JSON|*.json"),
                YaboLauncherLibrarySettings.DefaultExportPath);
            AddPath(panel, api, "Launcher exe (empty: the installed y4bo launcher)", "Settings.LauncherExePath",
                () => api.Dialogs.SelectFile("Launcher|*.exe"),
                YaboLauncherLibrarySettings.DefaultLauncherExePath);
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Text = "The library refreshes whenever the launcher rewrites the export: after an install, uninstall, " +
                       "launch or any other library change, and on every Playnite library update.",
            });
            Content = panel;
        }

        private static void AddPath(Panel panel, Playnite.SDK.IPlayniteAPI api, string label, string binding,
            System.Func<string> browse, string placeholder)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4) });
            var row = new DockPanel();
            var button = new Button { Content = "Browse", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            DockPanel.SetDock(button, Dock.Right);
            var box = new TextBox { ToolTip = placeholder };
            box.SetBinding(TextBox.TextProperty, new Binding(binding) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            button.Click += (s, e) =>
            {
                var picked = browse();
                if (!string.IsNullOrEmpty(picked)) box.Text = picked;
            };
            row.Children.Add(button);
            row.Children.Add(box);
            panel.Children.Add(row);
        }
    }
}
