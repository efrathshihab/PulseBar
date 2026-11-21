using System.IO;
using System.Runtime.InteropServices;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// Reads CPU, RAM, network and GPU every second and drives every 10 seconds in the background
/// (CPU/GPU temperature comes separately from <see cref="TemperatureMonitor"/>, every 2 seconds)
/// and writes straight into <see cref="AppState"/> (from a background thread).
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DriveInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private ulong _prevIdle;
    private ulong _prevTotal;
    private bool _hasPrevCpu;

    private IReadOnlyList<DriveUsage> _drives = [];
    private DateTime _lastDriveRead = DateTime.MinValue;

    private readonly NetworkMonitor _network = new();
    private readonly TemperatureMonitor _temperature = new();
    private GpuMonitor? _gpu;

    private readonly AppState _state;

    public SystemMonitor(AppState state)
    {
        _state = state;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), token);
        }
    }

    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            if (_loop is null)
            {
                return;
            }

            _cts!.Cancel();
            loop = _loop;
            _loop = null;
        }

        try
        {
            loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation exception, ignore
        }

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        _temperature.Dispose(); // calls Computer.Close() inside
        _network.Dispose();
        _gpu?.Dispose();
        _gpu = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        // Starting the GPU (DXGI + PDH) is a bit slow, so it happens here instead of on the UI thread
        _gpu ??= new GpuMonitor();

        // Temperature starts only after the main GPU name is known, so the right one is picked with several GPUs
        _temperature.PreferredGpuName = _gpu.Name;
        _temperature.Start();

        // Baseline for the first CPU and network sample; values are available from the first tick
        ReadCpuPercent();
        _network.TryRead(out _, out _);

        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Sample();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop from Stop()
        }
    }

    private void Sample()
    {
        var now = DateTime.Now;
        double? cpu = ReadCpuPercent();
        ReadMemory(out var used, out var total, out var ramPercent);

        if (now - _lastDriveRead >= DriveInterval)
        {
            _drives = ReadDrives();
            _lastDriveRead = now;
        }

        ulong? download = null;
        ulong? upload = null;
        if (_network.TryRead(out var down, out var up))
        {
            download = down;
            upload = up;
        }

        double? gpuPercent = null;
        ulong? vramUsed = null;
        _gpu?.Read(out gpuPercent, out vramUsed);

        var snapshot = new SystemSnapshot(
            cpu, used, total, ramPercent, _drives,
            download, upload,
            _temperature.CurrentCelsius, _temperature.Status,
            gpuPercent, _gpu?.Name, vramUsed, _gpu?.TotalVramBytes, _temperature.GpuCelsius,
            now);
        _state.UpdateSnapshot(snapshot);
    }

    private double? ReadCpuPercent()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _hasPrevCpu = false;
                return null;
            }

            // kernel time also includes idle time
            ulong total = kernel + user;
            double? result = null;

            if (_hasPrevCpu && total > _prevTotal)
            {
                double totalDelta = total - _prevTotal;
                double idleDelta = idle >= _prevIdle ? idle - _prevIdle : 0;
                result = Math.Clamp((totalDelta - idleDelta) / totalDelta * 100.0, 0, 100);
            }

            _prevIdle = idle;
            _prevTotal = total;
            _hasPrevCpu = true;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadMemory(out ulong? used, out ulong? total, out double? percent)
    {
        used = null;
        total = null;
        percent = null;

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            {
                return;
            }

            ulong usedBytes = status.ullTotalPhys - status.ullAvailPhys;
            used = usedBytes;
            total = status.ullTotalPhys;
            percent = (double)usedBytes / status.ullTotalPhys * 100.0;
        }
        catch
        {
            // Stays N/A
        }
    }

    private static IReadOnlyList<DriveUsage> ReadDrives()
    {
        var list = new List<DriveUsage>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return list;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                ulong totalBytes = (ulong)drive.TotalSize;
                if (totalBytes == 0)
                {
                    continue;
                }

                ulong usedBytes = totalBytes - (ulong)drive.TotalFreeSpace;
                string label;
                try
                {
                    label = drive.VolumeLabel;
                }
                catch
                {
                    label = string.Empty;
                }

                list.Add(new DriveUsage(
                    drive.Name,
                    label,
                    usedBytes,
                    totalBytes,
                    (double)usedBytes / totalBytes * 100.0));
            }
            catch
            {
                // Skip this drive, the rest keep working
            }
        }

        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // FILETIME (two 32-bit values) read directly as 64-bit
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
