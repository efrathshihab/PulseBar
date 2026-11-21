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

    /// <summary>Modifier নেই বা key টা hotkey হতে পারে না।</summary>
    Invalid,

    /// <summary>অন্য feature এ এই hotkey আছে।</summary>
    Duplicate,

    /// <summary>RegisterHotKey fail: সাধারণত অন্য app নিয়ে রেখেছে।</summary>
    InUse,
}

/// <summary>
/// কোন feature এ কোন global hotkey, তা <see cref="AppSettings"/> এর সাথে মিলিয়ে রাখে।
/// নতুন hotkey আগে register করে দেখে; পারলে তবেই পুরনোটা ছাড়ে আর setting বদলায়।
/// শুধু UI thread থেকে।
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

    /// <summary>কোনো hotkey এর register অবস্থা বা মান বদলালে।</summary>
    public event EventHandler? Changed;

    public static HotKey DefaultFor(HotKeyAction action) => action == HotKeyAction.ClipboardPanel
        ? AppSettings.DefaultClipboardPanelHotKey
        : AppSettings.DefaultScreenshotHotKey;

    public HotKey Get(HotKeyAction action) => action == HotKeyAction.ClipboardPanel
        ? _settings.ClipboardPanelHotKey
        : _settings.ScreenshotHotKey;

    /// <summary>Setting এর hotkey register হয়ে আছে কিনা (অন্য app নিয়ে রাখলে false)। Suspend এর সময় true ধরা হয়।</summary>
    public bool IsRegistered(HotKeyAction action) =>
        _entries.TryGetValue(action, out var entry) && (entry.Id != 0 || _suspended && entry.Works);

    /// <summary>Setting এর hotkey register করে। Fail করলে false, setting যেমন ছিল থাকে।</summary>
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

        // নিজের পুরনোটা এখনো register করা থাকলেও নতুনটা আলাদা, তাই সংঘর্ষ নেই
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
            // Hotkey বাছাইয়ের সময় চাপলে যেন panel না খোলে; Resume এ আবার register
            _manager.Unregister(newId);
            entry.Id = 0;
        }

        Set(action, hotKey);
        Changed?.Invoke(this, EventArgs.Empty);
        return HotKeyChangeResult.Ok;
    }

    /// <summary>
    /// Hotkey বাছাইয়ের সময় সব hotkey সাময়িক ছেড়ে দেয়, যাতে Ctrl+Alt+V চাপলে panel না খোলে।
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

        /// <summary>HotKeyManager এর id; register না থাকলে 0।</summary>
        public int Id { get; set; }

        /// <summary>শেষবার register করা গিয়েছিল কিনা (Suspend এর সময় অবস্থা দেখানোর জন্য)।</summary>
        public bool Works { get; set; }
    }
}
