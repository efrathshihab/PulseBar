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
/// Tray এর পাশে খোলা Fluent popup। একবারই বানানো হয়, এরপর শুধু Hide/Show।
/// </summary>
public partial class PopupWindow : FluentWindow
{
    // Popup খোলা অবস্থায় tray icon এ click করলে আগে Deactivated চলে popup লুকায়,
    // তারপর click event আসে। এই সময়ের মধ্যে আসা click কে "আবার খোলো" ধরা হবে না।
    private const long ReopenGuardMs = 300;

    private long _lastHiddenAt;
    private bool _allowClose;

    public PopupWindow(OverviewViewModel overview, ClipboardViewModel clipboard)
    {
        InitializeComponent();
        OverviewTab.DataContext = overview;
        ClipboardTab.DataContext = clipboard;
        Deactivated += (_, _) => HidePopup();
        Tabs.SizeChanged += (_, _) => SpreadTabs();
    }

    /// <summary>৪টা tab কে পুরো চওড়ায় সমানভাবে ছড়ায়।</summary>
    private void SpreadTabs()
    {
        int count = Tabs.Items.Count;
        if (count == 0 || Tabs.ActualWidth <= 0)
        {
            return;
        }

        // TabItem এর নিজস্ব margin বাদ দিয়ে, rounding এর জন্য ১px কম, যাতে দ্বিতীয় সারিতে না যায়
        foreach (TabItem tab in Tabs.Items)
        {
            double margin = tab.Margin.Left + tab.Margin.Right;
            tab.Width = Math.Max(0, Math.Floor(Tabs.ActualWidth / count - margin) - 1);

            // WPF-UI এর template এ header এর presenter বাম দিকে বাঁধা থাকে, তাই মাঝখানে আনা হয়
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
        // প্রথমবার handle বানানোর জন্য পর্দার বাইরে Show, তারপর সঠিক জায়গায় সরানো।
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            Left = -10000;
            Top = -10000;
        }

        // Show এর আগে, যাতে ViewModel সর্বশেষ মান আগেই বসাতে পারে
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

    /// <summary>App বন্ধ হওয়ার সময় সত্যিই window close করতে দেয়।</summary>
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
        // Alt+F4 ইত্যাদিতে window নষ্ট না করে শুধু লুকানো হবে।
        if (!_allowClose)
        {
            e.Cancel = true;
            HidePopup();
        }

        base.OnClosing(e);
    }
}
