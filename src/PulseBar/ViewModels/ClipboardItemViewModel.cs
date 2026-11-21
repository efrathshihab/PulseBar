using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;

namespace PulseBar.ViewModels;

/// <summary>
/// One card of the clipboard list. The image isn't kept here; once the card is visible
/// <see cref="Utilities.ThumbnailConverter"/> reads a small version from disk.
/// </summary>
public sealed partial class ClipboardItemViewModel : ObservableObject
{
    public ClipboardItemViewModel(ClipboardItem item, DateTimeOffset now)
    {
        Item = item;
        DisplayText = MakeDisplayText(item);
        RelativeTime = FormatRelative(item.CreatedAt, now);
    }

    public ClipboardItem Item { get; private set; }

    public string Id => Item.Id;

    public bool IsText => Item.Kind == ClipboardItemKind.Text;

    public bool IsImage => Item.Kind == ClipboardItemKind.Image;

    /// <summary>Camera badge in the card's corner.</summary>
    public bool IsScreenshot => Item.IsScreenshot;

    /// <summary>Text preview, or the image size such as "3840 × 2160".</summary>
    public string DisplayText { get; }

    public string? ThumbnailPath => Item.ThumbnailPath;

    public string AppName => string.IsNullOrEmpty(Item.SourceApp) ? "Unknown app" : Item.SourceApp;

    [ObservableProperty]
    public partial string RelativeTime { get; set; }

    /// <summary>To show "Copied" for 1 second after copying.</summary>
    [ObservableProperty]
    public partial bool IsCopied { get; set; }

    /// <summary>To show "Copy failed" for 2 seconds when copying fails; the reason is in the log.</summary>
    [ObservableProperty]
    public partial bool IsCopyFailed { get; set; }

    /// <summary>Sets the item with a new CreatedAt after moving it to the top (or when it is later marked as a screenshot).</summary>
    public void Update(ClipboardItem item, DateTimeOffset now)
    {
        bool screenshotChanged = Item.IsScreenshot != item.IsScreenshot;
        Item = item;
        RefreshTime(now);

        if (screenshotChanged)
        {
            OnPropertyChanged(nameof(IsScreenshot));
        }
    }

    public void RefreshTime(DateTimeOffset now) => RelativeTime = FormatRelative(Item.CreatedAt, now);

    public bool Matches(string query) =>
        IsText && Item.Text is { } text && text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string MakeDisplayText(ClipboardItem item)
    {
        if (item.Kind == ClipboardItemKind.Image)
        {
            return item.Preview;
        }

        string preview = item.Preview.Replace("\r\n", "\n").Replace('\t', ' ').Trim();
        bool cut = item.Text is { } full && full.Length > item.Preview.Length;
        return cut ? preview + "…" : preview;
    }

    public static string FormatRelative(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        return $"{(int)age.TotalDays} d ago";
    }
}
