using System.IO;
using System.Runtime.InteropServices;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// Background এ প্রতি ১ সেকেন্ডে CPU, RAM আর network, প্রতি ১০ সেকেন্ডে drive পড়ে
/// (temperature আলাদা <see cref="TemperatureMonitor"/> থেকে, প্রতি ২ সেকেন্ডে)
/// সরাসরি <see cref="AppState"/> এ লেখে (background thread থেকে)।
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
            _temperature.Start();
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
            // Cancel হওয়ার exception, উপেক্ষা
        }

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        _temperature.Dispose(); // ভেতরে Computer.Close()
        _network.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        // প্রথম CPU আর network sample এর ভিত্তি; এরপর প্রথম tick থেকেই মান পাওয়া যাবে
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
            // Stop() এ স্বাভাবিক থামা
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

        var snapshot = new SystemSnapshot(
            cpu, used, total, ramPercent, _drives,
            download, upload,
            _temperature.CurrentCelsius, _temperature.Status,
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

            // kernel time এর ভেতরে idle time ও ধরা থাকে
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
            // N/A থাকবে
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
                // এই drive বাদ, বাকিগুলো চলবে
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

    // FILETIME (দুটো 32-bit) কে সরাসরি 64-bit হিসেবে পড়া হচ্ছে
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
