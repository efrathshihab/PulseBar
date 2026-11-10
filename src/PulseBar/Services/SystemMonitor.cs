using System.IO;
using System.Runtime.InteropServices;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// Background এ প্রতি ১ সেকেন্ডে CPU আর RAM, প্রতি ১০ সেকেন্ডে drive পড়ে
/// <see cref="SnapshotUpdated"/> event দিয়ে পাঠায়। Event background thread থেকে আসে।
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

    public event EventHandler<SystemSnapshot>? SnapshotUpdated;

    /// <summary>সবশেষ snapshot, এখনো না নেওয়া হলে null।</summary>
    public SystemSnapshot? Latest { get; private set; }

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
            // Cancel হওয়ার exception, উপেক্ষা
        }

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken token)
    {
        // প্রথম CPU sample এর ভিত্তি; এরপর প্রথম tick থেকেই % পাওয়া যাবে
        ReadCpuPercent();

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

        var snapshot = new SystemSnapshot(cpu, used, total, ramPercent, _drives, now);
        Latest = snapshot;

        try
        {
            SnapshotUpdated?.Invoke(this, snapshot);
        }
        catch
        {
            // কোনো subscriber এর ভুলে monitor loop থামবে না
        }
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
