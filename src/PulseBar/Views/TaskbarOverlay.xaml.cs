using System.Windows.Controls;
using System.Windows.Media;

namespace PulseBar.Views;

/// <summary>
/// Taskbar এর উপর 2x2 লেখা: CPU, GPU, RAM, GPU temp। শুধু চেহারা; কোথায় বসবে তা
/// TaskbarOverlayHost ঠিক করে।
/// </summary>
public partial class TaskbarOverlay : UserControl
{
    /// <summary>Overlay এর স্থির আকার (DIP)।</summary>
    public const double DesignWidth = 130;
    public const double DesignHeight = 30;

    private static readonly SolidColorBrush DarkLabel = Freeze(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush DarkValue = Freeze(Colors.White);
    private static readonly SolidColorBrush LightLabel = Freeze(Color.FromArgb(0x99, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush LightValue = Freeze(Color.FromRgb(0x1A, 0x1A, 0x1A));

    public TaskbarOverlay()
    {
        InitializeComponent();
        Width = DesignWidth;
        Height = DesignHeight;
    }

    /// <summary>Dark taskbar এ সাদা লেখা, Light এ কালো।</summary>
    public void ApplyTheme(bool isLightTaskbar)
    {
        Resources["OverlayLabelBrush"] = isLightTaskbar ? LightLabel : DarkLabel;
        Resources["OverlayValueBrush"] = isLightTaskbar ? LightValue : DarkValue;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
