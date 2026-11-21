using System.Windows;
using System.Windows.Controls;
using PulseBar.ViewModels;
using Wpf.Ui.Controls;

namespace PulseBar.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || Window.GetWindow(this) is not { } window ||
            ContentDialogHost.GetForWindow(window) is not { } host)
        {
            return;
        }

        var dialog = new ContentDialog(host)
        {
            Title = "Reset to defaults?",
            Content = "All settings and hotkeys return to their defaults and launch at login turns off. Clipboard history is kept.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            DialogMaxWidth = 320,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await vm.ResetToDefaultsAsync();
        }
    }
}
