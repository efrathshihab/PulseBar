namespace PulseBar.Models;

/// <summary>Kind of output device, from PKEY_AudioEndpoint_FormFactor.</summary>
public enum AudioDeviceKind
{
    Other,
    Speakers,
    Headphones,
    Headset,

    /// <summary>HDMI or DisplayPort (monitor/TV).</summary>
    Display,
}

/// <summary>One active output (render) device. Immutable.</summary>
public sealed record AudioOutputDevice
{
    /// <summary>Windows endpoint ID.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public AudioDeviceKind Kind { get; init; }

    /// <summary>Whether it is the default now (Multimedia role).</summary>
    public bool IsDefault { get; init; }
}

/// <summary>Master volume of the default device.</summary>
/// <param name="Volume">0.0 to 1.0.</param>
public sealed record MasterVolumeState(float Volume, bool IsMuted);
