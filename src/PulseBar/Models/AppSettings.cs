using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PulseBar.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Taskbar overlay এ কয়টা মান।</summary>
public enum OverlayLayout
{
    /// <summary>CPU + RAM, এক সারি।</summary>
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

    /// <summary>Task Scheduler এর "Run with highest privileges" task।</summary>
    WithAdmin,
}

/// <summary>
/// User এর বদলানো যায় এমন setting। SettingsStore শুরুতে disk থেকে load করে আর বদলালে save করে।
/// শুধু UI thread থেকে বদলাতে হবে; PropertyChanged ও তখন UI thread এই আসে।
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    /// <summary>
    /// Ctrl+Shift+V না, কারণ ওটা অনেক app এর "paste as plain text"।
    /// </summary>
    public static readonly HotKey DefaultClipboardPanelHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.V);

    /// <summary>Windows এর screen clip খোলার hotkey।</summary>
    public static readonly HotKey DefaultScreenshotHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.S);

    public static AppSettings Current { get; } = new();

    /// <summary>Clipboard panel খোলার global hotkey।</summary>
    [ObservableProperty]
    public partial HotKey ClipboardPanelHotKey { get; set; } = DefaultClipboardPanelHotKey;

    /// <summary>
    /// Panel এ Enter/click করলে copy এর পরে আগের window এ Ctrl+V পাঠিয়ে সরাসরি paste করবে কিনা।
    /// বন্ধ থাকলে শুধু copy হয়।
    /// </summary>
    [ObservableProperty]
    public partial bool AutoPaste { get; set; } = true;

    /// <summary>Screenshot নেওয়ার global hotkey।</summary>
    [ObservableProperty]
    public partial HotKey ScreenshotHotKey { get; set; } = DefaultScreenshotHotKey;

    /// <summary>Screenshots folder এ নতুন ছবি এলে history তে যোগ করবে কিনা।</summary>
    [ObservableProperty]
    public partial bool WatchScreenshotsFolder { get; set; } = true;

    /// <summary>
    /// Screenshots folder এর নতুন ছবি clipboard এও দেবে কিনা, যাতে Win+PrtSc এর পরেই Ctrl+V করা যায়।
    /// </summary>
    [ObservableProperty]
    public partial bool AutoCopyScreenshots { get; set; } = true;

    [ObservableProperty]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Tray menu এর "Show on taskbar" এর সাথে একই মান।</summary>
    [ObservableProperty]
    public partial bool ShowTaskbarOverlay { get; set; } = true;

    [ObservableProperty]
    public partial OverlayLayout OverlayLayout { get; set; } = OverlayLayout.Balanced;

    [ObservableProperty]
    public partial ClipboardRetention ClipboardRetention { get; set; } = ClipboardRetention.TwentyFourHours;

    /// <summary>বন্ধ থাকলে নতুন কিছু history তে ধরা হয় না; পুরনোগুলো থাকে।</summary>
    [ObservableProperty]
    public partial bool ClipboardEnabled { get; set; } = true;

    [ObservableProperty]
    public partial LaunchAtLogin LaunchAtLogin { get; set; } = LaunchAtLogin.Off;

    public static TimeSpan ToTimeSpan(ClipboardRetention retention) => retention switch
    {
        ClipboardRetention.OneHour => TimeSpan.FromHours(1),
        ClipboardRetention.SevenDays => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>
    /// LaunchAtLogin আর hotkey ছাড়া সব default এ। Hotkey register আর login এর Run key/task এর
    /// কাজ আছে, তাই ওগুলো যার যার service দিয়ে বদলাতে হয়।
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
    }
}
