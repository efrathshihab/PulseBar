using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// One row of the Sound tab. While dragging the slider it sends the latest value about once every ~30 ms
/// instead of a COM call on every movement.
/// </summary>
public sealed partial class AudioAppViewModel : ObservableObject
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(30);

    // For this long after the user changes it, outside (stale) values aren't put on the slider, so it doesn't jump while dragging
    private const long UserHoldMs = 400;

    private readonly AudioController _controller;
    private readonly DispatcherTimer _pushTimer;
    private long _lastUserChange;
    private bool _applying;

    public AudioAppViewModel(AudioAppSession app, AudioController controller, Dispatcher dispatcher)
    {
        _controller = controller;
        Key = app.Key;
        IsSystemSounds = app.IsSystemSounds;
        Icon = AppIconCache.Get(app.IconPath, app.IconIndex);

        _pushTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = PushInterval };
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            _controller.SetVolume(Key, (float)(Volume / 100.0));
        };

        Apply(app);
    }

    public string Key { get; }

    public bool IsSystemSounds { get; }

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon is not null;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>0–100।</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    public partial double Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>e.g. "65%".</summary>
    public string VolumeText => $"{Math.Round(Volume):0}%";

    /// <summary>Applies the controller's new value; the volume is skipped while the user is dragging.</summary>
    public void Apply(AudioAppSession app)
    {
        Name = app.Name;

        if (_pushTimer.IsEnabled || Environment.TickCount64 - _lastUserChange < UserHoldMs)
        {
            return;
        }

        _applying = true;
        try
        {
            Volume = Math.Round(app.Volume * 100.0);
            IsMuted = app.IsMuted;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Sends any pending value when the row is removed.</summary>
    public void Flush()
    {
        if (_pushTimer.IsEnabled)
        {
            _pushTimer.Stop();
            _controller.SetVolume(Key, (float)(Volume / 100.0));
        }
    }

    partial void OnVolumeChanged(double value)
    {
        // Values that came from the controller aren't sent back to it
        if (_applying)
        {
            return;
        }

        _lastUserChange = Environment.TickCount64;
        if (!_pushTimer.IsEnabled)
        {
            _pushTimer.Start();
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        _lastUserChange = Environment.TickCount64;
        IsMuted = !IsMuted;
        _controller.SetMute(Key, IsMuted);
    }
}
