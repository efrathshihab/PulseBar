using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>
/// Sound tab এর master volume। Per-app row এর মতোই: slider টানার সময় ~৩০ ms এ একবার সর্বশেষ মান
/// পাঠায়, আর user নিজে বদলানোর ঠিক পরে বাইরের (পুরনো) মান বসায় না।
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

    /// <summary>যেমন "65%"।</summary>
    public string VolumeText => $"{Math.Round(Volume):0}%";

    /// <summary>Controller এর মান বসায়; user এখন টানছে থাকলে বাদ।</summary>
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

    /// <summary>Popup লুকানোর সময় বাকি থাকা মান পাঠিয়ে দেয়।</summary>
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
