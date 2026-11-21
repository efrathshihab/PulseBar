using System.IO;
using System.Runtime.InteropServices;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Windows এর Screenshots folder (Win+PrtSc, Snipping Tool এর auto-save) এ নতুন .png/.jpg এলে
/// তার bytes পড়ে <c>onScreenshot</c> এ দেয়। শুধু নতুন file: app চালু হওয়ার আগের file ধরা হয় না।
/// <para>
/// ⚠️ User এর Screenshots folder এর কোনো file কখনো মোছা, সরানো বা বদলানো হয় না; শুধু পড়া হয়।
/// History তে থাকে PulseBar এর নিজের copy।
/// </para>
/// File গুলো queue তে রেখে একটা একটা করে পড়া হয়; একসাথে অনেক এলে <see cref="MaxQueued"/> এর
/// বেশিগুলো বাদ যায়। চুপচাপ থাকলে কোনো thread বা timer চলে না।
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    public const int MaxQueued = 20;

    // এর চেয়ে বড় file screenshot না, পড়াই হবে না
    private const long MaxFileBytes = 64L * 1024 * 1024;

    // লেখা শেষ হওয়ার অপেক্ষা: প্রথমে একটু থেমে, তারপর এতবার খোলার চেষ্টা
    private const int FirstDelayMs = 150;
    private const int OpenRetryCount = 10;
    private const int OpenRetryDelayMs = 200;

    // এক file এর কাজ (ClipboardManager এর decode + save) এর বেশি নিলে পরেরটায়
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(15);

    // Rename এ আসা file এর তৈরির সময় app চালুর এর বেশি আগে হলে সেটা পুরনো file
    private static readonly TimeSpan CreationSlack = TimeSpan.FromSeconds(2);

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg"];

    private readonly Func<byte[], string, Task> _onScreenshot;
    private readonly Lock _gate = new();
    private readonly Queue<string> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
    private FileSystemWatcher? _watcher;
    private DateTime _startedUtc;
    private bool _processing;
    private int _dropped;
    private bool _enabled;
    private bool _disposed;

    /// <param name="onScreenshot">
    /// File এর bytes আর path। একটা একটা করে ডাকা হয় (background thread এ); ফেরত দেওয়া Task শেষ
    /// হলে পরের file।
    /// </param>
    /// <param name="folder">Test এর জন্য অন্য folder; null হলে Windows এর Screenshots folder।</param>
    public ScreenshotWatcher(Func<byte[], string, Task> onScreenshot, string? folder = null)
    {
        _onScreenshot = onScreenshot;
        Folder = folder ?? GetScreenshotsFolder();
    }

    /// <summary>যে folder দেখা হচ্ছে; পাওয়া না গেলে null।</summary>
    public string? Folder { get; }

    /// <summary>Folder দেখা চালু/বন্ধ। Folder না থাকলে চালু করলেও কিছু হয় না (log এ লেখা হয়)।</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            lock (_gate)
            {
                if (_disposed || _enabled == value)
                {
                    return;
                }

                _enabled = value;
                if (value)
                {
                    StartWatcher();
                }
                else
                {
                    StopWatcher();
                    _queue.Clear();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            StopWatcher();
            _queue.Clear();
        }

        _shutdown.Cancel();
    }

    /// <summary>_gate এর ভেতরে ডাকা হয়।</summary>
    private void StartWatcher()
    {
        if (Folder is null)
        {
            Log.Info("Screenshots folder: not found; watcher idle");
            return;
        }

        if (!Directory.Exists(Folder))
        {
            Log.Info($"Screenshots folder does not exist ({Folder}); watcher idle");
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(Folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
            };

            foreach (string extension in Extensions)
            {
                watcher.Filters.Add("*" + extension);
            }

            watcher.Created += OnCreated;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;

            _startedUtc = DateTime.UtcNow;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            Log.Info($"Screenshots folder: watching {Folder}");
        }
        catch (Exception ex)
        {
            Log.Error($"Screenshots folder watcher ({Folder})", ex);
            _watcher = null;
        }
    }

    /// <summary>_gate এর ভেতরে ডাকা হয়।</summary>
    private void StopWatcher()
    {
        if (_watcher is not { } watcher)
        {
            return;
        }

        _watcher = null;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Screenshots folder watcher stop", ex);
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath, renamed: false);

    // কিছু tool আগে temp নামে লেখে, পরে .png নাম দেয়
    private void OnRenamed(object sender, RenamedEventArgs e) => Enqueue(e.FullPath, renamed: true);

    private void OnError(object sender, ErrorEventArgs e) =>
        Log.Error("Screenshots folder watcher error (some files may be missed)", e.GetException());

    private void Enqueue(string path, bool renamed)
    {
        if (!HasImageExtension(path))
        {
            return;
        }

        // আগে থেকে থাকা file এর নাম বদলালে সেটা নতুন screenshot না
        if (renamed && !IsNewFile(path))
        {
            return;
        }

        lock (_gate)
        {
            if (!_enabled || _queue.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            if (_queue.Count >= MaxQueued)
            {
                _dropped++;
                return;
            }

            _queue.Enqueue(path);
            if (_processing)
            {
                return;
            }

            _processing = true;
        }

        // শুধু কাজ থাকলে একটা thread pool thread; শেষ হলে কিছুই চলে না
        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            string path;
            lock (_gate)
            {
                if (_queue.Count == 0 || _disposed)
                {
                    _processing = false;
                    if (_dropped > 0)
                    {
                        Log.Error($"Screenshots folder: too many files at once; skipped {_dropped} (max {MaxQueued})");
                        _dropped = 0;
                    }

                    return;
                }

                path = _queue.Dequeue();
            }

            try
            {
                await ProcessFileAsync(path, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                // বন্ধ হচ্ছে
            }
            catch (Exception ex)
            {
                Log.Error($"Screenshots folder: {Path.GetFileName(path)}", ex);
            }
        }
    }

    private async Task ProcessFileAsync(string path, CancellationToken cancel)
    {
        await Task.Delay(FirstDelayMs, cancel);

        byte[]? data = null;
        for (int attempt = 0; attempt < OpenRetryCount && data is null; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(OpenRetryDelayMs, cancel);
            }

            if (!File.Exists(path))
            {
                // Temp file ছিল, বা লেখার পরেই সরিয়ে ফেলেছে
                return;
            }

            data = TryRead(path);
        }

        if (data is null)
        {
            Log.Error($"Screenshots folder: {Path.GetFileName(path)} is still being written or too large; skipped");
            return;
        }

        if (!IsEnabled)
        {
            return;
        }

        await _onScreenshot(data, path).WaitAsync(HandlerTimeout, cancel);
    }

    /// <summary>
    /// শুধু পড়ার জন্য খোলে, আর অন্য কাউকে লিখতে দেয় না (FileShare.Read): কেউ এখনো লিখছে হলে খোলা
    /// fail করে, তখন null। File এ কোনো পরিবর্তন হয় না।
    /// </summary>
    private static byte[]? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
            long length = stream.Length;
            if (length <= 0 || length > MaxFileBytes)
            {
                return null;
            }

            var data = new byte[length];
            stream.ReadExactly(data);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool IsNewFile(string path)
    {
        try
        {
            return File.GetCreationTimeUtc(path) >= _startedUtc - CreationSlack;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasImageExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SHGetKnownFolderPath(FOLDERID_Screenshots): OneDrive বা অন্য drive এ সরানো থাকলেও আসল path।
    /// </summary>
    private static string? GetScreenshotsFolder()
    {
        IntPtr pathPtr = IntPtr.Zero;
        try
        {
            var id = FolderIdScreenshots;
            int hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pathPtr);
            if (hr != 0)
            {
                Log.Info($"SHGetKnownFolderPath(Screenshots) failed (0x{hr:X8})");
                return null;
            }

            return Marshal.PtrToStringUni(pathPtr);
        }
        catch (Exception ex)
        {
            Log.Error("SHGetKnownFolderPath(Screenshots)", ex);
            return null;
        }
        finally
        {
            if (pathPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
    }

    private static readonly Guid FolderIdScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
