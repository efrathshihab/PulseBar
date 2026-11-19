namespace PulseBar.Models;

/// <summary>
/// Sound tab এর একটা row: একই exe এর সব audio session একসাথে (যেমন Chrome এর কয়েকটা)।
/// Immutable; কিছু বদলালে <see cref="Services.AudioController"/> নতুন snapshot বানায়।
/// </summary>
public sealed record AudioAppSession
{
    /// <summary>Row চেনার জন্য: exe এর পুরো path (ছোট হাতে), System sounds এর জন্য <see cref="SystemSoundsKey"/>।</summary>
    public required string Key { get; init; }

    /// <summary>Exe এর FileDescription, না পেলে exe এর নাম; অথবা "System sounds"।</summary>
    public required string Name { get; init; }

    /// <summary>Icon কোন file থেকে (exe বা dll)। না থাকলে null।</summary>
    public string? IconPath { get; init; }

    /// <summary>File এর ভেতরে icon এর index; ঋণাত্মক হলে resource ID।</summary>
    public int IconIndex { get; init; }

    public bool IsSystemSounds { get; init; }

    /// <summary>0.0 থেকে 1.0; কয়েকটা session থাকলে সবচেয়ে বেশিটা।</summary>
    public float Volume { get; init; }

    /// <summary>সব session mute থাকলে true।</summary>
    public bool IsMuted { get; init; }

    public const string SystemSoundsKey = "#system-sounds";
}
