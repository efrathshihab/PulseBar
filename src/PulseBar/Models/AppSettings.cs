using System.Windows.Input;

namespace PulseBar.Models;

/// <summary>
/// User এর বদলানো যায় এমন setting। আপাতত শুধু memory তে; দিন ১২ এ Settings tab থেকে
/// বদলানো আর disk এ save হবে।
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Ctrl+Shift+V না, কারণ ওটা অনেক app এর "paste as plain text"।
    /// </summary>
    public static readonly HotKey DefaultClipboardPanelHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.V);

    /// <summary>Windows এর screen clip খোলার hotkey।</summary>
    public static readonly HotKey DefaultScreenshotHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, Key.S);

    public static AppSettings Current { get; } = new();

    private AppSettings()
    {
    }

    /// <summary>Clipboard panel খোলার global hotkey।</summary>
    public HotKey ClipboardPanelHotKey { get; set; } = DefaultClipboardPanelHotKey;

    /// <summary>
    /// Panel এ Enter/click করলে copy এর পরে আগের window এ Ctrl+V পাঠিয়ে সরাসরি paste করবে কিনা।
    /// বন্ধ থাকলে শুধু copy হয়।
    /// </summary>
    public bool AutoPaste { get; set; } = true;

    /// <summary>Screenshot নেওয়ার global hotkey।</summary>
    public HotKey ScreenshotHotKey { get; set; } = DefaultScreenshotHotKey;

    /// <summary>Screenshots folder এ নতুন ছবি এলে history তে যোগ করবে কিনা।</summary>
    public bool WatchScreenshotsFolder { get; set; } = true;

    /// <summary>
    /// Screenshots folder এর নতুন ছবি clipboard এও দেবে কিনা, যাতে Win+PrtSc এর পরেই Ctrl+V করা যায়।
    /// </summary>
    public bool AutoCopyScreenshots { get; set; } = true;
}
