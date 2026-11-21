namespace PulseBar.Models;

/// <summary>
/// The single app-wide shared state. SystemMonitor writes snapshots here,
/// and ViewModels and the tray icon read from it.
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    private volatile SystemSnapshot? _snapshot;
    private bool _isPopupVisible;
    private bool _isClipboardPanelVisible;

    private AppState()
    {
    }

    /// <summary>The latest snapshot. Changes every second from a background thread.</summary>
    public SystemSnapshot? Snapshot => _snapshot;

    /// <summary>Whether the popup is open now. Changed only from the UI thread.</summary>
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

    /// <summary>Whether the hotkey clipboard panel is open now. Changed only from the UI thread.</summary>
    public bool IsClipboardPanelVisible
    {
        get => _isClipboardPanelVisible;
        set
        {
            if (_isClipboardPanelVisible == value)
            {
                return;
            }

            _isClipboardPanelVisible = value;
            ClipboardPanelVisibilityChanged?.Invoke(this, value);
        }
    }

    /// <summary>Raised when a new snapshot arrives, from a background thread.</summary>
    public event EventHandler<SystemSnapshot>? SnapshotUpdated;

    /// <summary>Raised when the popup opens or hides, from the UI thread.</summary>
    public event EventHandler<bool>? PopupVisibilityChanged;

    /// <summary>Raised when the clipboard panel opens or hides, from the UI thread.</summary>
    public event EventHandler<bool>? ClipboardPanelVisibilityChanged;

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
                // One subscriber's error doesn't stop the others or the monitor
            }
        }
    }
}
