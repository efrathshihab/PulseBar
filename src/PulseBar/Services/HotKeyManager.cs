using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Global hotkeys through RegisterHotKey. Catches WM_HOTKEY on its own message-only window, so there's no
/// keyboard hook or polling. Create and call it from the UI thread; callbacks also arrive on the UI thread.
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private const int WmHotKey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Dictionary<int, (HotKey HotKey, Action Callback)> _registered = [];
    private HwndSource? _window;
    private int _nextId = 1;

    public HotKeyManager()
    {
        try
        {
            var parameters = new HwndSourceParameters("PulseBarHotKeys")
            {
                ParentWindow = HwndMessage,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };
            _window = new HwndSource(parameters);
            _window.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            // Without the window hotkeys won't work, but the app keeps running
            Log.Error("Hotkey window", ex);
            _window = null;
        }
    }

    /// <summary>
    /// Registers a hotkey. False if another app already holds it (or for any other reason); no
    /// exception then. On success it can later be removed with <see cref="Unregister"/> using <paramref name="id"/>.
    /// </summary>
    public bool TryRegister(HotKey hotKey, Action callback, out int id)
    {
        id = 0;
        if (_window is null)
        {
            return false;
        }

        uint modifiers = MOD_NOREPEAT;
        if (hotKey.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= MOD_ALT;
        }

        if (hotKey.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= MOD_CONTROL;
        }

        if (hotKey.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= MOD_SHIFT;
        }

        if (hotKey.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= MOD_WIN;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotKey.Key);
        int newId = _nextId++;

        if (!RegisterHotKey(_window.Handle, newId, modifiers, vk))
        {
            Log.Error($"RegisterHotKey {hotKey} failed (Win32 {Marshal.GetLastPInvokeError()})");
            return false;
        }

        _registered[newId] = (hotKey, callback);
        id = newId;
        Log.Info($"Hotkey {hotKey} registered");
        return true;
    }

    public void Unregister(int id)
    {
        if (_window is not null && _registered.Remove(id, out var entry))
        {
            UnregisterHotKey(_window.Handle, id);
            Log.Info($"Hotkey {entry.HotKey} unregistered");
        }
    }

    public void Dispose()
    {
        if (_window is null)
        {
            return;
        }

        foreach (int id in _registered.Keys.ToList())
        {
            Unregister(id);
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
        _window = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && _registered.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            try
            {
                entry.Callback();
            }
            catch (Exception ex)
            {
                Log.Error($"Hotkey {entry.HotKey} handler", ex);
            }
        }

        return IntPtr.Zero;
    }

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
