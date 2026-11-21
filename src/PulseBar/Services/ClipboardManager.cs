using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Captures new clipboard text or images (WM_CLIPBOARDUPDATE) into the history.
/// Messages arrive on a message-only window on the UI thread; reading, image encoding and saving
/// happen on a separate background thread so large images never block the UI.
/// </summary>
public sealed class ClipboardManager : IDisposable
{
    public const int MaxItems = 200;
    public const int MaxTextBytes = 1024 * 1024;          // 1 MB in UTF-8
    public const long MaxImageFileBytes = 20L * 1024 * 1024; // 20 MB saved PNG
    public const int ThumbnailWidth = 240;

    /// <summary>Items older than this are removed automatically; <see cref="Retention"/> can be changed from Settings.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

    private static readonly TimeSpan RetentionCheckInterval = TimeSpan.FromMinutes(10);

    // Raw bitmaps bigger than this are never loaded (a 4K screenshot DIB is ~33 MB, so 20 MB is too small)
    private const long MaxRawImageBytes = 128L * 1024 * 1024;

    private const int OpenRetryCount = 5;
    private const int OpenRetryDelayMs = 60;

    // When many updates arrive together (some apps write several times), only the last one is read
    private const int CoalesceDelayMs = 80;

    // Win+Shift+S puts the same image on the clipboard and also saves it to the Screenshots folder.
    // Images of the same size from both sides within this window count as the same (PNG bytes may differ).
    private const long SameSizeWindowMs = 5_000;

    // How long recent images are kept for pixel-hash duplicate checks
    private const long RecentImageWindowMs = 60_000;
    private const int MaxRecentImages = 16;

    // Clipboard images from these are screenshots (Win+Shift+S, Snipping Tool)
    private static readonly string[] ScreenshotTools = ["ScreenClippingHost.exe", "SnippingTool.exe"];

    private readonly ClipboardStorage _storage;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Dictionary<string, string> _appNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Action> _work = new();
    private Timer? _retentionTimer;
    private IntPtr _ownerHwnd;
    private Dispatcher? _uiDispatcher;

    // Cancels the worker's pending UI-thread work on shutdown, so Dispose (UI thread) and the
    // worker never wait on each other
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _clipboardChanged;
    private List<ClipboardItem> _items = [];
    private HwndSource? _window;
    private Thread? _worker;
    private uint _lastSequence;
    private bool _handledImage;

    // Owner exe at WM_CLIPBOARDUPDATE time; Screen Clipping may exit before we read it
    private volatile string? _notifiedOwnerExe;

    // Recent images for duplicate detection; used only on the worker thread
    private readonly List<RecentImage> _recentImages = [];
    private volatile bool _disposed;

    public ClipboardManager(ClipboardStorage storage)
    {
        _storage = storage;
    }

    private long _retentionTicks = DefaultRetention.Ticks;
    private volatile bool _captureEnabled = true;

