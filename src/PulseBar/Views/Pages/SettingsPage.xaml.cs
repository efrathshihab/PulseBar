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
            Content = "সব setting আর hotkey প্রথম অবস্থায় ফিরবে, launch at login বন্ধ হবে। Clipboard history মুছবে না।",
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
