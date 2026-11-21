using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PulseBar.Models;
using PulseBar.Utilities;
using PulseBar.ViewModels;
using Wpf.Ui.Controls;

namespace PulseBar.Views;

/// <summary>
/// The Fluent popup next to the tray. Created once, then only Hide/Show.
/// </summary>
public partial class PopupWindow : FluentWindow
{
    // Clicking the tray icon while the popup is open first deactivates and hides the popup,
    // then the click event arrives. A click within this time doesn't count as "open again".
    private const long ReopenGuardMs = 300;

    private long _lastHiddenAt;
    private bool _allowClose;

    public PopupWindow(OverviewViewModel overview, SoundViewModel sound, ClipboardViewModel clipboard, SettingsViewModel settings)
    {
        InitializeComponent();
        OverviewTab.DataContext = overview;
        SoundTab.DataContext = sound;
        ClipboardTab.DataContext = clipboard;
        SettingsTab.DataContext = settings;

        if (BuildInfo.DevLabel is { } dev)
        {
            DevLabel.Text = dev;
            DevLabel.Visibility = Visibility.Visible;
        }

        Deactivated += (_, _) => HidePopup();
        Tabs.SizeChanged += (_, _) => SpreadTabs();
    }

    /// <summary>Spreads the 4 tabs evenly across the full width.</summary>
    private void SpreadTabs()
    {
        int count = Tabs.Items.Count;
        if (count == 0 || Tabs.ActualWidth <= 0)
        {
            return;
        }

        // Minus the TabItem's own margin, and 1px less for rounding, so it doesn't wrap to a second row
        foreach (TabItem tab in Tabs.Items)
        {
            double margin = tab.Margin.Left + tab.Margin.Right;
            tab.Width = Math.Max(0, Math.Floor(Tabs.ActualWidth / count - margin) - 1);

            // WPF-UI's template pins the header presenter to the left, so it is centered
            tab.ApplyTemplate();
            if (tab.Template?.FindName("ContentSite", tab) is FrameworkElement site)
            {
                site.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            HidePopup();
            return;
        }

        if (Environment.TickCount64 - _lastHiddenAt < ReopenGuardMs)
        {
            return;
        }

        ShowPopup();
    }

    public void ShowPopup()
    {
        // The first time, Show off-screen to create the handle, then move to the right place.
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            Left = -10000;
            Top = -10000;
        }

        // Before Show, so the ViewModel can set the latest values first
        AppState.Current.IsPopupVisible = true;

        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        PopupPlacement.PlaceNearTray(hwnd, Width, Height);

        Activate();
        PopupPlacement.BringToForeground(hwnd);
    }

    public void HidePopup()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();
        AppState.Current.IsPopupVisible = false;
        _lastHiddenAt = Environment.TickCount64;
    }

    /// <summary>Lets the window really close when the app shuts down.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePopup();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 and the like only hide the window instead of destroying it.
        if (!_allowClose)
        {
            e.Cancel = true;
            HidePopup();
        }

        base.OnClosing(e);
    }
}
