using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulseBar.Models;

namespace PulseBar.Services;

/// <summary>
/// Keeps clipboard history on disk: %LocalAppData%\PulseBar\clipboard\history.json and images\.
/// Saves are debounced and run on a background thread; a corrupt file starts with an empty history.
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
    /// Reads history (newest first). Items whose image is missing are dropped, and images not belonging to any item are deleted.
    /// Never throws.
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
                    // Corrupt file: move it aside for later inspection and start empty
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
            // Disk/permission problem: run with an empty history
        }

        return items;
    }

    /// <summary>Saves in the background a little later; calls in between only write the latest one.</summary>
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

    /// <summary>Deletes every file in the images folder (Clear all).</summary>
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
            // Anything left over is deleted as an orphan at next startup
        }
    }

    /// <summary>Deletes the item's image and thumbnail.</summary>
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
            // Deleted as an orphan at next startup
        }
    }

    /// <summary>Writes any pending save now and stops the timer.</summary>
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

        // Never two writes at once (timer and Dispose)
        lock (_writeGate)
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);

                // Write to a temp file first, then replace, so stopping halfway doesn't corrupt the file
                string temp = HistoryPath + ".tmp";
                using (var stream = File.Create(temp))
                {
                    JsonSerializer.Serialize(stream, items, ClipboardJsonContext.Default.ClipboardItemArray);
                }

                File.Move(temp, HistoryPath, overwrite: true);
            }
            catch
            {
                // Disk full or locked: retried on the next change
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

    /// <summary>Only files in our own images folder; outside paths in history.json are not accepted.</summary>
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
