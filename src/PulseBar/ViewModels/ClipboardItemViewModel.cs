using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;

namespace PulseBar.ViewModels;

/// <summary>
/// Clipboard list এর একটা card। ছবি এখানে রাখা হয় না; thumbnail টা card দেখা গেলেই
/// <see cref="Utilities.ThumbnailConverter"/> disk থেকে ছোট করে পড়ে।
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

    /// <summary>Text এর preview, অথবা ছবির মাপ যেমন "3840 × 2160"।</summary>
    public string DisplayText { get; }

    public string? ThumbnailPath => Item.ThumbnailPath;

    public string AppName => string.IsNullOrEmpty(Item.SourceApp) ? "Unknown app" : Item.SourceApp;

    [ObservableProperty]
    public partial string RelativeTime { get; set; }

    /// <summary>Copy করার পর ১ সেকেন্ড "Copied" দেখানোর জন্য।</summary>
    [ObservableProperty]
    public partial bool IsCopied { get; set; }

    /// <summary>Copy না হলে ২ সেকেন্ড "Copy failed" দেখানোর জন্য; কারণ log এ থাকে।</summary>
    [ObservableProperty]
    public partial bool IsCopyFailed { get; set; }

    /// <summary>উপরে তোলার পর নতুন CreatedAt সহ item বসায়।</summary>
    public void Update(ClipboardItem item, DateTimeOffset now)
    {
        Item = item;
        RefreshTime(now);
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
            return "এইমাত্র";
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
