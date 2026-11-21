using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>Settings এর একটা hotkey (HotKeyBox এর DataContext)।</summary>
public sealed partial class HotKeySettingViewModel : ObservableObject, IDisposable
{
    public const string InUseMessage = "এই shortcut অন্য app ব্যবহার করছে";

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

    /// <summary>Control এর নিচে লাল লেখা; কিছু না থাকলে null।</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    public partial bool IsDefault { get; private set; }

    /// <summary>Key চাপার অপেক্ষার সময় সব hotkey ছেড়ে দেয়, যাতে চাপলে panel না খোলে।</summary>
    public void BeginCapture()
    {
        _changeError = null;
        _bindings.Suspend();
        Refresh();
    }

    public void EndCapture() => _bindings.Resume();

    /// <returns>নেওয়া হলে true; না হলে <see cref="ErrorText"/> এ কারণ।</returns>
    public bool TryApply(HotKey hotKey)
    {
        var result = _bindings.TryChange(_action, hotKey);
        _changeError = result switch
        {
            HotKeyChangeResult.Ok => null,
            HotKeyChangeResult.Invalid => "Ctrl, Alt, Shift বা Win এর সাথে একটা key চাপুন",
            HotKeyChangeResult.Duplicate => _action == HotKeyAction.ClipboardPanel
                ? "Screenshot এ এই shortcut আছে"
                : "Clipboard panel এ এই shortcut আছে",
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

        // নতুনটা নেওয়া না গেলে সেই কারণ; না হলে এখনকারটা চলছে কিনা
        ErrorText = _changeError ?? (_bindings.IsRegistered(_action) ? null : InUseMessage);
    }
}
