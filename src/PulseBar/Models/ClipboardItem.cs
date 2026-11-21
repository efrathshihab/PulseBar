using System.Text.Json.Serialization;

namespace PulseBar.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ClipboardItemKind>))]
public enum ClipboardItemKind
{
    Text,
    Image,
}

/// <summary>
/// One clipboard history item. Immutable; moving it to the top makes a copy with a new CreatedAt.
/// </summary>
public sealed record ClipboardItem
{
    public const int PreviewLength = 200;

    public required string Id { get; init; }

    public required ClipboardItemKind Kind { get; init; }

    /// <summary>The full text of a text item; null for images.</summary>
    public string? Text { get; init; }

    /// <summary>Path of the full image PNG file; null for text.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Path of the small (~240px wide) PNG thumbnail; null for text.</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>The first ~200 characters of text; for images e.g. "1920 × 1080".</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>The app it was copied from, if known.</summary>
    public string? SourceApp { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Came from Snipping Tool / Screen Clipping, or is a new file from the Screenshots folder. Older
    /// history.json files don't have this field, so it's false then.
    /// </summary>
    public bool IsScreenshot { get; init; }

    /// <summary>SHA-256 (hex) to recognize the same content copied a second time.</summary>
    public required string ContentHash { get; init; }

    public static string MakePreview(string text) =>
        text.Length <= PreviewLength ? text : text[..PreviewLength];
}
