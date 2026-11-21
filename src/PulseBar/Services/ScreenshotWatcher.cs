using System.IO;
using System.Runtime.InteropServices;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// When a new .png/.jpg appears in Windows' Screenshots folder (Win+PrtSc, Snipping Tool auto-save),
/// reads its bytes and passes them to <c>onScreenshot</c>. Only new files: files from before the app started are ignored.
/// <para>
/// ⚠️ No file in the user's Screenshots folder is ever deleted, moved or changed; it is only read.
/// History holds PulseBar's own copy.
/// </para>
/// Files are queued and read one at a time; if many arrive at once, those beyond <see cref="MaxQueued"/>
/// are dropped. When idle, no thread or timer runs.
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    public const int MaxQueued = 20;

    // Files bigger than this aren't screenshots and aren't read
    private const long MaxFileBytes = 64L * 1024 * 1024;

    // Waiting for the write to finish: a short pause first, then this many open attempts
    private const int FirstDelayMs = 150;
    private const int OpenRetryCount = 10;
    private const int OpenRetryDelayMs = 200;

    // If one file's work (ClipboardManager's decode + save) takes longer than this, move to the next
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(15);

    // A renamed file created more than this long before app start is an old file
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
    /// The file's bytes and path. Called one at a time (on a background thread); the next file starts when
    /// the returned Task completes.
    /// </param>
    /// <param name="folder">Another folder for tests; null for Windows' Screenshots folder.</param>
    public ScreenshotWatcher(Func<byte[], string, Task> onScreenshot, string? folder = null)
    {
        _onScreenshot = onScreenshot;
        Folder = folder ?? GetScreenshotsFolder();
    }

    /// <summary>The folder being watched; null if not found.</summary>
    public string? Folder { get; }

    /// <summary>Turns folder watching on/off. If the folder doesn't exist, turning it on does nothing (it's logged).</summary>
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

    /// <summary>Called inside _gate.</summary>
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

    /// <summary>Called inside _gate.</summary>
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

    // Some tools write a temp name first, then rename to .png
    private void OnRenamed(object sender, RenamedEventArgs e) => Enqueue(e.FullPath, renamed: true);

    private void OnError(object sender, ErrorEventArgs e) =>
        Log.Error("Screenshots folder watcher error (some files may be missed)", e.GetException());

    private void Enqueue(string path, bool renamed)
    {
        if (!HasImageExtension(path))
        {
            return;
        }

        // Renaming a file that already existed isn't a new screenshot
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

        // One thread pool thread only while there's work; nothing runs when done
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
                // Shutting down
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
                // It was a temp file, or was moved right after writing
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
    /// Opens for reading only and doesn't let anyone else write (FileShare.Read): if someone is still writing,
    /// opening fails and returns null. The file is never changed.
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
    /// SHGetKnownFolderPath(FOLDERID_Screenshots): the real path even if moved to OneDrive or another drive.
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
