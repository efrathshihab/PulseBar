using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;
using PulseBar.ViewModels;
using PulseBar.Views.Controls;
using Wpf.Ui.Controls;

namespace PulseBar.Views;

/// <summary>
/// Hotkey এর clipboard panel। একবারই বানানো হয়, এরপর শুধু Hide/Show।
/// Enter/click: copy, বন্ধ, আগের window এ focus ফিরিয়ে Ctrl+V (Windows এর Win+V এর মতো)।
/// Shift+Enter: শুধু copy। Esc, আবার hotkey বা বাইরে click: বন্ধ।
/// </summary>
public partial class ClipboardPanel : FluentWindow
{
    private static readonly TimeSpan BlockedNoticeTime = TimeSpan.FromSeconds(1.5);

    private readonly ClipboardViewModel _viewModel;
    private readonly PasteService _paste;
    private readonly AppSettings _settings;
    private int _showCount;
    private bool _busy;
    private bool _allowClose;

    public ClipboardPanel(ClipboardViewModel viewModel, PasteService paste, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _paste = paste;
        _settings = settings;

        List.DataContext = viewModel;
        List.ItemActivated += OnItemActivated;
        Deactivated += (_, _) => HidePanel(restoreFocus: false);
    }

    /// <summary>
    /// Startup এ পর্দার বাইরে, focus না নিয়ে একবার দেখিয়ে লুকায়, যাতে window, template আর
    /// Mica আগেই তৈরি থাকে আর প্রথম hotkey তেও panel দ্রুত খোলে।
    /// </summary>
    public void Prewarm()
    {
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            return;
        }

        Left = -10000;
        Top = -10000;
        ShowActivated = false;
        Show();
        Hide();
        ShowActivated = true;
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            HidePanel(restoreFocus: true);
        }
        else
        {
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        var stopwatch = Stopwatch.StartNew();
        _showCount++;

        // Panel সামনে আসার আগেই, যাতে PulseBar এর নিজের window না ধরে
        _paste.RememberTarget();

        StatusBadge.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        HintText.Text = _settings.AutoPaste
            ? "Enter: paste   Shift+Enter: copy   Esc: বন্ধ"
            : "Enter: copy   Esc: বন্ধ";

        // এতে popup বন্ধ হয় আর ViewModel সর্বশেষ history মেলায়
        AppState.Current.IsClipboardPanelVisible = true;
        _viewModel.ResetForQuickPick();

        // দেখানোর আগেই জায়গামতো, যাতে পুরনো জায়গায় এক ঝলক না দেখা যায়; অন্য DPI এর monitor এ
        // WPF দেখানোর সময় মাপ বদলাতে পারে, তাই পরে আরেকবার
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        PopupPlacement.PlaceNearCursor(hwnd, Width, Height);
        Show();
        PopupPlacement.PlaceNearCursor(hwnd, Width, Height);

        Activate();
        PopupPlacement.BringToForeground(hwnd);
        List.FocusSearch();
        List.ScrollToSelected();

        // Layout আর render শেষ হলে
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            Log.Info($"Clipboard panel shown in {stopwatch.ElapsedMilliseconds} ms"));
    }

    /// <param name="restoreFocus">
    /// Esc বা hotkey এ true: আগের window এ focus ফেরে। বাইরে click এ false, তখন focus যেখানে
    /// click হয়েছে সেখানেই থাকে।
    /// </param>
    public void HidePanel(bool restoreFocus)
    {
        if (!IsVisible)
        {
            return;
        }

        // আগে focus ফেরানো, তারপর লুকানো, যাতে মাঝে Windows অন্য কোনো window সামনে না আনে।
        // এতে Deactivated হয়ে panel নিজেই লুকিয়ে যেতে পারে।
        if (restoreFocus)
        {
            _paste.RestoreFocus();
        }

        if (IsVisible)
        {
            Hide();
        }

        AppState.Current.IsClipboardPanelVisible = false;
    }

    private async void OnItemActivated(object? sender, ClipboardItemActivatedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            int showCount = _showCount;
            bool copied = await _viewModel.CopyAsync(e.Item);

            // Copy না হলে card এ "Copy failed" দেখা যায়, panel খোলা থাকে
            if (!copied || !IsVisible || showCount != _showCount)
            {
                return;
            }

            bool paste = !e.CopyOnly && _settings.AutoPaste && _paste.HasTarget;
            if (paste && _paste.IsTargetBlocked())
            {
                Log.Info("Auto-paste: target window runs at a higher integrity level; copy only");
                HintText.Visibility = Visibility.Collapsed;
                StatusBadge.Visibility = Visibility.Visible;

                await Task.Delay(BlockedNoticeTime);
                if (IsVisible && showCount == _showCount)
                {
                    HidePanel(restoreFocus: true);
                }

                return;
            }

            HidePanel(restoreFocus: true);

            if (paste && !await _paste.PasteAsync())
            {
                Log.Error("Auto-paste failed; item is on the clipboard");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard panel activate", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>App বন্ধ হওয়ার সময় সত্যিই window close করতে দেয়।</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Search box এর আগেই, যাতে লেখা থাকলেও Esc panel বন্ধ করে
        if (e.Key == Key.Escape)
        {
            HidePanel(restoreFocus: true);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 ইত্যাদিতে window নষ্ট না করে শুধু লুকানো হবে।
        if (!_allowClose)
        {
            e.Cancel = true;
            HidePanel(restoreFocus: true);
        }

        base.OnClosing(e);
    }
}
