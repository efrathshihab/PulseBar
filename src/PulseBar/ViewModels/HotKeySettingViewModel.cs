using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>One hotkey in Settings (the HotKeyBox's DataContext).</summary>
public sealed partial class HotKeySettingViewModel : ObservableObject, IDisposable
{
    public const string InUseMessage = "This shortcut is used by another app";

    private readonly HotKeyBindings _bindings;
    private readonly HotKeyAction _action;
    private readonly AppSettings _settings;
    private string? _changeError;

    public HotKeySettingViewModel(HotKeyBindings bindings, HotKeyAction action, AppSettings settings)
    {
        _bindings = bindings;
        _action = action;
        _settings = settings;
        _bindings.Changed += OnBindingsChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        Refresh();
    }

    [ObservableProperty]
    public partial string DisplayText { get; private set; } = "";

    /// <summary>Red text under the control; null if none.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial bool IsDefault { get; private set; }

    /// <summary>Releases all hotkeys while waiting for keys, so pressing one doesn't open the panel.</summary>
    public void BeginCapture()
    {
        _changeError = null;
        _bindings.Suspend();
        Refresh();
    }

    public void EndCapture() => _bindings.Resume();

    /// <returns>True if accepted; otherwise the reason is in <see cref="ErrorText"/>.</returns>
    public bool TryApply(HotKey hotKey)
    {
        var result = _bindings.TryChange(_action, hotKey);
        _changeError = result switch
        {
            HotKeyChangeResult.Ok => null,
            HotKeyChangeResult.Invalid => "Press a key together with Ctrl, Alt, Shift or Win",
            HotKeyChangeResult.Duplicate => _action == HotKeyAction.ClipboardPanel
                ? "Screenshot already uses this shortcut"
                : "Clipboard panel already uses this shortcut",
            _ => InUseMessage,
        };
        Refresh();
        return result == HotKeyChangeResult.Ok;
    }

    public void ResetToDefault() => TryApply(HotKeyBindings.DefaultFor(_action));

    public void Dispose()
    {
        _bindings.Changed -= OnBindingsChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnBindingsChanged(object? sender, EventArgs e) => Refresh();

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ClipboardPanelHotKey) or nameof(AppSettings.ScreenshotHotKey))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var current = _bindings.Get(_action);
        DisplayText = current.DisplayText;
        IsDefault = current == HotKeyBindings.DefaultFor(_action);

        // The reason if the new one wasn't accepted; otherwise whether the current one works
        ErrorText = _changeError ?? (_bindings.IsRegistered(_action) ? null : InUseMessage);
    }
}
