namespace PulseBar.Models;

/// <summary>Output device এর ধরন, PKEY_AudioEndpoint_FormFactor থেকে।</summary>
public enum AudioDeviceKind
{
    Other,
    Speakers,
    Headphones,
    Headset,

    /// <summary>HDMI বা DisplayPort (monitor/TV)।</summary>
    Display,
}

/// <summary>একটা Active output (render) device। Immutable।</summary>
public sealed record AudioOutputDevice
{
    /// <summary>Windows এর endpoint ID।</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public AudioDeviceKind Kind { get; init; }

    /// <summary>এখন default (Multimedia role) কিনা।</summary>
    public bool IsDefault { get; init; }
}

/// <summary>Default device এর master volume।</summary>
/// <param name="Volume">0.0 থেকে 1.0।</param>
public sealed record MasterVolumeState(float Volume, bool IsMuted);
