using System.ComponentModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>Load এর সময় কী পাওয়া গেল।</summary>
public sealed record SettingsLoadResult(bool FileExisted, bool WasCorrupt, IReadOnlyList<string> InvalidFields, bool HasLaunchAtLogin);

/// <summary>
/// Settings disk এ রাখে: %LocalAppData%\PulseBar\settings.json। প্রতিটা মান আলাদা করে পড়া হয়,
/// তাই একটা মান ভুল হলে শুধু সেটাই default হয়; file পুরোটা নষ্ট হলে settings.json.corrupt নামে
/// সরিয়ে রেখে default দিয়ে চলে। Save ৫০০ ms debounce করা, আগে temp file এ লিখে তারপর replace।
/// কোনো অবস্থাতেই exception ছোড়ে না।
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const int CurrentVersion = 1;

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock _saveGate = new();
    private readonly Lock _writeGate = new();
    private readonly Timer _saveTimer;
    private AppSettings? _attached;
    private byte[]? _pending;
    private bool _disposed;

    /// <param name="directory">null হলে %LocalAppData%\PulseBar (test এ আলাদা folder দেওয়া যায়)।</param>
    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseBar");
        FilePath = Path.Combine(Directory, "settings.json");
        _saveTimer = new Timer(_ => WritePending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>File থেকে <paramref name="target"/> এ মান বসায়। না থাকা বা ভুল মান default থাকে।</summary>
    public SettingsLoadResult Load(AppSettings target)
    {
        var invalid = new List<string>();
        bool existed = false;
        bool corrupt = false;
        bool hasLaunch = false;

        try
        {
            if (File.Exists(FilePath))
            {
                existed = true;
                using var stream = File.OpenRead(FilePath);
                try
                {
                    // Stream থেকে পড়লে UTF-8 BOM থাকলেও চলে
                    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    });

                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("Root is not an object");
                    }

                    hasLaunch = Read(document.RootElement, target, invalid);
                }
                catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
                {
                    corrupt = true;
                    Log.Error("Settings file is corrupt; using defaults", ex);
                    stream.Dispose();
                    TryMove(FilePath, FilePath + ".corrupt");
                }
            }
        }
        catch (Exception ex)
        {
            // পড়া গেল না (permission, lock): default দিয়ে চলবে
            Log.Error("Settings load", ex);
        }

        // দুটো feature এ একই hotkey থাকবে না
        if (target.ScreenshotHotKey == target.ClipboardPanelHotKey)
        {
            invalid.Add(nameof(AppSettings.ScreenshotHotKey));
            target.ScreenshotHotKey = AppSettings.DefaultScreenshotHotKey;
            if (target.ScreenshotHotKey == target.ClipboardPanelHotKey)
            {
                target.ClipboardPanelHotKey = AppSettings.DefaultClipboardPanelHotKey;
            }
        }

        if (invalid.Count > 0)
        {
            Log.Error($"Settings: invalid values reset to default: {string.Join(", ", invalid)}");
        }

        return new SettingsLoadResult(existed, corrupt, invalid, hasLaunch);
    }

    /// <summary>এরপর <paramref name="settings"/> এর প্রতিটা বদল save হবে। UI thread থেকে।</summary>
    public void Attach(AppSettings settings)
    {
        if (_attached is not null)
        {
            _attached.PropertyChanged -= OnSettingChanged;
        }

        _attached = settings;
        settings.PropertyChanged += OnSettingChanged;
    }

    /// <summary>এখনকার মান save এর জন্য রাখে (debounce করা)। যে thread এ settings বদলায় সেখান থেকে ডাকতে হবে।</summary>
    public void ScheduleSave(AppSettings settings)
    {
        byte[] bytes = Serialize(settings);
        lock (_saveGate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = bytes;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>বাকি থাকা save এখনই লেখে।</summary>
    public void Flush() => WritePending();

    public void Dispose()
    {
        if (_attached is not null)
        {
            _attached.PropertyChanged -= OnSettingChanged;
            _attached = null;
        }

        lock (_saveGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveTimer.Dispose();
        }

        WritePending();
    }

    public static byte[] Serialize(AppSettings s)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,

            // '+' যেন + হয়ে না যায়, file টা হাতে পড়া যায়; এটা শুধু নিজের file, browser এ যায় না
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", CurrentVersion);
            writer.WriteString(nameof(AppSettings.Theme), s.Theme.ToString());
            writer.WriteString(nameof(AppSettings.LaunchAtLogin), s.LaunchAtLogin.ToString());
            writer.WriteBoolean(nameof(AppSettings.ShowTaskbarOverlay), s.ShowTaskbarOverlay);
            writer.WriteString(nameof(AppSettings.OverlayLayout), s.OverlayLayout.ToString());
            writer.WriteBoolean(nameof(AppSettings.ClipboardEnabled), s.ClipboardEnabled);
            writer.WriteString(nameof(AppSettings.ClipboardRetention), s.ClipboardRetention.ToString());
            writer.WriteString(nameof(AppSettings.ClipboardPanelHotKey), s.ClipboardPanelHotKey.ToString());
            writer.WriteBoolean(nameof(AppSettings.AutoPaste), s.AutoPaste);
            writer.WriteString(nameof(AppSettings.ScreenshotHotKey), s.ScreenshotHotKey.ToString());
            writer.WriteBoolean(nameof(AppSettings.WatchScreenshotsFolder), s.WatchScreenshotsFolder);
            writer.WriteBoolean(nameof(AppSettings.AutoCopyScreenshots), s.AutoCopyScreenshots);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <returns>File এ ঠিক LaunchAtLogin মান ছিল কিনা।</returns>
    private static bool Read(JsonElement root, AppSettings target, List<string> invalid)
    {
        // Version এখন শুধু লেখা হয়; পরে format বদলালে এটা দেখে পুরনো file migrate করা যাবে
        if (root.TryGetProperty("Version", out var version) && version.ValueKind != JsonValueKind.Number)
        {
            invalid.Add("Version");
        }

        ReadEnum<AppTheme>(root, nameof(AppSettings.Theme), v => target.Theme = v, invalid);
        bool hasLaunch = ReadEnum<LaunchAtLogin>(root, nameof(AppSettings.LaunchAtLogin), v => target.LaunchAtLogin = v, invalid);
        ReadBool(root, nameof(AppSettings.ShowTaskbarOverlay), v => target.ShowTaskbarOverlay = v, invalid);
        ReadEnum<OverlayLayout>(root, nameof(AppSettings.OverlayLayout), v => target.OverlayLayout = v, invalid);
        ReadBool(root, nameof(AppSettings.ClipboardEnabled), v => target.ClipboardEnabled = v, invalid);
        ReadEnum<ClipboardRetention>(root, nameof(AppSettings.ClipboardRetention), v => target.ClipboardRetention = v, invalid);
        ReadHotKey(root, nameof(AppSettings.ClipboardPanelHotKey), v => target.ClipboardPanelHotKey = v, invalid);
        ReadBool(root, nameof(AppSettings.AutoPaste), v => target.AutoPaste = v, invalid);
        ReadHotKey(root, nameof(AppSettings.ScreenshotHotKey), v => target.ScreenshotHotKey = v, invalid);
        ReadBool(root, nameof(AppSettings.WatchScreenshotsFolder), v => target.WatchScreenshotsFolder = v, invalid);
        ReadBool(root, nameof(AppSettings.AutoCopyScreenshots), v => target.AutoCopyScreenshots = v, invalid);
        return hasLaunch;
    }

    private static bool ReadBool(JsonElement root, string name, Action<bool> set, List<string> invalid)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false; // পুরনো file এ নেই: default থাকবে
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            set(value.GetBoolean());
            return true;
        }

        invalid.Add(name);
        return false;
    }

    private static bool ReadEnum<T>(JsonElement root, string name, Action<T> set, List<string> invalid)
        where T : struct, Enum
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        // শুধু নাম দিয়ে; সংখ্যা ("2") বা অজানা নাম মানা হবে না
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text &&
            !char.IsDigit(text[0]) && text[0] != '-' &&
            Enum.TryParse(text, ignoreCase: true, out T parsed) &&
            Enum.IsDefined(parsed))
        {
            set(parsed);
            return true;
        }

        invalid.Add(name);
        return false;
    }

    private static bool ReadHotKey(JsonElement root, string name, Action<HotKey> set, List<string> invalid)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String && HotKey.TryParse(value.GetString(), out var hotKey))
        {
            set(hotKey);
            return true;
        }

        invalid.Add(name);
        return false;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AppSettings settings)
        {
            ScheduleSave(settings);
        }
    }

    private void WritePending()
    {
        byte[]? bytes;
        lock (_saveGate)
        {
            bytes = _pending;
            _pending = null;
        }

        if (bytes is null)
        {
            return;
        }

        // Timer আর Dispose একসাথে লিখবে না
        lock (_writeGate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                // মাঝপথে বন্ধ হলেও settings.json নষ্ট হবে না
                string temp = FilePath + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Settings save", ex);
            }
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Settings: could not move the corrupt file aside", ex);
        }
    }
}
