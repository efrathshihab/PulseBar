using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PulseBar.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>How many values the taskbar overlay shows.</summary>
public enum OverlayLayout
{
    /// <summary>CPU + RAM, one row.</summary>
    Compact,

    /// <summary>CPU, GPU, RAM, GPU temp, 2x2।</summary>
    Balanced,
}

public enum ClipboardRetention
{
    OneHour,
    TwentyFourHours,
    SevenDays,
}

public enum LaunchAtLogin
{
    Off,

    /// <summary>HKCU Run key।</summary>
    Normal,

    /// <summary>A Task Scheduler task with "Run with highest privileges".</summary>
    WithAdmin,
}

/// <summary>
/// Settings the user can change. SettingsStore loads them from disk at startup and saves on change.
/// Change only from the UI thread; PropertyChanged then also arrives on the UI thread.
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    /// <summary>
    /// Not Ctrl+Shift+V, because that's "paste as plain text" in many apps.
    /// </summary>
    public static readonly HotKey DefaultClipboardPanelHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.V);

    /// <summary>Hotkey that opens Windows' screen clip.</summary>
    public static readonly HotKey DefaultScreenshotHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.S);

    public static AppSettings Current { get; } = new();

    /// <summary>Global hotkey that opens the clipboard panel.</summary>
    [ObservableProperty]
    public partial HotKey ClipboardPanelHotKey { get; set; } = DefaultClipboardPanelHotKey;

    /// <summary>
    /// Whether Enter/click in the panel, after copying, sends Ctrl+V to the previous window to paste directly.
    /// When off, it only copies.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoPaste { get; set; } = true;

    /// <summary>Global hotkey for taking a screenshot.</summary>
    [ObservableProperty]
    public partial HotKey ScreenshotHotKey { get; set; } = DefaultScreenshotHotKey;

    /// <summary>Whether new images in the Screenshots folder are added to history.</summary>
    [ObservableProperty]
    public partial bool WatchScreenshotsFolder { get; set; } = true;

    /// <summary>
    /// Whether new Screenshots folder images also go to the clipboard, so Ctrl+V works right after Win+PrtSc.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoCopyScreenshots { get; set; } = true;

    [ObservableProperty]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>The same value as the tray menu's "Show on taskbar".</summary>
    [ObservableProperty]
    public partial bool ShowTaskbarOverlay { get; set; } = true;

    [ObservableProperty]
    public partial OverlayLayout OverlayLayout { get; set; } = OverlayLayout.Balanced;

    [ObservableProperty]
    public partial ClipboardRetention ClipboardRetention { get; set; } = ClipboardRetention.TwentyFourHours;

    /// <summary>When off, nothing new is captured into history; existing items stay.</summary>
    [ObservableProperty]
    public partial bool ClipboardEnabled { get; set; } = true;

    [ObservableProperty]
    public partial LaunchAtLogin LaunchAtLogin { get; set; } = LaunchAtLogin.Off;

    /// <summary>Check GitHub for updates 60 seconds after start and then every 6 hours.</summary>
    [ObservableProperty]
    public partial bool AutoCheckUpdates { get; set; } = true;

    public static TimeSpan ToTimeSpan(ClipboardRetention retention) => retention switch
    {
        ClipboardRetention.OneHour => TimeSpan.FromHours(1),
        ClipboardRetention.SevenDays => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>
    /// Everything except LaunchAtLogin and hotkeys back to default. Hotkeys need registering and login needs
    /// Run key/task work, so those are changed through their own services.
    /// </summary>
    public void ResetSimpleValues()
    {
        var defaults = new AppSettings();
        AutoPaste = defaults.AutoPaste;
        WatchScreenshotsFolder = defaults.WatchScreenshotsFolder;
        AutoCopyScreenshots = defaults.AutoCopyScreenshots;
        Theme = defaults.Theme;
        ShowTaskbarOverlay = defaults.ShowTaskbarOverlay;
        OverlayLayout = defaults.OverlayLayout;
        ClipboardRetention = defaults.ClipboardRetention;
        ClipboardEnabled = defaults.ClipboardEnabled;
        AutoCheckUpdates = defaults.AutoCheckUpdates;
    }
}
