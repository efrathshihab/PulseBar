using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.Services;

public enum HotKeyAction
{
    ClipboardPanel,
    Screenshot,
}

public enum HotKeyChangeResult
{
    Ok,

    /// <summary>No modifier, or the key can't be a hotkey.</summary>
    Invalid,

    /// <summary>Another feature already uses this hotkey.</summary>
    Duplicate,

    /// <summary>RegisterHotKey failed: usually another app holds it.</summary>
    InUse,
}

/// <summary>
/// Keeps which global hotkey belongs to which feature in sync with <see cref="AppSettings"/>.
/// A new hotkey is registered first; only if that works is the old one released and the setting changed.
/// UI thread only.
/// </summary>
public sealed class HotKeyBindings : IDisposable
{
    private readonly HotKeyManager _manager;
    private readonly AppSettings _settings;
    private readonly Dictionary<HotKeyAction, Entry> _entries = [];
    private bool _suspended;

    public HotKeyBindings(HotKeyManager manager, AppSettings settings)
    {
        _manager = manager;
        _settings = settings;
    }

    /// <summary>Raised when any hotkey's registration state or value changes.</summary>
    public event EventHandler? Changed;

    public static HotKey DefaultFor(HotKeyAction action) => action == HotKeyAction.ClipboardPanel
        ? AppSettings.DefaultClipboardPanelHotKey
        : AppSettings.DefaultScreenshotHotKey;

    public HotKey Get(HotKeyAction action) => action == HotKeyAction.ClipboardPanel
        ? _settings.ClipboardPanelHotKey
        : _settings.ScreenshotHotKey;

    /// <summary>Whether the setting's hotkey is registered (false if another app holds it). Treated as true while suspended.</summary>
    public bool IsRegistered(HotKeyAction action) =>
        _entries.TryGetValue(action, out var entry) && (entry.Id != 0 || _suspended && entry.Works);

    /// <summary>Registers the setting's hotkey. On failure returns false and the setting stays as it was.</summary>
    public bool Bind(HotKeyAction action, Action callback)
    {
        var entry = new Entry(callback);
        _entries[action] = entry;
        bool ok = TryRegister(action, entry, Get(action));
        Changed?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public HotKeyChangeResult TryChange(HotKeyAction action, HotKey hotKey)
    {
        if (!_entries.TryGetValue(action, out var entry))
        {
            return HotKeyChangeResult.InUse;
        }

        if (!hotKey.IsValid)
        {
            return HotKeyChangeResult.Invalid;
        }

        var other = action == HotKeyAction.ClipboardPanel ? HotKeyAction.Screenshot : HotKeyAction.ClipboardPanel;
        if (Get(other) == hotKey)
        {
            return HotKeyChangeResult.Duplicate;
        }

        if (hotKey == Get(action) && (entry.Id != 0 || _suspended && entry.Works))
        {
            return HotKeyChangeResult.Ok;
        }

        // Even if our old one is still registered, the new one is different, so there's no conflict
        if (!_manager.TryRegister(hotKey, entry.Callback, out int newId))
        {
            return HotKeyChangeResult.InUse;
        }

        if (entry.Id != 0)
        {
            _manager.Unregister(entry.Id);
        }

        entry.Id = newId;
        entry.Works = true;
        if (_suspended)
        {
            // So pressing it while picking a hotkey doesn't open the panel; registered again on Resume
            _manager.Unregister(newId);
            entry.Id = 0;
        }

        Set(action, hotKey);
        Changed?.Invoke(this, EventArgs.Empty);
        return HotKeyChangeResult.Ok;
    }

    /// <summary>
    /// Temporarily releases all hotkeys while picking a hotkey, so pressing Ctrl+Alt+V doesn't open the panel.
    /// </summary>
    public void Suspend()
    {
        if (_suspended)
        {
            return;
        }

        _suspended = true;
        foreach (var entry in _entries.Values)
        {
            entry.Works = entry.Id != 0;
            if (entry.Id != 0)
            {
                _manager.Unregister(entry.Id);
                entry.Id = 0;
            }
        }
    }

    public void Resume()
    {
        if (!_suspended)
        {
            return;
        }

        _suspended = false;
        foreach (var (action, entry) in _entries)
        {
            TryRegister(action, entry, Get(action));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Id != 0)
            {
                _manager.Unregister(entry.Id);
                entry.Id = 0;
            }
        }

        _entries.Clear();
    }

    private bool TryRegister(HotKeyAction action, Entry entry, HotKey hotKey)
    {
        if (entry.Id != 0)
        {
            return true;
        }

        if (_manager.TryRegister(hotKey, entry.Callback, out int id))
        {
            entry.Id = id;
            entry.Works = true;
            return true;
        }

        entry.Works = false;
        Log.Error($"Hotkey for {action} ({hotKey}) is not available");
        return false;
    }

    private void Set(HotKeyAction action, HotKey hotKey)
    {
        if (action == HotKeyAction.ClipboardPanel)
        {
            _settings.ClipboardPanelHotKey = hotKey;
        }
        else
        {
            _settings.ScreenshotHotKey = hotKey;
        }
    }

    private sealed class Entry(Action callback)
    {
        public Action Callback { get; } = callback;

        /// <summary>HotKeyManager id; 0 if not registered.</summary>
        public int Id { get; set; }

        /// <summary>Whether it could be registered last time (to show the state while suspended).</summary>
        public bool Works { get; set; }
    }
}
