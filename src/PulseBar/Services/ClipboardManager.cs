using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Clipboard এ নতুন কিছু এলে (WM_CLIPBOARDUPDATE) text বা ছবি ধরে history তে রাখে।
/// Message শোনা হয় UI thread এর একটা message-only window এ; পড়া, ছবি encode আর save হয়
/// একটা আলাদা background thread এ, যাতে বড় ছবিতেও UI না আটকায়।
/// </summary>
public sealed class ClipboardManager : IDisposable
{
    public const int MaxItems = 200;
    public const int MaxTextBytes = 1024 * 1024;          // UTF-8 এ ১ MB
    public const long MaxImageFileBytes = 20L * 1024 * 1024; // save হওয়া PNG ২০ MB
    public const int ThumbnailWidth = 240;

    /// <summary>এর চেয়ে পুরনো item নিজে থেকে মুছে যায়। দিন ১২ এ Settings থেকে <see cref="Retention"/> বদলানো যাবে।</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

    private static readonly TimeSpan RetentionCheckInterval = TimeSpan.FromMinutes(10);

    // এর চেয়ে বড় raw bitmap memory তেই আনা হবে না (৪K screenshot এর DIB ~৩৩ MB, তাই ২০ MB এ আটকানো যায় না)
    private const long MaxRawImageBytes = 128L * 1024 * 1024;

    private const int OpenRetryCount = 5;
    private const int OpenRetryDelayMs = 60;

    // একসাথে অনেকগুলো update এলে (কিছু app কয়েকবার লেখে) শেষেরটাই পড়া হয়
    private const int CoalesceDelayMs = 80;

    private readonly ClipboardStorage _storage;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Dictionary<string, string> _appNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Action> _work = new();
    private Timer? _retentionTimer;
    private IntPtr _ownerHwnd;
    private Dispatcher? _uiDispatcher;
    private volatile bool _clipboardChanged;
    private List<ClipboardItem> _items = [];
    private HwndSource? _window;
    private Thread? _worker;
    private uint _lastSequence;
    private bool _handledImage;
    private volatile bool _disposed;

    public ClipboardManager(ClipboardStorage storage)
    {
        _storage = storage;
    }

    /// <summary>এর চেয়ে পুরনো item মুছে যায়।</summary>
    public TimeSpan Retention { get; set; } = DefaultRetention;

    /// <summary>
    /// History বদলালে। সাধারণত background thread থেকে আসে; Copy/Delete/Clear এ যে thread ডাকে সেখান থেকে।
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

