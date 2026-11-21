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
/// The hotkey clipboard panel. Created once, then only Hide/Show.
/// Enter/click: copy, close, return focus to the previous window and send Ctrl+V (like Windows' Win+V).
/// Shift+Enter: copy only. Esc, the hotkey again or a click outside: close.
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
    /// At startup, shows once off-screen without taking focus and hides again, so the window, template and
    /// Mica are ready and the panel opens quickly even on the first hotkey.
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

        // Before the panel comes to the front, so PulseBar's own window isn't remembered
        _paste.RememberTarget();

        StatusBadge.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        HintText.Text = _settings.AutoPaste
            ? "Enter: paste   Shift+Enter: copy   Esc: close"
            : "Enter: copy   Esc: close";

        // This closes the popup and lets the ViewModel reconcile the latest history
        AppState.Current.IsClipboardPanelVisible = true;
        _viewModel.ResetForQuickPick();

        // Positioned before showing so it doesn't flash at the old spot; on a monitor with another DPI
        // WPF may change the size while showing, so once more afterwards
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        PopupPlacement.PlaceNearCursor(hwnd, Width, Height);
        Show();
        PopupPlacement.PlaceNearCursor(hwnd, Width, Height);

        Activate();
        PopupPlacement.BringToForeground(hwnd);
        List.FocusSearch();
        List.ScrollToSelected();

        // Once layout and render are done
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            Log.Info($"Clipboard panel shown in {stopwatch.ElapsedMilliseconds} ms"));
    }

    /// <param name="restoreFocus">
    /// True for Esc or the hotkey: focus returns to the previous window. False for a click outside; focus then
    /// stays where the click happened.
    /// </param>
    public void HidePanel(bool restoreFocus)
    {
        if (!IsVisible)
        {
            return;
        }

        // Return focus first, then hide, so Windows doesn't bring some other window forward in between.
        // This may deactivate the panel and hide it by itself.
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

            // If the copy fails the card shows "Copy failed" and the panel stays open
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

    /// <summary>Lets the window really close when the app shuts down.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Before the search box, so Esc closes the panel even when there's text
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
        // Alt+F4 and the like only hide the window instead of destroying it.
        if (!_allowClose)
        {
            e.Cancel = true;
            HidePanel(restoreFocus: true);
        }

        base.OnClosing(e);
    }
}
