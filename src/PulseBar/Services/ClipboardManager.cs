using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseBar.Models;

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

    /// <summary>History বদলালে; background thread থেকে আসে।</summary>
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

            if (!AddClipboardFormatListener(_window.Handle))
            {
                Debug.WriteLine($"AddClipboardFormatListener failed: {Marshal.GetLastPInvokeError()}");
            }
        }
        catch (Exception ex)
        {
            // Listener না পেলে clipboard history চলবে না, তবে app চলবে
            Debug.WriteLine($"Clipboard listener: {ex.Message}");
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

            Thread.Sleep(CoalesceDelayMs);

            try
            {
                Capture();
            }
            catch (Exception ex)
            {
                // অদ্ভুত data তে কোনো অবস্থাতেই app crash করবে না
                Debug.WriteLine($"Clipboard capture: {ex}");
            }

            if (_handledImage)
            {
                _handledImage = false;
                ReleaseImageMemory();
            }
        }
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
            Debug.WriteLine($"Clipboard image: {ex.Message}");
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

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
