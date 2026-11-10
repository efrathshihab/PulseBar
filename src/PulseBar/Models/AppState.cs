namespace PulseBar.Models;

/// <summary>
/// App জুড়ে একটাই shared state। SystemMonitor এখানে snapshot লেখে,
/// ViewModel আর tray icon এখান থেকে পড়ে।
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    private volatile SystemSnapshot? _snapshot;
    private bool _isPopupVisible;

    private AppState()
    {
    }

    /// <summary>সর্বশেষ snapshot। প্রতি সেকেন্ডে background thread থেকে বদলায়।</summary>
    public SystemSnapshot? Snapshot => _snapshot;

    /// <summary>Popup এখন খোলা কিনা। শুধু UI thread থেকে বদলায়।</summary>
    public bool IsPopupVisible
    {
        get => _isPopupVisible;
        set
        {
            if (_isPopupVisible == value)
            {
                return;
            }

            _isPopupVisible = value;
            PopupVisibilityChanged?.Invoke(this, value);
        }
    }

    /// <summary>নতুন snapshot এলে, background thread থেকে।</summary>
    public event EventHandler<SystemSnapshot>? SnapshotUpdated;

    /// <summary>Popup খুললে বা লুকালে, UI thread থেকে।</summary>
    public event EventHandler<bool>? PopupVisibilityChanged;

    public void UpdateSnapshot(SystemSnapshot snapshot)
    {
        _snapshot = snapshot;

        var handlers = SnapshotUpdated;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<SystemSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // একজন subscriber এর ভুলে বাকিরা বা monitor থামবে না
            }
        }
    }
}
