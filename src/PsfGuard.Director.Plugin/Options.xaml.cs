using System.ComponentModel.Composition;
using System.Windows;

namespace PsfGuard.Director.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options() => InitializeComponent();

    private void SettingsLoaded(object sender, RoutedEventArgs args) => RefreshEquipmentVisibility(sender);

    private void SettingsVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => RefreshEquipmentVisibility(sender);

    private void SettingsUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: DirectorPlugin plugin }) plugin.SetEquipmentSettingsVisible(false);
    }

    private static void RefreshEquipmentVisibility(object sender)
    {
        if (sender is FrameworkElement { DataContext: DirectorPlugin plugin } root)
        {
            plugin.SetEquipmentSettingsVisible(root.IsLoaded && root.IsVisible);
            if (root.IsLoaded && root.IsVisible) plugin.RefreshSessionDefaults();
        }
    }
}
