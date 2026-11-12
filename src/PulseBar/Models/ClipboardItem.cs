using System.Text.Json.Serialization;

namespace PulseBar.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ClipboardItemKind>))]
public enum ClipboardItemKind
{
    Text,
    Image,
}

/// <summary>
/// Clipboard history এর একটা item। Immutable; উপরে তোলার সময় নতুন CreatedAt দিয়ে কপি বানানো হয়।
/// </summary>
public sealed record ClipboardItem
{
    public const int PreviewLength = 200;

    public required string Id { get; init; }

    public required ClipboardItemKind Kind { get; init; }

    /// <summary>Text item এর পুরো লেখা; ছবি হলে null।</summary>
    public string? Text { get; init; }

    /// <summary>পুরো ছবির PNG file এর path; text হলে null।</summary>
    public string? ImagePath { get; init; }

    /// <summary>ছোট (চওড়া ~240px) PNG thumbnail এর path; text হলে null।</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>Text এর প্রথম ~২০০ অক্ষর; ছবি হলে যেমন "1920 × 1080"।</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>কোন app থেকে copy হয়েছে, পাওয়া গেলে।</summary>
    public string? SourceApp { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>একই জিনিস দ্বিতীয়বার copy হলে চেনার জন্য SHA-256 (hex)।</summary>
    public required string ContentHash { get; init; }

    public static string MakePreview(string text) =>
        text.Length <= PreviewLength ? text : text[..PreviewLength];
}
