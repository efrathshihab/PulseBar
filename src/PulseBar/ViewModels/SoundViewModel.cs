using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>
/// Sound tab: output device selection, master volume and per-app volume/mute. While the popup is hidden the UI
/// changes nothing; controller events only set a "changed" flag, and everything is reconciled when the popup opens.
/// </summary>
public sealed partial class SoundViewModel : ObservableObject, IDisposable
{
    private readonly AudioController _controller;
    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private static readonly TimeSpan SwitchErrorDuration = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, AudioAppViewModel> _rows = [];
    private readonly DispatcherTimer _switchErrorTimer;
    private string? _currentDeviceId;
    private int _dirty = 1;

    public SoundViewModel(AudioController controller, AppState state, Dispatcher dispatcher)
    {
        _controller = controller;
        _state = state;
        _dispatcher = dispatcher;
        Master = new MasterVolumeViewModel(controller, dispatcher);

        _switchErrorTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SwitchErrorDuration };
        _switchErrorTimer.Tick += (_, _) =>
        {
            _switchErrorTimer.Stop();
            IsSwitchErrorVisible = false;
        };

        _controller.AppsChanged += OnAudioChanged;
        _controller.DevicesChanged += OnAudioChanged;
        _controller.MasterChanged += OnAudioChanged;
        _controller.DeviceSwitchFailed += OnDeviceSwitchFailed;
        _state.PopupVisibilityChanged += OnPopupVisibilityChanged;
    }

    public MasterVolumeViewModel Master { get; }

    /// <summary>All active output devices, by name.</summary>
    public ObservableCollection<AudioOutputDevice> Devices { get; } = [];

    /// <summary>The current default device; null if none.</summary>
    [ObservableProperty]
    public partial AudioOutputDevice? CurrentDevice { get; set; }

    /// <summary>No output device: the master and apps sections are hidden.</summary>
    [ObservableProperty]
    public partial bool HasDevices { get; set; }

    /// <summary>Master volume could be read.</summary>
    [ObservableProperty]
    public partial bool HasMaster { get; set; }

    [ObservableProperty]
    public partial bool IsDeviceListOpen { get; set; }

    /// <summary>"Couldn't switch device", for a few seconds.</summary>
    [ObservableProperty]
    public partial bool IsSwitchErrorVisible { get; set; }

    /// <summary>System sounds first, then by name.</summary>
    public ObservableCollection<AudioAppViewModel> Apps { get; } = [];

    /// <summary>No apps other than System sounds.</summary>
    [ObservableProperty]
    public partial bool IsEmptyVisible { get; set; } = true;

    public void Dispose()
    {
        _controller.AppsChanged -= OnAudioChanged;
        _controller.DevicesChanged -= OnAudioChanged;
        _controller.MasterChanged -= OnAudioChanged;
        _controller.DeviceSwitchFailed -= OnDeviceSwitchFailed;
        _state.PopupVisibilityChanged -= OnPopupVisibilityChanged;
        _switchErrorTimer.Stop();
    }

    [RelayCommand]
    private void ToggleDeviceList() => IsDeviceListOpen = !IsDeviceListOpen;

    [RelayCommand]
    private void SelectDevice(AudioOutputDevice? device)
    {
        IsDeviceListOpen = false;
        if (device is null || device.IsDefault)
        {
            return;
        }

        IsSwitchErrorVisible = false;
        _controller.SetDefaultDevice(device.Id);
    }

    private void OnDeviceSwitchFailed(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            IsSwitchErrorVisible = true;
            _switchErrorTimer.Stop();
            _switchErrorTimer.Start();
        });

    private void OnAudioChanged(object? sender, EventArgs e)
    {
        // From the audio thread; posted to the UI thread only once
        if (Interlocked.Exchange(ref _dirty, 1) == 0 && _state.IsPopupVisible)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, SyncIfDirty);
        }
    }

    private void OnPopupVisibilityChanged(object? sender, bool visible)
    {
        if (visible)
        {
            SyncIfDirty();
        }
        else
        {
            IsDeviceListOpen = false;
            Master.Flush();
            foreach (var row in _rows.Values)
            {
                row.Flush();
            }
        }
    }

    private void SyncIfDirty()
    {
        if (_state.IsPopupVisible && Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            SyncDevices(_controller.GetDevices());
            SyncMaster(_controller.GetMaster());
            Sync(_controller.GetApps());
        }
    }

    private void SyncDevices(IReadOnlyList<AudioOutputDevice> devices)
    {
        if (!Devices.SequenceEqual(devices))
        {
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(device);
            }
        }

        CurrentDevice = devices.FirstOrDefault(d => d.IsDefault);
        HasDevices = devices.Count > 0;
        if (!HasDevices)
        {
            IsDeviceListOpen = false;
        }
    }

    private void SyncMaster(MasterVolumeState? master)
    {
        HasMaster = master is not null;
        if (master is null)
        {
            return;
        }

        // When the default device changes, the new device's values apply right away, even mid-drag
        string? deviceId = CurrentDevice?.Id;
        bool deviceChanged = deviceId != _currentDeviceId;
        _currentDeviceId = deviceId;
        Master.Apply(master, force: deviceChanged);
    }

    /// <summary>Reuses old row ViewModels with only Move/Insert/Remove, so sliders aren't lost.</summary>
    private void Sync(IReadOnlyList<AudioAppSession> apps)
    {
        var target = new List<AudioAppViewModel>(apps.Count);
        foreach (var app in apps)
        {
            if (_rows.TryGetValue(app.Key, out var row))
            {
                row.Apply(app);
            }
            else
            {
                row = new AudioAppViewModel(app, _controller, _dispatcher);
                _rows[app.Key] = row;
            }

            target.Add(row);
        }

        var keep = new HashSet<AudioAppViewModel>(target);
        foreach (string key in _rows.Keys.Where(key => !keep.Contains(_rows[key])).ToList())
        {
            _rows.Remove(key);
        }

        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Apps[i]))
            {
                Apps.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < Apps.Count && ReferenceEquals(Apps[i], target[i]))
            {
                continue;
            }

            int existing = Apps.IndexOf(target[i]);
            if (existing >= 0)
            {
                Apps.Move(existing, i);
            }
            else
            {
                Apps.Insert(i, target[i]);
            }
        }

        IsEmptyVisible = !target.Any(row => !row.IsSystemSounds);
    }
}
