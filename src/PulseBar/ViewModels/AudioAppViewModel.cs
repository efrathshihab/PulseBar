using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// Sound tab এর একটা row। Slider টানার সময় প্রতিটা নড়াচড়ায় COM call না করে ~৩০ ms এ একবার
/// সর্বশেষ মান পাঠায়।
/// </summary>
public sealed partial class AudioAppViewModel : ObservableObject
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(30);

    // User নিজে বদলানোর পর এতক্ষণ বাইরের (পুরনো) মান slider এ বসানো হবে না, যাতে টানার সময় লাফ না দেয়
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

    /// <summary>যেমন "65%"।</summary>
    public string VolumeText => $"{Math.Round(Volume):0}%";

    /// <summary>Controller এর নতুন মান বসায়; user এখন টানছে থাকলে volume টা বাদ।</summary>
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

    /// <summary>Row সরানোর সময় বাকি থাকা মান পাঠিয়ে দেয়।</summary>
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
        // Controller থেকে আসা মান আবার controller এ পাঠানো হয় না
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
