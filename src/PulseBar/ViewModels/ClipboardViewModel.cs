using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>
/// Clipboard tab এর সাধারণ test list (দিন ৭ এ পুরো UI হবে)।
/// Popup লুকানো থাকলে শুধু মনে রাখে যে বদলেছে; খুললে তখন list বসায়।
/// </summary>
public sealed partial class ClipboardViewModel : ObservableObject, IDisposable
{
    private readonly ClipboardManager _manager;
    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private int _dirty = 1;

    [ObservableProperty]
    public partial IReadOnlyList<ClipboardItem> Items { get; set; } = [];

    [ObservableProperty]
    public partial string CountText { get; set; } = "0 items";

    public ClipboardViewModel(ClipboardManager manager, AppState state, Dispatcher dispatcher)
    {
        _manager = manager;
        _state = state;
        _dispatcher = dispatcher;
        _manager.HistoryChanged += OnHistoryChanged;
        _state.PopupVisibilityChanged += OnPopupVisibilityChanged;
    }

    public void Dispose()
    {
        _manager.HistoryChanged -= OnHistoryChanged;
        _state.PopupVisibilityChanged -= OnPopupVisibilityChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        // Background thread থেকে আসে; একবারই UI thread এ পাঠানো হয়
        if (Interlocked.Exchange(ref _dirty, 1) == 0 && _state.IsPopupVisible)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshIfVisible);
        }
    }

    private void OnPopupVisibilityChanged(object? sender, bool visible)
    {
        if (visible)
        {
            RefreshIfVisible();
        }
    }

    private void RefreshIfVisible()
    {
        if (!_state.IsPopupVisible || Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        var items = _manager.GetItems();
        Items = items;
        CountText = items.Length == 1 ? "1 item" : $"{items.Length} items";
    }
}
