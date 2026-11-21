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
/// Lists output devices and switches the default device, the default device's master volume, and its
/// audio sessions (like the Volume Mixer) grouped by exe for per-app volume/mute.
/// </summary>
/// <remarks>
/// All COM work runs in order on one dedicated MTA thread. COM events (new session, volume change,
/// device change) arrive on any thread; they only queue work onto this thread.
/// The UI gets immutable snapshots (<see cref="GetApps"/>, <see cref="GetDevices"/>, <see cref="GetMaster"/>)
/// and their Changed events.
/// </remarks>
public sealed class AudioController : IDisposable
{
    private const string SystemSoundsName = "System sounds";

    // To recognize the echo of our own volume writes (float rounding)
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

    // Everything below is touched only from the audio thread
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
    /// Raised from the audio thread when the app list or someone's volume/mute changes from outside (such as the
    /// Windows Volume Mixer). Not raised for the echo of volume/mute that PulseBar itself wrote.
    /// </summary>
    public event EventHandler? AppsChanged;

    /// <summary>Raised from the audio thread when a device is added/removed or its state or the default changes.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>
    /// Raised from the audio thread when master volume/mute changes from outside (volume keys, the Windows flyout)
    /// or the default device changes. Not raised for the echo of what PulseBar itself wrote.
    /// </summary>
    public event EventHandler? MasterChanged;

    /// <summary>Raised from the audio thread when the default device couldn't be changed (IPolicyConfig failed).</summary>
    public event EventHandler? DeviceSwitchFailed;

    /// <summary>The current apps: System sounds first, then by name.</summary>
    public IReadOnlyList<AudioAppSession> GetApps()
    {
        lock (_gate)
        {
            return _apps;
        }
    }

    /// <summary>Active output devices, by name.</summary>
    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        lock (_gate)
        {
            return _devices;
        }
    }

    /// <summary>Master volume of the default device; null if there is no device or it can't be read.</summary>
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

    /// <summary>Sets the volume (0.0–1.0) on all of the app's sessions.</summary>
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

    /// <summary>Mutes or unmutes all of the app's sessions.</summary>
    public void SetMute(string appKey, bool mute) =>
        Enqueue(() => ForEachSession(appKey, entry =>
        {
            entry.ExpectEcho(entry.Volume, mute);
            entry.Control.SimpleAudioVolume.Mute = mute;
            entry.Muted = mute;
        }));

    /// <summary>
    /// Makes the device the default for all three roles: Console, Multimedia and Communications. On success
    /// everything is rebuilt from Windows' DefaultDeviceChanged; otherwise <see cref="DeviceSwitchFailed"/>.
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

    /// <summary>Master volume of the default device (0.0–1.0).</summary>
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

        // Last job on the thread's queue: release all COM objects; then the thread ends by itself
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
                    // A COM error never stops the thread
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

            // e.g. Bluetooth headphones connecting/disconnecting; capture devices also report, costing just one extra read
            _deviceNotifications.DeviceAdded += (_, _) => QueueDeviceRefresh();
            _deviceNotifications.DeviceRemoved += (_, _) => QueueDeviceRefresh();
            _deviceNotifications.DeviceStateChanged += (_, _) => QueueDeviceRefresh();
        }
        catch (Exception ex)
        {
            // Audio service stopped and so on: the Sound tab stays empty, the app keeps running
            Log.Error("Audio: device enumerator unavailable", ex);
            return;
        }

        Rebuild();
    }

    /// <summary>
    /// Releases everything from the old device (sessions, master notification) and rebuilds the app list,
    /// master volume and device list from the current default device.
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
    /// Windows sends this even when the same device is set as default again; then there's no need to rebuild.
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
            // Old device is broken: must rebuild
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

    /// <summary>Reads master volume and subscribes to its notifications. On failure master is null, but the app list still works.</summary>
    private void AttachMaster(MMDevice device)
    {
        // To recognize late notifications from the old device
        int generation = ++_deviceGeneration;
        _masterEchoVolume = null;

        try
        {
            var endpoint = device.AudioEndpointVolume;
            _masterVolume = endpoint.MasterVolumeLevelScalar;
            _masterMuted = endpoint.Mute;

            // Comes from a COM thread; read the data there and only queue it
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

    /// <summary>This value is being written now; its echo notification won't count as an outside change.</summary>
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

    /// <summary>From a COM thread; many arriving together are read only once.</summary>
    private void QueueDeviceRefresh()
    {
        if (Interlocked.Exchange(ref _devicesQueued, 1) == 0)
        {
            Enqueue(RefreshDevices);
        }
    }

    /// <summary>Rereads the active output devices; raises no event if nothing changed.</summary>
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
                // Moved between default devices; DefaultDeviceChanged will come again
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
                        // Removed while being read, and so on
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

    /// <summary>From a COM thread. This object is borrowed, so it isn't kept; the whole list is reconciled again.</summary>
    private void OnSessionCreated(object sender, AudioSessionControl newSession)
    {
        try
        {
            newSession.Dispose();
        }
        catch
        {
            // Borrowed wrapper; no harm if it can't be released
        }

        if (Interlocked.Exchange(ref _resyncQueued, 1) == 0)
        {
            Enqueue(Resync);
        }
    }

    /// <summary>
    /// Rereads the session list: adds new ones and drops ones that are gone or expired.
    /// If a session can't be read (access denied and so on), only that one is skipped.
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
                        control = null; // now owned by the entry
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
                        // Already broken
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
            // If the process exe can't be read (access denied, or already exited), skip the row
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

        // Stop watching for the process exit if it has no other sessions
        if (!_sessions.Values.Any(e => e.ProcessId == entry.ProcessId) &&
            _processWatchers.Remove(entry.ProcessId, out var process))
        {
            process?.Dispose();
        }
    }

    /// <summary>When a process exits, drop all its sessions right away (session expiry is slow).</summary>
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
            // Admin processes and the like: exit can't be watched, the session is dropped when it expires
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
                // Broken in the meantime
                Log.Error($"Audio: could not change '{entry.Name}'", ex);
                RemoveEntry(entry);
            }
        }

        // Our own change: the snapshot is kept correct, but no event (the UI already shows it)
        Publish(raise: false);
    }

    /// <summary>Session events, on the audio thread.</summary>
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

    /// <summary>A new snapshot with sessions grouped by app.</summary>
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

        // The endpoint volume is released in the device's own Dispose (including the notification registration)
        _endpointVolume = null;
        _masterHandler = null;

        try
        {
            if (_sessionManager is not null)
            {
                // This also releases the session notification registration
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
            // No version info: use the exe name
        }

        _appNames[exe] = name;
        return name;
    }

    /// <summary>File and index from text like "@%SystemRoot%\System32\AudioSrv.Dll,-203".</summary>
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

    /// <summary>One audio session, its COM wrapper and event callback.</summary>
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

        /// <summary>This value is being written now; its echo event won't count as an outside change.</summary>
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
                // Session already broken
            }

            try
            {
                Control.Dispose();
            }
            catch
            {
                // Same
            }
        }

        // Everything below comes from COM threads; it only queues work onto the audio thread
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