    /// <summary>এই মুহূর্তের history এর কপি, নতুনটা আগে।</summary>
    public ClipboardItem[] GetItems()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }

    /// <summary>UI thread থেকে ডাকতে হবে।</summary>
    public void Start()
    {
        if (_window is not null || _disposed)
        {
            return;
        }

        _uiDispatcher = Dispatcher.CurrentDispatcher;

        // Disk থেকে load আর সব capture একই background thread এ, ক্রমানুসারে
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
            // Listener না পেলে clipboard history চলবে না, তবে app চলবে
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
        _signal.Set();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _storage.Dispose();
        _signal.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            if (!_disposed)
            {
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
            // Load এর আগেই কিছু ধরা পড়লে সেগুলো উপরে থাকবে
            _items.AddRange(loaded);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);

        // শুরুতে clipboard এ যা আছে সেটা নতুন copy না, তাই ধরা হবে না
        _lastSequence = GetClipboardSequenceNumber();

        // শুরুতে একবার, তারপর প্রতি ১০ মিনিটে পুরনো item মোছা
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
                    // অদ্ভুত data তে কোনো অবস্থাতেই app crash করবে না
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
    /// Item টা আবার clipboard এ দেয়; সফল হলে সবার উপরে তোলে। ছবির ক্ষেত্রে PNG আর DIB দুটো format দেয়।
    /// তৈরির কাজ (disk থেকে পুরো ছবি পড়া, DIB বানানো) worker thread এ; clipboard খোলা আর লেখা
    /// UI thread এ, যে thread এর window clipboard এর owner হবে। ফল: সফল হলে null, না হলে কারণ।
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
            return Task.FromResult<string?>("Item আর history তে নেই");
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
                // নিজের copy capture এ বাদ পড়ে, তাই উপরে তোলা এখানেই
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
            result.TrySetResult("App বন্ধ হচ্ছে");
        }

        return result.Task;
    }

    /// <summary>Item কে নতুন সময় দিয়ে সবার উপরে তোলে।</summary>
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

    /// <summary>Item আর তার ছবি মুছে দেয়।</summary>
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

    /// <summary>সব item মুছে images folder খালি করে।</summary>
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
            // বন্ধ হওয়ার মাঝে; কাজটা আর হবে না
            return false;
        }
    }

    /// <summary><see cref="Retention"/> এর চেয়ে পুরনো item আর তাদের ছবি মোছে। Worker thread এ চলে।</summary>
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

    /// <summary>Worker thread এ চলে। সফল হলে null, না হলে কারণ।</summary>
    private string? WriteToClipboard(ClipboardItem item)
    {
        if (item.Kind == ClipboardItemKind.Text)
        {
            if (item.Text is not { } text)
            {
                return "Text item এ লেখা নেই";
            }

            // UTF-16, শেষে null অক্ষর
            var bytes = new byte[(text.Length + 1) * sizeof(char)];
            MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
            return SetClipboard([(CfUnicodeText, bytes)]);
        }

        if (item.ImagePath is not { } path)
        {
            return "Image item এ ImagePath নেই";
        }

        if (!File.Exists(path))
        {
            return $"ছবির file পাওয়া যায়নি: {path}";
        }

        _handledImage = true;
        byte[] png = File.ReadAllBytes(path);
        byte[] dib;
        using (var input = new MemoryStream(png, writable: false))
        {
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            dib = ToDib(decoder.Frames[0]);
        }

        // PNG: Chrome, Discord, Word (transparency সহ); DIB: Paint আর পুরনো app
        return SetClipboard([(PngFormat, png), (CfDib, dib)]);
    }

    /// <summary>32bpp bottom-up BITMAPINFOHEADER DIB বানায়।</summary>
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
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height); // ধনাত্মক = নিচের সারি আগে
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);

        bgra.CopyPixels(dib, stride, headerSize);

        // WPF উপরের সারি আগে দেয়, DIB এর জন্য সারিগুলো উল্টানো হয়
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
    /// Worker thread থেকে ডাকা হয়। প্রতিটা চেষ্টা UI thread এ চলে (owner window ওই thread এর,
    /// আর সে message pump করে, তাই অন্য app এর EmptyClipboard আটকায় না)। অন্য app clipboard খুলে
    /// রাখলে চেষ্টার মাঝে worker এ অপেক্ষা হয়, UI আটকায় না। সফল হলে null, না হলে কারণ।
    /// </summary>
    private string? SetClipboard((uint Format, byte[] Data)[] formats)
    {
        if (_uiDispatcher is not { } dispatcher || _ownerHwnd == IntPtr.Zero)
        {
            return "Clipboard window তৈরি হয়নি";
        }

        string? error = null;
        for (int attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (_disposed)
            {
                return "App বন্ধ হচ্ছে";
            }

            bool retry = false;
            error = dispatcher.Invoke(() => TrySetClipboard(formats, out retry));
            if (error is null || !retry)
            {
                return error;
            }

            Thread.Sleep(OpenRetryDelayMs * (attempt + 1));
        }

        return $"{error} ({OpenRetryCount} বার চেষ্টার পরে)";
    }

    /// <summary>UI thread এ একবার চেষ্টা। Clipboard অন্য কেউ খুলে রাখলে <paramref name="retry"/> = true।</summary>
    private string? TrySetClipboard((uint Format, byte[] Data)[] formats, out bool retry)
    {
        retry = false;
        if (!OpenClipboard(_ownerHwnd))
        {
            int code = Marshal.GetLastPInvokeError();
            retry = true;
            return $"OpenClipboard failed (Win32 {code}), clipboard খুলে রেখেছে: {DescribeWindowProcess(GetOpenClipboardWindow())}";
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

                // সফল হলে memory এর মালিক system; না হলে নিজেদেরই ছাড়তে হবে
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
            // OpenClipboard(NULL) দিয়ে খুললে কোন app সেটা Windows জানায় না
            return "অজানা app (window ছাড়া খুলেছে)";
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId ? "PulseBar নিজে" : ResolveAppName(pid) ?? $"PID {pid}";
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

            // একই ছবি আবার হলে encode এর খরচই লাগবে না
            if (TryPromote(ClipboardItemKind.Image, hash))
            {
                return;
            }

            var item = SaveImage(data, raw.ImageIsPng, hash, sourceApp);
            if (item is not null)
            {
                AddOrPromote(ClipboardItemKind.Image, hash, () => item);
            }
        }
    }

    private ClipboardItem? SaveImage(byte[] data, bool isPng, string hash, string? sourceApp)
    {
        string id = NewId();
        string imagePath = _storage.ImagePathFor(id);
        string thumbPath = _storage.ThumbnailPathFor(id);

        try
        {
            Directory.CreateDirectory(_storage.ImagesDirectory);

            using var input = new MemoryStream(data, writable: false);
            BitmapDecoder decoder = isPng
                ? new PngBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                : new BmpBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            {
                return null;
            }

            if (isPng)
            {
                // আগে থেকেই PNG, আবার encode না করে হুবহু লেখা হয় (alpha ঠিক থাকে)
                File.WriteAllBytes(imagePath, data);
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
    /// ৪K ছবির raw bytes (LOH) আর WIC এর native buffer GC নিজে থেকে অনেক দেরিতে ছাড়ে,
    /// তাই ছবির কাজ শেষে একবার পুরো collect করে memory ফেরত দেওয়া হয়।
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
    /// একই hash এর item থাকলে সেটাকে নতুন সময় দিয়ে সবার উপরে তোলে (true ফেরত দেয়)।
    /// না থাকলে আর <paramref name="create"/> দেওয়া থাকলে নতুন item যোগ করে।
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

    // ---------------- Clipboard পড়া ----------------

    private sealed record RawClipboard(string? Text, byte[]? ImageData, bool ImageIsPng, uint OwnerProcessId);

    private static RawClipboard? ReadClipboard()
    {
        // অন্য app clipboard খুলে রাখলে (CLIPBRD_E_CANT_OPEN এর অবস্থা) একটু পরপর কয়েকবার চেষ্টা
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
            // Password manager ইত্যাদি history তে রাখতে মানা করেছে
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

            // PulseBar নিজে copy করলে নতুন item হবে না
            if (ownerPid == (uint)Environment.ProcessId)
            {
                return null;
            }

            if (IsClipboardFormatAvailable(CfUnicodeText))
            {
                var text = ReadText(GetClipboardData(CfUnicodeText), out bool tooLarge);
                if (tooLarge)
                {
                    // বিশাল text (যেমন বড় Excel range) এর বদলে তার ছবিও নেওয়া হবে না
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

        // UTF-16 এ ২ MB এর বেশি মানে ১০ লাখের বেশি অক্ষর, UTF-8 এ নিশ্চিতভাবে ১ MB পার
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

    /// <summary>Clipboard এর memory থেকে bytes কপি করে; শুরুতে <paramref name="headerSize"/> bytes খালি রাখে।</summary>
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
    /// CF_DIB এর আগে BITMAPFILEHEADER বসায়, যাতে WPF এর BMP decoder সেটা পড়তে পারে।
    /// Header অদ্ভুত হলে false।
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

        // BITMAPINFOHEADER এর পরে আলাদা color mask থাকে (V4/V5 header এ mask header এর ভেতরেই)
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
        // Limited query admin ছাড়াও অন্য user/elevated process এ সাধারণত কাজ করে
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
