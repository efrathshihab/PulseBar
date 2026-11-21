using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>
/// Sound tab: output device বাছাই, master volume, আর per-app volume/mute। Popup লুকানো থাকলে UI
/// কিছুই বদলায় না; controller এর event শুধু "বদলেছে" চিহ্ন দেয়, popup খুললে তখন সব মেলানো হয়।
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

    /// <summary>সব Active output device, নাম অনুযায়ী।</summary>
    public ObservableCollection<AudioOutputDevice> Devices { get; } = [];

    /// <summary>এখনকার default device; না থাকলে null।</summary>
    [ObservableProperty]
    public partial AudioOutputDevice? CurrentDevice { get; set; }

    /// <summary>কোনো output device নেই: master আর apps অংশ লুকানো।</summary>
    [ObservableProperty]
    public partial bool HasDevices { get; set; }

    /// <summary>Master volume পড়া গেছে।</summary>
    [ObservableProperty]
    public partial bool HasMaster { get; set; }

    [ObservableProperty]
    public partial bool IsDeviceListOpen { get; set; }

    /// <summary>"Device বদলানো যায়নি", কয়েক সেকেন্ড।</summary>
    [ObservableProperty]
    public partial bool IsSwitchErrorVisible { get; set; }

    /// <summary>আগে System sounds, তারপর নাম অনুযায়ী।</summary>
    public ObservableCollection<AudioAppViewModel> Apps { get; } = [];

    /// <summary>System sounds ছাড়া কোনো app নেই।</summary>
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
        // Audio thread থেকে; একবারই UI thread এ পাঠানো হয়
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

        // Default device বদলালে নতুন device এর মান সাথে সাথে, user এর টানা বাকি থাকলেও
        string? deviceId = CurrentDevice?.Id;
        bool deviceChanged = deviceId != _currentDeviceId;
        _currentDeviceId = deviceId;
        Master.Apply(master, force: deviceChanged);
    }

    /// <summary>পুরনো row এর ViewModel আবার ব্যবহার করে শুধু Move/Insert/Remove, যাতে slider না হারায়।</summary>
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
