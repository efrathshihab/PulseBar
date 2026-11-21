using System.Windows;
using System.Windows.Controls;
using PulseBar.ViewModels;
using Wpf.Ui.Controls;

namespace PulseBar.Views.Pages;

public partial class OverviewPage : UserControl
{
    public OverviewPage()
    {
        InitializeComponent();
    }

    /// <summary>"Enable" under CPU Temp: confirm, then restart as administrator.</summary>
    private async void EnableCpuTemp_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverviewViewModel vm || Window.GetWindow(this) is not { } window ||
            ContentDialogHost.GetForWindow(window) is not { } host)
        {
            return;
        }

        var dialog = new ContentDialog(host)
        {
            Title = "Show CPU temperature?",
            Content = "PulseBar needs to restart as administrator to read CPU temperature.",
            PrimaryButtonText = "Yes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 320,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            vm.EnableCpuTempCommand.Execute(null);
        }
    }
}
