using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Output device এর list আর default device বদলানো, default device এর master volume, আর তার
/// audio session গুলো (Volume Mixer এর মতো) exe অনুযায়ী জোড়া দিয়ে per-app volume/mute।
/// </summary>
/// <remarks>
/// সব COM কাজ একটাই নিজস্ব MTA thread এ, ক্রমানুসারে। COM এর event (নতুন session, volume বদল,
/// device বদল) যেকোনো thread এ আসে; সেগুলো শুধু এই thread এর queue তে কাজ পাঠায়।
/// UI এর জন্য immutable snapshot (<see cref="GetApps"/>, <see cref="GetDevices"/>, <see cref="GetMaster"/>)
/// আর তাদের Changed event।
/// </remarks>
public sealed class AudioController : IDisposable
{
    private const string SystemSoundsName = "System sounds";

    // নিজের লেখা volume এর echo চেনার জন্য (float এর rounding)
    private const float EchoTolerance = 0.001f;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Lock _gate = new();
    private AudioAppSession[] _apps = [];
    private AudioOutputDevice[] _devices = [];
    private MasterVolumeState? _master;
    private Thread? _thread;
    private volatile bool _disposed;
    private int _resyncQueued;
    private int _devicesQueued;

    // নিচের সব শুধু audio thread থেকে ছোঁয়া হয়
    private readonly Dictionary<string, SessionEntry> _sessions = [];
    private readonly Dictionary<int, Process?> _processWatchers = [];
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _deviceNotifications;
    private MMDevice? _device;
    private AudioSessionManager? _sessionManager;
    private AudioEndpointVolume? _endpointVolume;
    private AudioEndpointVolumeNotificationDelegate? _masterHandler;
    private int _deviceGeneration;
    private float _masterVolume;
    private bool _masterMuted;
    private float? _masterEchoVolume;
    private bool _masterEchoMuted;

    /// <summary>
    /// App এর list বা কারও volume/mute বাইরে থেকে (যেমন Windows এর Volume Mixer) বদলালে, audio thread
    /// থেকে। PulseBar নিজে যে volume/mute লিখেছে তার ফিরতি event এ এটা আসে না।
    /// </summary>
    public event EventHandler? AppsChanged;

    /// <summary>Device যোগ/সরানো, state বা default বদলালে, audio thread থেকে।</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>
    /// Master volume/mute বাইরে থেকে (volume key, Windows এর flyout) বদলালে বা default device বদলালে,
    /// audio thread থেকে। PulseBar নিজে যা লিখেছে তার ফিরতি notification এ আসে না।
    /// </summary>
    public event EventHandler? MasterChanged;

    /// <summary>Default device বদলানো যায়নি (IPolicyConfig কাজ করেনি), audio thread থেকে।</summary>
    public event EventHandler? DeviceSwitchFailed;

    /// <summary>এই মুহূর্তের app গুলো: আগে System sounds, তারপর নাম অনুযায়ী।</summary>
    public IReadOnlyList<AudioAppSession> GetApps()
    {
        lock (_gate)
        {
            return _apps;
        }
    }

