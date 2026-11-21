using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>
/// Master volume on the Sound tab. Like the per-app rows: while dragging the slider it sends the latest value
/// about once every ~30 ms, and right after the user changes it, it doesn't apply an outside (stale) value.
/// </summary>
public sealed partial class MasterVolumeViewModel : ObservableObject
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(30);
    private const long UserHoldMs = 400;

    private readonly AudioController _controller;
    private readonly DispatcherTimer _pushTimer;
    private long _lastUserChange;
    private bool _applying;

    public MasterVolumeViewModel(AudioController controller, Dispatcher dispatcher)
    {
        _controller = controller;
        _pushTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = PushInterval };
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            _controller.SetMasterVolume((float)(Volume / 100.0));
        };
    }

    /// <summary>0–100।</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    public partial double Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>e.g. "65%".</summary>
    public string VolumeText => $"{Math.Round(Volume):0}%";

    /// <summary>Applies the controller's value; skipped while the user is dragging.</summary>
    public void Apply(MasterVolumeState master, bool force = false)
    {
        if (!force && (_pushTimer.IsEnabled || Environment.TickCount64 - _lastUserChange < UserHoldMs))
        {
            return;
        }

        _pushTimer.Stop();
        _applying = true;
        try
        {
            Volume = Math.Round(master.Volume * 100.0);
            IsMuted = master.IsMuted;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Sends any pending value when the popup hides.</summary>
    public void Flush()
    {
        if (_pushTimer.IsEnabled)
        {
            _pushTimer.Stop();
            _controller.SetMasterVolume((float)(Volume / 100.0));
        }
    }

    partial void OnVolumeChanged(double value)
    {
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
        _controller.SetMasterMute(IsMuted);
    }
}
