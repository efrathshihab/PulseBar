namespace PulseBar.Models;

/// <summary>
/// One row of the Sound tab: all audio sessions of the same exe together (e.g. several from Chrome).
/// Immutable; when anything changes <see cref="Services.AudioController"/> builds a new snapshot.
/// </summary>
public sealed record AudioAppSession
{
    /// <summary>Identifies the row: the exe's full path (lowercase), or <see cref="SystemSoundsKey"/> for System sounds.</summary>
    public required string Key { get; init; }

    /// <summary>The exe's FileDescription, or the exe name if missing; or "System sounds".</summary>
    public required string Name { get; init; }

    /// <summary>File the icon comes from (exe or dll). null if none.</summary>
    public string? IconPath { get; init; }

    /// <summary>Index of the icon inside the file; negative means a resource ID.</summary>
    public int IconIndex { get; init; }

    public bool IsSystemSounds { get; init; }

    /// <summary>0.0 to 1.0; the highest one when there are several sessions.</summary>
    public float Volume { get; init; }

    /// <summary>True when all sessions are muted.</summary>
    public bool IsMuted { get; init; }

    public const string SystemSoundsKey = "#system-sounds";
}
