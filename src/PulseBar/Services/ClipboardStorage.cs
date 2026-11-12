using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// Clipboard history disk এ রাখে: %LocalAppData%\PulseBar\clipboard\history.json আর images\।
/// Save debounce করা হয় আর background thread এ হয়; file নষ্ট হলে খালি history দিয়ে শুরু।
/// </summary>
public sealed class ClipboardStorage : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly Lock _saveGate = new();
    private readonly Lock _writeGate = new();
    private readonly Timer _saveTimer;
    private ClipboardItem[]? _pending;
    private bool _disposed;

    public ClipboardStorage()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseBar",
            "clipboard");
        ImagesDirectory = Path.Combine(RootDirectory, "images");
        HistoryPath = Path.Combine(RootDirectory, "history.json");
        _saveTimer = new Timer(_ => SavePending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string RootDirectory { get; }

    public string ImagesDirectory { get; }

    public string HistoryPath { get; }

    public string ImagePathFor(string id) => Path.Combine(ImagesDirectory, id + ".png");

    public string ThumbnailPathFor(string id) => Path.Combine(ImagesDirectory, id + "_thumb.png");

    /// <summary>
    /// History পড়ে (নতুনটা আগে)। ছবি হারানো item বাদ যায়, আর কোনো item এর না এমন ছবি মুছে যায়।
    /// কোনো অবস্থাতেই exception ছোড়ে না।
    /// </summary>
    public List<ClipboardItem> Load()
    {
        var items = new List<ClipboardItem>();
        try
        {
            Directory.CreateDirectory(ImagesDirectory);

            if (File.Exists(HistoryPath))
            {
                try
                {
                    using var stream = File.OpenRead(HistoryPath);
                    var loaded = JsonSerializer.Deserialize(stream, ClipboardJsonContext.Default.ListClipboardItem);
                    if (loaded is not null)
                    {
                        items = loaded;
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    // নষ্ট file: পরে দেখার জন্য সরিয়ে রেখে খালি history
                    TryMove(HistoryPath, HistoryPath + ".corrupt");
                    items = [];
                }
            }

            items.RemoveAll(item => !IsValid(item));
            items.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            DeleteOrphanImages(items);
        }
        catch
        {
            // Disk/permission সমস্যা: খালি history দিয়ে চলবে
        }

        return items;
    }

    /// <summary>কিছুক্ষণ পরে background এ save হবে; এর মধ্যে আবার ডাকলে শুধু সর্বশেষটা লেখা হয়।</summary>
    public void ScheduleSave(ClipboardItem[] items)
    {
        lock (_saveGate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = items;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>images folder এর সব file মুছে দেয় (Clear all)।</summary>
    public void DeleteAllImages()
    {
        try
        {
            if (!Directory.Exists(ImagesDirectory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(ImagesDirectory))
            {
                TryDelete(file);
            }
        }
        catch
        {
            // বাকি থাকলে পরের বার শুরুতে orphan হিসেবে মুছে যাবে
        }
    }

    /// <summary>Item এর ছবি আর thumbnail মুছে দেয়।</summary>
    public static void DeleteFiles(ClipboardItem item)
    {
        TryDelete(item.ImagePath);
        TryDelete(item.ThumbnailPath);
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // পরের বার শুরুতে orphan হিসেবে মুছে যাবে
        }
    }

    /// <summary>বাকি থাকা save এখনই লিখে timer বন্ধ করে।</summary>
    public void Dispose()
    {
        lock (_saveGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveTimer.Dispose();
        }

        SavePending();
    }

    private void SavePending()
    {
        ClipboardItem[]? items;
        lock (_saveGate)
        {
            items = _pending;
            _pending = null;
        }

        if (items is null)
        {
            return;
        }

        // একসাথে দুটো write যেন না হয় (timer আর Dispose)
        lock (_writeGate)
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);

                // আগে temp file এ লিখে তারপর replace, যাতে মাঝপথে বন্ধ হলে file নষ্ট না হয়
                string temp = HistoryPath + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, items, ClipboardJsonContext.Default.ClipboardItemArray);
                }

                File.Move(temp, HistoryPath, overwrite: true);
            }
            catch
            {
                // Disk ভর্তি বা lock: পরের পরিবর্তনে আবার চেষ্টা হবে
            }
        }
    }

    private bool IsValid(ClipboardItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Id) || string.IsNullOrEmpty(item.ContentHash))
        {
            return false;
        }

        return item.Kind switch
        {
            ClipboardItemKind.Text => item.Text is not null,
            ClipboardItemKind.Image => IsOurImage(item.ImagePath) && File.Exists(item.ImagePath),
            _ => false,
        };
    }

    /// <summary>শুধু নিজেদের images folder এর file; history.json এ বাইরের path থাকলে মানা হবে না।</summary>
    private bool IsOurImage(string? path) =>
        !string.IsNullOrEmpty(path) &&
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), ImagesDirectory, StringComparison.OrdinalIgnoreCase);

    private void DeleteOrphanImages(List<ClipboardItem> items)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.ImagePath is { } image)
            {
                keep.Add(Path.GetFullPath(image));
            }

            if (item.ThumbnailPath is { } thumb)
            {
                keep.Add(Path.GetFullPath(thumb));
            }
        }

        foreach (string file in Directory.EnumerateFiles(ImagesDirectory))
        {
            if (!keep.Contains(Path.GetFullPath(file)))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch
        {
            TryDelete(from);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ClipboardItem>))]
[JsonSerializable(typeof(ClipboardItem[]))]
internal sealed partial class ClipboardJsonContext : JsonSerializerContext;
