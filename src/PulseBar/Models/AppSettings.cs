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
}