    /// <summary>Active output device গুলো, নাম অনুযায়ী।</summary>
    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        lock (_gate)
        {
            return _devices;
        }
    }

    /// <summary>Default device এর master volume; কোনো device না থাকলে বা পড়া না গেলে null।</summary>
    public MasterVolumeState? GetMaster()
    {
        lock (_gate)
        {
            return _master;
        }
    }

    public void Start()
    {
        if (_thread is not null || _disposed)
        {
            return;
        }

        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PulseBar audio",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        Enqueue(Initialize);
    }

    /// <summary>App এর সব session এ volume (0.0–1.0) বসায়।</summary>
    public void SetVolume(string appKey, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        Enqueue(() => ForEachSession(appKey, entry =>
        {
            entry.ExpectEcho(volume, entry.Muted);
            entry.Control.SimpleAudioVolume.Volume = volume;
            entry.Volume = volume;
        }));
    }

    /// <summary>App এর সব session mute বা unmute করে।</summary>
    public void SetMute(string appKey, bool mute) =>
        Enqueue(() => ForEachSession(appKey, entry =>
        {
            entry.ExpectEcho(entry.Volume, mute);
            entry.Control.SimpleAudioVolume.Mute = mute;
            entry.Muted = mute;
        }));

    /// <summary>
    /// Device টাকে Console, Multimedia আর Communications তিন role এরই default বানায়। সফল হলে
    /// Windows এর DefaultDeviceChanged থেকে সব নতুন করে বানানো হয়; না হলে <see cref="DeviceSwitchFailed"/>।
    /// </summary>
    public void SetDefaultDevice(string deviceId) =>
        Enqueue(() =>
        {
            if (AudioPolicyConfig.TrySetDefaultEndpoint(deviceId, out string? error))
            {
                Log.Info($"Audio: set default output device {deviceId}");
                return;
            }

            Log.Error($"Audio: could not set default output device {deviceId}: {error}");
            DeviceSwitchFailed?.Invoke(this, EventArgs.Empty);
        });

    /// <summary>Default device এর master volume (0.0–1.0)।</summary>
    public void SetMasterVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        Enqueue(() =>
        {
            if (_endpointVolume is null)
            {
                return;
            }

            ExpectMasterEcho(volume, _masterMuted);
            _endpointVolume.MasterVolumeLevelScalar = volume;
            _masterVolume = volume;
            PublishMaster(raise: false);
        });
    }

    public void SetMasterMute(bool mute) =>
        Enqueue(() =>
        {
            if (_endpointVolume is null)
            {
                return;
            }

            ExpectMasterEcho(_masterVolume, mute);
            _endpointVolume.Mute = mute;
            _masterMuted = mute;
            PublishMaster(raise: false);
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Thread এর queue তে শেষ কাজ: সব COM object ছেড়ে দেওয়া; তারপর thread নিজেই শেষ হয়
        _work.Enqueue(ReleaseAll);
        _disposed = true;
        _signal.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }

    private bool Enqueue(Action action)
    {
        if (_disposed)
        {
            return false;
        }

        _work.Enqueue(action);
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            try
            {
                _signal.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            while (_work.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // কোনো COM error এ thread থামবে না
                    Log.Error("Audio work", ex);
                }
            }

            if (_disposed)
            {
                return;
            }
        }
    }

    private void Initialize()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _deviceNotifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _deviceNotifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
                {
                    string? id = e.DeviceId;
                    Enqueue(() => OnDefaultDeviceChanged(id));
                }
            };

            // যেমন Bluetooth headphone connect/disconnect; capture device এরটাও আসে, তাতে শুধু একবার বাড়তি পড়া
            _deviceNotifications.DeviceAdded += (_, _) => QueueDeviceRefresh();
            _deviceNotifications.DeviceRemoved += (_, _) => QueueDeviceRefresh();
            _deviceNotifications.DeviceStateChanged += (_, _) => QueueDeviceRefresh();
        }
        catch (Exception ex)
        {
            // Audio service বন্ধ ইত্যাদি: Sound tab খালি থাকবে, app চলবে
            Log.Error("Audio: device enumerator unavailable", ex);
            return;
        }

        Rebuild();
    }

    /// <summary>
    /// পুরনো device এর সব কিছু (session, master notification) ছেড়ে দিয়ে বর্তমান default device থেকে
    /// app list, master volume আর device list নতুন করে বানায়।
    /// </summary>
    private void Rebuild()
    {
        ReleaseDevice();

        if (_enumerator is null)
        {
            return;
        }

        MMDevice device;
        try
        {
            if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var found) || found is null)
            {
                Log.Info("Audio: no default output device");
                PublishNoDevice();
                return;
            }

            device = found;
            _device = device;
            _sessionManager = device.AudioSessionManager;
            _sessionManager.OnSessionCreated += OnSessionCreated;
            Log.Info($"Audio: default output device '{device.FriendlyName}'");
        }
        catch (Exception ex)
        {
            Log.Error("Audio: could not open the default output device", ex);
            ReleaseDevice();
            PublishNoDevice();
            return;
        }

        AttachMaster(device);
        RefreshDevices();
        Resync();
    }

    /// <summary>
    /// একই device আবার default বসালেও Windows এটা পাঠায়; তখন সব ছেড়ে আবার বানানোর দরকার নেই।
    /// </summary>
    private void OnDefaultDeviceChanged(string? deviceId)
    {
        string? currentId = null;
        try
        {
            currentId = _device?.ID;
        }
        catch
        {
            // পুরনো device নষ্ট: নতুন করে বানাতেই হবে
        }

        if (deviceId is not null && deviceId == currentId)
        {
            return;
        }

        Rebuild();
    }

    private void PublishNoDevice()
    {
        Publish(raise: true);
        PublishMaster(raise: true);
        RefreshDevices();
    }

    /// <summary>Master volume পড়ে আর তার notification এ লাগে। না পারলে master null, app list তবুও চলে।</summary>
    private void AttachMaster(MMDevice device)
    {
        // পুরনো device এর দেরিতে আসা notification চেনার জন্য
        int generation = ++_deviceGeneration;
        _masterEchoVolume = null;

        try
        {
            var endpoint = device.AudioEndpointVolume;
            _masterVolume = endpoint.MasterVolumeLevelScalar;
            _masterMuted = endpoint.Mute;

            // COM thread থেকে আসে; data টা সেখানেই পড়ে শুধু queue তে পাঠানো
            _masterHandler = data =>
            {
                float volume = data.MasterVolume;
                bool muted = data.Muted;
                Enqueue(() => OnMasterNotification(generation, volume, muted));
            };
            endpoint.OnVolumeNotification += _masterHandler;
            _endpointVolume = endpoint;
        }
        catch (Exception ex)
        {
            Log.Error("Audio: master volume unavailable", ex);
            _endpointVolume = null;
            _masterHandler = null;
        }

        PublishMaster(raise: true);
    }

    private void OnMasterNotification(int generation, float volume, bool muted)
    {
        if (generation != _deviceGeneration || _endpointVolume is null)
        {
            return;
        }

        bool echo = _masterEchoVolume is { } expected &&
                    Math.Abs(expected - volume) < EchoTolerance &&
                    muted == _masterEchoMuted;
        _masterVolume = volume;
        _masterMuted = muted;
        PublishMaster(raise: !echo);
    }

    /// <summary>এখনই এই মান লেখা হচ্ছে; এর ফিরতি notification কে বাইরের পরিবর্তন ধরা হবে না।</summary>
    private void ExpectMasterEcho(float volume, bool muted)
    {
        _masterEchoVolume = volume;
        _masterEchoMuted = muted;
    }

    private void PublishMaster(bool raise)
    {
        var master = _endpointVolume is null ? null : new MasterVolumeState(_masterVolume, _masterMuted);
        lock (_gate)
        {
            _master = master;
        }

        if (raise)
        {
            MasterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>COM thread থেকে; অনেকগুলো একসাথে এলেও একবারই পড়া হয়।</summary>
    private void QueueDeviceRefresh()
    {
        if (Interlocked.Exchange(ref _devicesQueued, 1) == 0)
        {
            Enqueue(RefreshDevices);
        }
    }

    /// <summary>Active output device গুলো নতুন করে পড়ে; কিছু না বদলালে event দেয় না।</summary>
    private void RefreshDevices()
    {
        Interlocked.Exchange(ref _devicesQueued, 0);
        var list = new List<AudioOutputDevice>();

        if (_enumerator is not null)
        {
            string? defaultId = null;
            try
            {
                defaultId = _device?.ID;
            }
            catch
            {
                // Default device এর মধ্যে সরে গেছে; DefaultDeviceChanged এ আবার আসবে
            }

            try
            {
                using var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                int count = collection.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        using var device = collection[i];
                        string id = device.ID;
                        list.Add(new AudioOutputDevice
                        {
                            Id = id,
                            Name = device.FriendlyName,
                            Kind = KindOf(device),
                            IsDefault = id == defaultId,
                        });
                    }
                    catch (Exception ex)
                    {
                        // পড়ার মাঝে সরানো হয়েছে ইত্যাদি
                        Log.Error($"Audio: skipped output device {i}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Audio: output device enumeration failed", ex);
            }
        }

        var devices = list
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            if (devices.SequenceEqual(_devices))
            {
                return;
            }

            _devices = devices;
        }

        Log.Info($"Audio: {devices.Length} output devices");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static AudioDeviceKind KindOf(MMDevice device)
    {
        try
        {
            if (!device.Properties.TryGetValue(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out uint formFactor))
            {
                return AudioDeviceKind.Other;
            }

            // EndpointFormFactor
            return formFactor switch
            {
                1 => AudioDeviceKind.Speakers,
                3 => AudioDeviceKind.Headphones,
                5 or 6 => AudioDeviceKind.Headset, // Headset, Handset
                9 => AudioDeviceKind.Display,      // DigitalAudioDisplayDevice: HDMI/DisplayPort
                _ => AudioDeviceKind.Other,
            };
        }
        catch
        {
            return AudioDeviceKind.Other;
        }
    }

    /// <summary>COM thread থেকে। এই object টা ধার করা, তাই রাখা হয় না; পুরো list আবার মেলানো হয়।</summary>
    private void OnSessionCreated(object sender, AudioSessionControl newSession)
    {
        try
        {
            newSession.Dispose();
        }
        catch
        {
            // ধার করা wrapper; ছাড়তে না পারলেও ক্ষতি নেই
        }

        if (Interlocked.Exchange(ref _resyncQueued, 1) == 0)
        {
            Enqueue(Resync);
        }
    }

    /// <summary>
    /// Session এর list নতুন করে পড়ে: নতুনগুলো যোগ, আর যেগুলো আর নেই বা expired সেগুলো বাদ।
    /// কোনো session পড়তে না পারলে (access denied ইত্যাদি) শুধু সেটা বাদ যায়।
    /// </summary>
    private void Resync()
    {
        Interlocked.Exchange(ref _resyncQueued, 0);
        if (_sessionManager is null)
        {
            return;
        }

        var seen = new HashSet<string>();
        try
        {
            _sessionManager.RefreshSessions();
            var sessions = _sessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? control = null;
                try
                {
                    control = sessions[i];
                    if (control.State == AudioSessionState.AudioSessionStateExpired)
                    {
                        control.Dispose();
                        continue;
                    }

                    string id = control.GetSessionInstanceIdentifier;
                    seen.Add(id);
                    if (_sessions.ContainsKey(id))
                    {
                        control.Dispose();
                        continue;
                    }

                    if (CreateEntry(id, control) is { } entry)
                    {
                        _sessions[id] = entry;
                        control = null; // এখন entry র
                        WatchProcess(entry.ProcessId);
                    }
                    else
                    {
                        control.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Audio: skipped session {i}", ex);
                    try
                    {
                        control?.Dispose();
                    }
                    catch
                    {
                        // ইতিমধ্যে নষ্ট
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio: session enumeration failed", ex);
        }

        foreach (var gone in _sessions.Values.Where(e => !seen.Contains(e.InstanceId)).ToList())
        {
            RemoveEntry(gone);
        }

        Publish(raise: true);
        Log.Info($"Audio: {GetApps().Count} apps from {_sessions.Count} sessions");
    }

    private SessionEntry? CreateEntry(string id, AudioSessionControl control)
    {
        int pid = (int)control.GetProcessID;
        bool isSystem = pid == 0 || control.IsSystemSoundsSession;

        string key;
        string name;
        string? iconPath;
        int iconIndex = 0;

        if (isSystem)
        {
            key = AudioAppSession.SystemSoundsKey;
            name = SystemSoundsName;
            (iconPath, iconIndex) = ParseIconPath(control.IconPath);
        }
        else
        {
            // Process এর exe পড়া না গেলে (access denied, বা ইতিমধ্যে বন্ধ) row বাদ
            if (ExePathOf(pid) is not { } exe)
            {
                return null;
            }

            key = exe.ToLowerInvariant();
            name = AppNameOf(exe);
            iconPath = exe;
        }

        var volume = control.SimpleAudioVolume;
        var entry = new SessionEntry(this, id, pid, key, name, iconPath, iconIndex, isSystem, control)
        {
            Volume = volume.Volume,
            Muted = volume.Mute,
        };

        control.RegisterEventClient(entry);
        return entry;
    }

    private void RemoveEntry(SessionEntry entry)
    {
        if (!_sessions.Remove(entry.InstanceId))
        {
            return;
        }

        entry.Release();

        // ওই process এর আর কোনো session না থাকলে তার exit দেখাও বন্ধ
        if (!_sessions.Values.Any(e => e.ProcessId == entry.ProcessId) &&
            _processWatchers.Remove(entry.ProcessId, out var process))
        {
            process?.Dispose();
        }
    }

    /// <summary>Process বন্ধ হলে তার সব session সাথে সাথে বাদ (session expired হতে দেরি হয়)।</summary>
    private void WatchProcess(int pid)
    {
        if (pid == 0 || _processWatchers.ContainsKey(pid))
        {
            return;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Enqueue(() => OnProcessExited(pid));
        }
        catch
        {
            // Admin process ইত্যাদি: exit দেখা যাবে না, session expired হলে তখন বাদ যাবে
            process?.Dispose();
            process = null;
        }

        _processWatchers[pid] = process;
    }

    private void OnProcessExited(int pid)
    {
        var gone = _sessions.Values.Where(e => e.ProcessId == pid).ToList();
        foreach (var entry in gone)
        {
            RemoveEntry(entry);
        }

        if (_processWatchers.Remove(pid, out var process))
        {
            process?.Dispose();
        }

        if (gone.Count > 0)
        {
            Publish(raise: true);
        }
    }

    private void ForEachSession(string appKey, Action<SessionEntry> action)
    {
        foreach (var entry in _sessions.Values.Where(e => e.AppKey == appKey).ToList())
        {
            try
            {
                action(entry);
            }
            catch (Exception ex)
            {
                // Session এর মধ্যে নষ্ট হয়ে গেছে
                Log.Error($"Audio: could not change '{entry.Name}'", ex);
                RemoveEntry(entry);
            }
        }

        // নিজের পরিবর্তন: snapshot ঠিক রাখা হয়, কিন্তু event না (UI তে আগেই বসানো আছে)
        Publish(raise: false);
    }

    /// <summary>Session এর event, audio thread এ।</summary>
    private void OnSessionVolumeChanged(SessionEntry entry, float volume, bool muted)
    {
        if (!_sessions.ContainsKey(entry.InstanceId))
        {
            return;
        }

        bool echo = entry.IsEcho(volume, muted);
        entry.Volume = volume;
        entry.Muted = muted;
        Publish(raise: !echo);
    }

    private void OnSessionEnded(SessionEntry entry)
    {
        if (_sessions.ContainsKey(entry.InstanceId))
        {
            RemoveEntry(entry);
            Publish(raise: true);
        }
    }

    /// <summary>Session গুলোকে app অনুযায়ী জোড়া দিয়ে নতুন snapshot।</summary>
    private void Publish(bool raise)
    {
        var apps = _sessions.Values
            .GroupBy(e => e.AppKey)
            .Select(group =>
            {
                var first = group.First();
                return new AudioAppSession
                {
                    Key = first.AppKey,
                    Name = first.Name,
                    IconPath = first.IconPath,
                    IconIndex = first.IconIndex,
                    IsSystemSounds = first.IsSystem,
                    Volume = group.Max(e => e.Volume),
                    IsMuted = group.All(e => e.Muted),
                };
            })
            .OrderBy(app => app.IsSystemSounds ? 0 : 1)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            _apps = apps;
        }

        if (raise)
        {
            AppsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReleaseDevice()
    {
        foreach (var entry in _sessions.Values.ToList())
        {
            RemoveEntry(entry);
        }

        foreach (var process in _processWatchers.Values)
        {
            process?.Dispose();
        }

        _processWatchers.Clear();

        try
        {
            if (_endpointVolume is not null && _masterHandler is not null)
            {
                _endpointVolume.OnVolumeNotification -= _masterHandler;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio: master notification release", ex);
        }

        // Endpoint volume নিজে device এর Dispose এ ছাড়া হয় (notification registration সহ)
        _endpointVolume = null;
        _masterHandler = null;

        try
        {
            if (_sessionManager is not null)
            {
                // Session notification এর registration ও এতে ছাড়া হয়
                _sessionManager.OnSessionCreated -= OnSessionCreated;
                _sessionManager.Dispose();
            }

            _device?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Audio: device dispose", ex);
        }

        _sessionManager = null;
        _device = null;
    }

    private void ReleaseAll()
    {
        ReleaseDevice();

        try
        {
            _deviceNotifications?.Dispose();
            _enumerator?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Audio: enumerator dispose", ex);
        }

        _deviceNotifications = null;
        _enumerator = null;
    }

    private string AppNameOf(string exe)
    {
        if (_appNames.TryGetValue(exe, out var cached))
        {
            return cached;
        }

        string name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            if (FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim() is { Length: > 0 } description)
            {
                name = description;
            }
        }
        catch
        {
            // Version info নেই: exe এর নাম
        }

        _appNames[exe] = name;
        return name;
    }

    /// <summary>"@%SystemRoot%\System32\AudioSrv.Dll,-203" এর মতো লেখা থেকে file আর index।</summary>
    private static (string? Path, int Index) ParseIconPath(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return (null, 0);
        }

        string value = Environment.ExpandEnvironmentVariables(iconPath.Trim().TrimStart('@'));
        int comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..], out int index))
        {
            return (value[..comma], index);
        }

        return (value, 0);
    }

    private static string? ExePathOf(int pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>একটা audio session, তার COM wrapper আর event callback।</summary>
    private sealed class SessionEntry(
        AudioController owner,
        string instanceId,
        int processId,
        string appKey,
        string name,
        string? iconPath,
        int iconIndex,
        bool isSystem,
        AudioSessionControl control) : IAudioSessionEventsHandler
    {
        private float? _echoVolume;
        private bool _echoMuted;

        public string InstanceId { get; } = instanceId;

        public int ProcessId { get; } = processId;

        public string AppKey { get; } = appKey;

        public string Name { get; } = name;

        public string? IconPath { get; } = iconPath;

        public int IconIndex { get; } = iconIndex;

        public bool IsSystem { get; } = isSystem;

        public AudioSessionControl Control { get; } = control;

        public float Volume { get; set; }

        public bool Muted { get; set; }

        /// <summary>এখনই এই মান লেখা হচ্ছে; এর ফিরতি event কে বাইরের পরিবর্তন ধরা হবে না।</summary>
        public void ExpectEcho(float volume, bool muted)
        {
            _echoVolume = volume;
            _echoMuted = muted;
        }

        public bool IsEcho(float volume, bool muted) =>
            _echoVolume is { } expected && Math.Abs(expected - volume) < EchoTolerance && muted == _echoMuted;

        public void Release()
        {
            try
            {
                Control.UnRegisterEventClient(this);
            }
            catch
            {
                // Session ইতিমধ্যে নষ্ট
            }

            try
            {
                Control.Dispose();
            }
            catch
            {
                // একই
            }
        }

        // নিচের সব COM thread থেকে আসে; শুধু audio thread এ কাজ পাঠায়
        public void OnVolumeChanged(float volume, bool isMuted) =>
            owner.Enqueue(() => owner.OnSessionVolumeChanged(this, volume, isMuted));

        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                owner.Enqueue(() => owner.OnSessionEnded(this));
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) =>
            owner.Enqueue(() => owner.OnSessionEnded(this));

        public void OnDisplayNameChanged(string displayName)
        {
        }

        public void OnIconPathChanged(string iconPath)
        {
        }

        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
        {
        }

        public void OnGroupingParamChanged(ref Guid groupingId)
        {
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);
}