    /// <summary>Items older than this are removed. Lowering it removes old items right away. Any thread.</summary>
    public TimeSpan Retention
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _retentionTicks));
        set
        {
            if (value <= TimeSpan.Zero || Interlocked.Exchange(ref _retentionTicks, value.Ticks) == value.Ticks)
            {
                return;
            }

            if (_worker is not null)
            {
                Enqueue(PurgeExpired);
            }
        }
    }

    /// <summary>
    /// When off, nothing new from the clipboard is added to history; existing history stays and copy/delete still work.
    /// </summary>
    public bool IsCaptureEnabled
    {
        get => _captureEnabled;
        set => _captureEnabled = value;
    }

    /// <summary>
    /// Raised when the history changes. Usually from a background thread; for Copy/Delete/Clear, from the calling thread.
    /// </summary>
    public event EventHandler? HistoryChanged;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>A copy of the current history, newest first.</summary>
    public ClipboardItem[] GetItems()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }

    /// <summary>Must be called from the UI thread.</summary>
    public void Start()
    {
        if (_window is not null || _disposed)
        {
            return;
        }

        _uiDispatcher = Dispatcher.CurrentDispatcher;

        // Loading from disk and all captures run in order on the same background thread
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PulseBar clipboard",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();

        try
        {
            var parameters = new HwndSourceParameters("PulseBarClipboardListener")
            {
                ParentWindow = HwndMessage,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };
            _window = new HwndSource(parameters);
            _window.AddHook(WndProc);
            _ownerHwnd = _window.Handle;

            if (!AddClipboardFormatListener(_window.Handle))
            {
                Log.Error($"AddClipboardFormatListener failed (Win32 {Marshal.GetLastPInvokeError()})");
            }
        }
        catch (Exception ex)
        {
            // Without a listener clipboard history won't work, but the app keeps running
            Log.Error("Clipboard listener", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_window is not null)
        {
            RemoveClipboardFormatListener(_window.Handle);
            _window.RemoveHook(WndProc);
            _window.Dispose();
            _window = null;
        }

        _retentionTimer?.Dispose();
        _shutdown.Cancel();
        _signal.Set();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _storage.Dispose();
        _signal.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            if (!_disposed && _captureEnabled)
            {
                _notifiedOwnerExe = OwnerExeNow();
                _clipboardChanged = true;
                _signal.Set();
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private void WorkerLoop()
    {
        var loaded = _storage.Load();
        if (loaded.Count > MaxItems)
        {
            foreach (var extra in loaded.Skip(MaxItems))
            {
                ClipboardStorage.DeleteFiles(extra);
            }

            loaded.RemoveRange(MaxItems, loaded.Count - MaxItems);
        }

        lock (_gate)
        {
            // Anything captured before loading finished stays on top
            _items.AddRange(loaded);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);

        // Whatever is on the clipboard at startup is not a new copy, so it isn't captured
        _lastSequence = GetClipboardSequenceNumber();

        // Once at startup, then every 10 minutes, remove old items
        PurgeExpired();
        _retentionTimer = new Timer(_ => Enqueue(PurgeExpired), null, RetentionCheckInterval, RetentionCheckInterval);

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

            if (_disposed)
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
                    Log.Error("Clipboard work", ex);
                }
            }

            if (_clipboardChanged)
            {
                _clipboardChanged = false;
                Thread.Sleep(CoalesceDelayMs);

                try
                {
                    Capture();
                }
                catch (Exception ex)
                {
                    // Odd data must never crash the app
                    Log.Error("Clipboard capture", ex);
                }
            }

            if (_handledImage)
            {
                _handledImage = false;
                ReleaseImageMemory();
            }
        }
    }

    /// <summary>
    /// Puts the item back on the clipboard and moves it to the top on success. Images get both PNG and DIB.
    /// Preparation (reading the full image from disk, building the DIB) runs on the worker thread; opening and
    /// writing the clipboard runs on the UI thread, whose window becomes the owner. Result: null on success, otherwise the reason.
    /// </summary>
    public Task<string?> CopyToClipboardAsync(string id)
    {
        ClipboardItem? item;
        lock (_gate)
        {
            item = _items.Find(i => i.Id == id);
        }

        if (item is null)
        {
            return Task.FromResult<string?>("Item is no longer in history");
        }

        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Enqueue(() =>
        {
            string? error;
            try
            {
                error = WriteToClipboard(item);
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (error is null)
            {
                // Our own copy is skipped by capture, so it is moved to the top here
                Promote(id);
            }
            else
            {
                Log.Error($"Copy failed for {item.Kind} item {item.Id}: {error}");
            }

            result.TrySetResult(error);
        });

        if (!queued)
        {
            result.TrySetResult("PulseBar is closing");
        }

        return result.Task;
    }

    /// <summary>Moves the item to the top with a new timestamp.</summary>
    private void Promote(string id)
    {
        ClipboardItem[] snapshot;
        lock (_gate)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                return;
            }

            var item = _items[index] with { CreatedAt = DateTimeOffset.Now };
            _items.RemoveAt(index);
            _items.Insert(0, item);
            snapshot = [.. _items];
        }

        _storage.ScheduleSave(snapshot);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the item and its image.</summary>
    public void Delete(string id)
    {
        ClipboardItem removed;
        ClipboardItem[] snapshot;
        lock (_gate)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                return;
            }

            removed = _items[index];
            _items.RemoveAt(index);
            snapshot = [.. _items];
        }

        ClipboardStorage.DeleteFiles(removed);
        _storage.ScheduleSave(snapshot);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes all items and empties the images folder.</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _items.Clear();
        }

        _storage.DeleteAllImages();
        _storage.ScheduleSave([]);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
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
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; the work will not run
            return false;
        }
    }

    /// <summary>Removes items older than <see cref="Retention"/> and their images. Runs on the worker thread.</summary>
    private void PurgeExpired()
    {
        var cutoff = DateTimeOffset.Now - Retention;
        List<ClipboardItem> expired;
        ClipboardItem[] snapshot;
        lock (_gate)
        {
            expired = _items.FindAll(i => i.CreatedAt < cutoff);
            if (expired.Count == 0)
            {
                return;
            }

            _items.RemoveAll(i => i.CreatedAt < cutoff);
            snapshot = [.. _items];
        }

        foreach (var item in expired)
        {
            ClipboardStorage.DeleteFiles(item);
        }

        _storage.ScheduleSave(snapshot);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs on the worker thread. Null on success, otherwise the reason.</summary>
    private string? WriteToClipboard(ClipboardItem item)
    {
        if (item.Kind == ClipboardItemKind.Text)
        {
            if (item.Text is not { } text)
            {
                return "Text item has no text";
            }

            // UTF-16 with a trailing null character
            var bytes = new byte[(text.Length + 1) * sizeof(char)];
            MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
            return SetClipboard([(CfUnicodeText, bytes)]);
        }

        if (item.ImagePath is not { } path)
        {
            return "Image item has no ImagePath";
        }

        if (!File.Exists(path))
        {
            return $"Image file not found: {path}";
        }

        _handledImage = true;
        byte[] png = File.ReadAllBytes(path);
        byte[] dib;
        using (var input = new MemoryStream(png, writable: false))
        {
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            dib = ToDib(decoder.Frames[0]);
        }

        // PNG: Chrome, Discord, Word (with transparency); DIB: Paint and older apps
        return SetClipboard([(PngFormat, png), (CfDib, dib)]);
    }

    /// <summary>Builds a 32bpp bottom-up BITMAPINFOHEADER DIB.</summary>
    private static byte[] ToDib(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        const int headerSize = 40;

        var dib = new byte[headerSize + (long)stride * height];
        var header = dib.AsSpan(0, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height); // positive = bottom row first
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);

        bgra.CopyPixels(dib, stride, headerSize);

        // WPF gives the top row first, so rows are flipped for the DIB
        var row = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            var a = dib.AsSpan(headerSize + top * stride, stride);
            var b = dib.AsSpan(headerSize + bottom * stride, stride);
            a.CopyTo(row);
            b.CopyTo(a);
            row.CopyTo(b);
        }

        return dib;
    }

    /// <summary>
    /// Called from the worker thread. Each attempt runs on the UI thread (the owner window belongs to it
    /// and pumps messages, so other apps' EmptyClipboard isn't blocked). If another app holds the clipboard,
    /// the wait between attempts happens on the worker, not the UI. Null on success, otherwise the reason.
    /// </summary>
    private string? SetClipboard((uint Format, byte[] Data)[] formats)
    {
        if (_uiDispatcher is not { } dispatcher || _ownerHwnd == IntPtr.Zero)
        {
            return "Clipboard window was not created";
        }

        string? error = null;
        for (int attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (_disposed)
            {
                return "PulseBar is closing";
            }

            bool retry = false;
            try
            {
                error = dispatcher.Invoke(() => TrySetClipboard(formats, out retry), DispatcherPriority.Send, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return "PulseBar is closing";
            }

            if (error is null || !retry)
            {
                return error;
            }

            Thread.Sleep(OpenRetryDelayMs * (attempt + 1));
        }

        return $"{error} (after {OpenRetryCount} attempts)";
    }

    /// <summary>One attempt on the UI thread. <paramref name="retry"/> = true if someone else has the clipboard open.</summary>
    private string? TrySetClipboard((uint Format, byte[] Data)[] formats, out bool retry)
    {
        retry = false;
        if (!OpenClipboard(_ownerHwnd))
        {
            int code = Marshal.GetLastPInvokeError();
            retry = true;
            return $"OpenClipboard failed (Win32 {code}), clipboard held by: {DescribeWindowProcess(GetOpenClipboardWindow())}";
        }

        try
        {
            if (!EmptyClipboard())
            {
                return $"EmptyClipboard failed (Win32 {Marshal.GetLastPInvokeError()})";
            }

            foreach (var (format, data) in formats)
            {
                if (format == 0)
                {
                    continue;
                }

                IntPtr memory = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)data.Length);
                if (memory == IntPtr.Zero)
                {
                    return $"GlobalAlloc failed for {data.Length} bytes (Win32 {Marshal.GetLastPInvokeError()})";
                }

                IntPtr ptr = GlobalLock(memory);
                if (ptr == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return "GlobalLock failed";
                }

                Marshal.Copy(data, 0, ptr, data.Length);
                GlobalUnlock(memory);

                // On success the system owns the memory; otherwise we must free it
                if (SetClipboardData(format, memory) == IntPtr.Zero)
                {
                    int code = Marshal.GetLastPInvokeError();
                    GlobalFree(memory);
                    return $"SetClipboardData({format}) failed (Win32 {code})";
                }
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private string DescribeWindowProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            // Windows doesn't say which app opened it with OpenClipboard(NULL)
            return "Unknown app (opened without a window)";
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId ? "PulseBar itself" : ResolveAppName(pid) ?? $"PID {pid}";
    }

    private void Capture()
    {
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == _lastSequence)
        {
            return;
        }

        _lastSequence = sequence;

        var raw = ReadClipboard();
        if (raw is null || _disposed)
        {
            return;
        }

        string? sourceApp = ResolveAppName(raw.OwnerProcessId);

        if (raw.Text is { } text)
        {
            string hash = HashText(text);
            AddOrPromote(ClipboardItemKind.Text, hash, () => new ClipboardItem
            {
                Id = NewId(),
                Kind = ClipboardItemKind.Text,
                Text = text,
                Preview = ClipboardItem.MakePreview(text),
                SourceApp = sourceApp,
                CreatedAt = DateTimeOffset.Now,
                ContentHash = hash,
            });
        }
        else if (raw.ImageData is { } data)
        {
            _handledImage = true;
            string hash = Convert.ToHexString(SHA256.HashData(data));

            // The same image again costs no encoding
            if (TryPromote(ClipboardItemKind.Image, hash))
            {
                return;
            }

            if (Decode(data, raw.ImageIsPng) is not { } frame)
            {
                return;
            }

            // If the owner exited before we read it, use the one from notification time
            string? ownerExe = Path.GetFileName(GetProcessPath(raw.OwnerProcessId)) ?? _notifiedOwnerExe;
            bool isScreenshot = IsScreenshotTool(ownerExe);
            string? pixelHash = isScreenshot ? PixelHash(frame) : null;

            // If the folder file arrived first, don't add the same screenshot twice
            if (isScreenshot && FindRecentDuplicate(frame, pixelHash, fromClipboard: true) is { } existingId)
            {
                Log.Info("Screenshot from clipboard is already in history (from the Screenshots folder)");
                Promote(existingId);
                return;
            }

            var item = SaveImage(frame, raw.ImageIsPng ? data : null, hash, sourceApp, isScreenshot);
            if (item is not null)
            {
                AddOrPromote(ClipboardItemKind.Image, hash, () => item);
                RememberRecent(item.Id, frame, pixelHash, fromClipboard: true);
            }
        }
    }

    /// <summary>
    /// Adds a new Screenshots folder file or our own screenshot to history
    /// (<see cref="ClipboardItem.IsScreenshot"/> = true). If the same image is already there (Win+Shift+S also
    /// puts it on the clipboard), no new item is added. With <paramref name="copyToClipboard"/>, the new item is
    /// also put on the clipboard (PNG + DIB); that is our own write, so the listener doesn't add it again.
    /// Result: true if a new item was added. Never throws.
    /// </summary>
    public Task<bool> AddScreenshotAsync(byte[] data, string? sourceApp, bool copyToClipboard)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Enqueue(() =>
        {
            try
            {
                result.TrySetResult(AddScreenshot(data, sourceApp, copyToClipboard));
            }
            catch (Exception ex)
            {
                Log.Error("Screenshot add", ex);
                result.TrySetResult(false);
            }
        });

        if (!queued)
        {
            result.TrySetResult(false);
        }

        return result.Task;
    }

    /// <summary>Runs on the worker thread.</summary>
    private bool AddScreenshot(byte[] data, string? sourceApp, bool copyToClipboard)
    {
        _handledImage = true;
        string hash = Convert.ToHexString(SHA256.HashData(data));

        if (Decode(data, isPng: null) is not { } frame)
        {
            Log.Error("Screenshot: could not decode the image");
            return false;
        }

        string pixelHash = PixelHash(frame);
        string? existingId;
        lock (_gate)
        {
            existingId = _items.Find(i => i.Kind == ClipboardItemKind.Image && i.ContentHash == hash)?.Id;
        }

        existingId ??= FindRecentDuplicate(frame, pixelHash, fromClipboard: false);
        if (existingId is not null)
        {
            // e.g. Win+Shift+S: the image already came from the clipboard, now just mark it as a screenshot
            Log.Info("Screenshot is already in history; not added again");
            MarkScreenshot(existingId);
            return false;
        }

        var item = SaveImage(frame, IsPng(data) ? data : null, hash, sourceApp, isScreenshot: true);
        if (item is null)
        {
            return false;
        }

        AddOrPromote(ClipboardItemKind.Image, hash, () => item);
        RememberRecent(item.Id, frame, pixelHash, fromClipboard: false);
        Log.Info($"Screenshot added ({item.Preview})");

        if (copyToClipboard && WriteToClipboard(item) is { } error)
        {
            Log.Error($"Screenshot copy to clipboard failed: {error}");
        }

        return true;
    }

    /// <summary>Marks the item as a screenshot (if it isn't already).</summary>
    private void MarkScreenshot(string id)
    {
        ClipboardItem[] snapshot;
        lock (_gate)
        {
            int index = _items.FindIndex(i => i.Id == id);
            if (index < 0 || _items[index].IsScreenshot)
            {
                return;
            }

            _items[index] = _items[index] with { IsScreenshot = true };
            snapshot = [.. _items];
        }

        _storage.ScheduleSave(snapshot);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Screenshot duplicate ----------------

    private sealed record RecentImage(string Id, int Width, int Height, string? PixelHash, bool FromClipboard, long Tick);

    private void RememberRecent(string id, BitmapSource frame, string? pixelHash, bool fromClipboard)
    {
        _recentImages.Insert(0, new RecentImage(id, frame.PixelWidth, frame.PixelHeight, pixelHash, fromClipboard, Environment.TickCount64));
        if (_recentImages.Count > MaxRecentImages)
        {
            _recentImages.RemoveRange(MaxRecentImages, _recentImages.Count - MaxRecentImages);
        }
    }

    /// <summary>
    /// Whether the same image arrived recently: the same pixel hash, or an image of the same size from the
    /// other side (clipboard vs folder) within 5 seconds. Returns the id of an item still in history.
    /// </summary>
    private string? FindRecentDuplicate(BitmapSource frame, string? pixelHash, bool fromClipboard)
    {
        long now = Environment.TickCount64;
        _recentImages.RemoveAll(r => now - r.Tick > RecentImageWindowMs);

        foreach (var recent in _recentImages)
        {
            bool samePixels = pixelHash is not null && recent.PixelHash == pixelHash;
            bool sameSizeOtherSide = recent.FromClipboard != fromClipboard &&
                recent.Width == frame.PixelWidth &&
                recent.Height == frame.PixelHeight &&
                now - recent.Tick <= SameSizeWindowMs;

            if (!samePixels && !sameSizeOtherSide)
            {
                continue;
            }

            lock (_gate)
            {
                if (_items.Exists(i => i.Id == recent.Id))
                {
                    return recent.Id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// SHA-256 of the decoded pixels (without alpha), so the same image matches as PNG, DIB or another encoder.
    /// Reads a few rows at a time instead of the whole image at once.
    /// </summary>
    internal static string PixelHash(BitmapSource frame)
    {
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        const int rowsPerChunk = 16;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, width);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], height);
        sha.AppendData(size);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(stride * Math.Min(rowsPerChunk, height));
        try
        {
            for (int y = 0; y < height; y += rowsPerChunk)
            {
                int rows = Math.Min(rowsPerChunk, height - y);
                int length = rows * stride;
                bgra.CopyPixels(new Int32Rect(0, y, width, rows), buffer, stride, 0);

                // Some encoders keep alpha 0, some 255; alpha means nothing in a screenshot
                for (int i = 3; i < length; i += 4)
                {
                    buffer[i] = 0;
                }

                sha.AppendData(buffer, 0, length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static bool IsScreenshotTool(string? exeName) =>
        exeName is not null && ScreenshotTools.Contains(exeName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Exe name of the current clipboard owner; null if it's us or unknown. UI thread, very cheap.</summary>
    private static string? OwnerExeNow()
    {
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(owner, out uint pid);
        return pid == (uint)Environment.ProcessId ? null : Path.GetFileName(GetProcessPath(pid));
    }

    private static bool IsPng(byte[] data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';

    /// <summary>
    /// Decodes an image. If <paramref name="isPng"/> is null the format is detected (PNG, JPG and so on).
    /// Null if corrupt or unusually large.
    /// </summary>
    internal static BitmapSource? Decode(byte[] data, bool? isPng)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            BitmapDecoder decoder = isPng switch
            {
                true => new PngBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
                false => new BmpBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
                null => BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
            };

            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                (long)frame.PixelWidth * frame.PixelHeight * 4 > MaxRawImageBytes)
            {
                return null;
            }

            return frame;
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard image decode", ex);
            return null;
        }
    }

    /// <param name="png">The bytes if the image is already PNG; then they are written as-is without re-encoding.</param>
    private ClipboardItem? SaveImage(BitmapSource frame, byte[]? png, string hash, string? sourceApp, bool isScreenshot)
    {
        string id = NewId();
        string imagePath = _storage.ImagePathFor(id);
        string thumbPath = _storage.ThumbnailPathFor(id);

        try
        {
            Directory.CreateDirectory(_storage.ImagesDirectory);

            if (png is not null)
            {
                // Already PNG: written as-is without re-encoding (keeps alpha intact)
                File.WriteAllBytes(imagePath, png);
            }
            else
            {
                WritePng(frame, imagePath);
            }

            if (new FileInfo(imagePath).Length > MaxImageFileBytes)
            {
                ClipboardStorage.TryDelete(imagePath);
                return null;
            }

            BitmapSource thumb = frame;
            if (frame.PixelWidth > ThumbnailWidth)
            {
                double scale = (double)ThumbnailWidth / frame.PixelWidth;
                thumb = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }

            WritePng(thumb, thumbPath);

            return new ClipboardItem
            {
                Id = id,
                Kind = ClipboardItemKind.Image,
                ImagePath = imagePath,
                ThumbnailPath = thumbPath,
                Preview = $"{frame.PixelWidth} × {frame.PixelHeight}",
                SourceApp = sourceApp,
                CreatedAt = DateTimeOffset.Now,
                ContentHash = hash,
                IsScreenshot = isScreenshot,
            };
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard image save", ex);
            ClipboardStorage.TryDelete(imagePath);
            ClipboardStorage.TryDelete(thumbPath);
            return null;
        }
    }

    /// <summary>
    /// The GC releases raw bytes of 4K images (LOH) and WIC's native buffers very late,
    /// so after image work a full collection returns the memory.
    /// </summary>
    private static void ReleaseImageMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void WritePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private bool TryPromote(ClipboardItemKind kind, string hash) => AddOrPromote(kind, hash, create: null);

    /// <summary>
    /// If an item with the same hash exists, moves it to the top with a new timestamp (returns true).
    /// Otherwise, if <paramref name="create"/> is given, adds a new item.
    /// </summary>
    private bool AddOrPromote(ClipboardItemKind kind, string hash, Func<ClipboardItem>? create)
    {
        List<ClipboardItem>? removed = null;
        ClipboardItem[] snapshot;
        bool promoted;

        lock (_gate)
        {
            int index = _items.FindIndex(i => i.Kind == kind && i.ContentHash == hash);
            if (index >= 0)
            {
                var existing = _items[index];
                _items.RemoveAt(index);
                _items.Insert(0, existing with { CreatedAt = DateTimeOffset.Now });
                promoted = true;
            }
            else if (create is not null)
            {
                _items.Insert(0, create());
                promoted = false;

                if (_items.Count > MaxItems)
                {
                    removed = _items.GetRange(MaxItems, _items.Count - MaxItems);
                    _items.RemoveRange(MaxItems, _items.Count - MaxItems);
                }
            }
            else
            {
                return false;
            }

            snapshot = [.. _items];
        }

        if (removed is not null)
        {
            foreach (var old in removed)
            {
                ClipboardStorage.DeleteFiles(old);
            }
        }

        _storage.ScheduleSave(snapshot);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return promoted;
    }

    // ---------------- Reading the clipboard ----------------

    private sealed record RawClipboard(string? Text, byte[]? ImageData, bool ImageIsPng, uint OwnerProcessId);

    private static RawClipboard? ReadClipboard()
    {
        // If another app holds the clipboard (CLIPBRD_E_CANT_OPEN), retry a few times shortly after
        bool opened = false;
        for (int attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                opened = true;
                break;
            }

            Thread.Sleep(OpenRetryDelayMs * (attempt + 1));
        }

        if (!opened)
        {
            return null;
        }

        try
        {
            // Password managers and the like asked not to be kept in history
            if (IsClipboardFormatAvailable(ExcludeFromMonitorFormat))
            {
                return null;
            }

            if (IsClipboardFormatAvailable(CanIncludeInHistoryFormat) &&
                ReadDword(GetClipboardData(CanIncludeInHistoryFormat)) == 0)
            {
                return null;
            }

            uint ownerPid = 0;
            IntPtr owner = GetClipboardOwner();
            if (owner != IntPtr.Zero)
            {
                GetWindowThreadProcessId(owner, out ownerPid);
            }

            // No new item when PulseBar itself copied
            if (ownerPid == (uint)Environment.ProcessId)
            {
                return null;
            }

            if (IsClipboardFormatAvailable(CfUnicodeText))
            {
                var text = ReadText(GetClipboardData(CfUnicodeText), out bool tooLarge);
                if (tooLarge)
                {
                    // For huge text (like a big Excel range), its image isn't taken either
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return new RawClipboard(text, null, false, ownerPid);
                }
            }

            if (PngFormat != 0 && IsClipboardFormatAvailable(PngFormat) &&
                ReadBytes(GetClipboardData(PngFormat), MaxImageFileBytes, headerSize: 0) is { } png)
            {
                return new RawClipboard(null, png, true, ownerPid);
            }

            if (IsClipboardFormatAvailable(CfDib) &&
                ReadBytes(GetClipboardData(CfDib), MaxRawImageBytes, BmpFileHeaderSize) is { } bmp &&
                WriteBmpFileHeader(bmp))
            {
                return new RawClipboard(null, bmp, false, ownerPid);
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static unsafe string? ReadText(IntPtr handle, out bool tooLarge)
    {
        tooLarge = false;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        long size = (long)GlobalSize(handle);

        // More than 2 MB in UTF-16 means over a million characters, definitely over 1 MB in UTF-8
        if (size > 2L * MaxTextBytes + 16)
        {
            tooLarge = true;
            return null;
        }

        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var span = new ReadOnlySpan<char>((void*)ptr, (int)(size / sizeof(char)));
            int end = span.IndexOf('\0');
            if (end >= 0)
            {
                span = span[..end];
            }

            if (Encoding.UTF8.GetByteCount(span) > MaxTextBytes)
            {
                tooLarge = true;
                return null;
            }

            return span.ToString();
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Copies bytes from clipboard memory, leaving <paramref name="headerSize"/> bytes empty at the start.</summary>
    private static byte[]? ReadBytes(IntPtr handle, long maxBytes, int headerSize)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        long size = (long)GlobalSize(handle);
        if (size <= 0 || size > maxBytes)
        {
            return null;
        }

        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new byte[headerSize + size];
            Marshal.Copy(ptr, buffer, headerSize, (int)size);
            return buffer;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static int? ReadDword(IntPtr handle)
    {
        if (handle == IntPtr.Zero || (long)GlobalSize(handle) < sizeof(int))
        {
            return null;
        }

        IntPtr ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.ReadInt32(ptr);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// Prepends a BITMAPFILEHEADER to CF_DIB so WPF's BMP decoder can read it.
    /// False if the header looks odd.
    /// </summary>
    private static bool WriteBmpFileHeader(byte[] buffer)
    {
        var dib = buffer.AsSpan(BmpFileHeaderSize);
        if (dib.Length < 40)
        {
            return false;
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);

        if (headerSize < 40 || headerSize > dib.Length)
        {
            return false;
        }

        // After a BITMAPINFOHEADER separate color masks follow (in V4/V5 headers the masks are inside the header)
        long masks = headerSize == 40
            ? compression switch { BiBitfields => 12, BiAlphaBitfields => 16, _ => 0 }
            : 0;
        long colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1L << bitCount : 0;
        long pixelOffset = BmpFileHeaderSize + headerSize + masks + colors * 4;

        if (pixelOffset >= buffer.Length)
        {
            return false;
        }

        var header = buffer.AsSpan(0, BmpFileHeaderSize);
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)buffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], (uint)pixelOffset);
        return true;
    }

    private string? ResolveAppName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        string? path = GetProcessPath(processId);
        if (path is null)
        {
            return null;
        }

        if (_appNameCache.TryGetValue(path, out string? cached))
        {
            return cached;
        }

        string name;
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            name = string.IsNullOrEmpty(description) ? Path.GetFileNameWithoutExtension(path) : description;
        }
        catch
        {
            name = Path.GetFileNameWithoutExtension(path);
        }

        _appNameCache[path] = name;
        return name;
    }

    private static string? GetProcessPath(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        // Limited query usually works on other users'/elevated processes without admin
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            int length = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref length) ? new string(buffer, 0, length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())));

    private static string NewId() => Guid.NewGuid().ToString("N");

    // ---------------- Win32 ----------------

    private const int WmClipboardUpdate = 0x031D;
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint BiBitfields = 3;
    private const uint BiAlphaBitfields = 6;
    private const int BmpFileHeaderSize = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GmemMoveable = 0x0002;
    private static readonly IntPtr HwndMessage = new(-3);

    private static readonly uint ExcludeFromMonitorFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint PngFormat = RegisterClipboardFormat("PNG");

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hwndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
