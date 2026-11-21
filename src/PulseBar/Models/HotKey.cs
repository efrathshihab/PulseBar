using System.Windows.Input;

namespace PulseBar.Models;

/// <summary>একটা global hotkey: modifier গুলো আর একটা key, যেমন Ctrl+Alt+V।</summary>
public readonly record struct HotKey(ModifierKeys Modifiers, Key Key)
{
    private const ModifierKeys AllModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

    /// <summary>অন্তত একটা modifier আছে আর মূল key টা নিজে modifier না।</summary>
    public bool IsValid =>
        (Modifiers & AllModifiers) != ModifierKeys.None &&
        (Modifiers & ~AllModifiers) == ModifierKeys.None &&
        IsUsableKey(Key);

    /// <summary>যেমন "Ctrl+Alt+V"। Settings file এ এই রূপেই থাকে, <see cref="TryParse"/> আবার পড়তে পারে।</summary>
    public override string ToString() => Format(Key.ToString());

    /// <summary>UI এর জন্য: D1 এর বদলে "1"।</summary>
    public string DisplayText => Format(KeyDisplayName(Key));

    /// <summary>"Ctrl+Alt+V" ধরনের লেখা পড়ে। ভুল লেখা বা modifier ছাড়া হলে false।</summary>
    public static bool TryParse(string? text, out HotKey hotKey)
    {
        hotKey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            ModifierKeys modifier = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" or "windows" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };

            if (modifier == ModifierKeys.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        // সংখ্যা দিয়ে enum পড়া হবে না ("Ctrl+65" এর মতো)
        string keyName = parts[^1];
        if (keyName.Length == 0 || char.IsDigit(keyName[0]) && keyName.Length > 1 ||
            !Enum.TryParse(keyName.Length == 1 && char.IsDigit(keyName[0]) ? "D" + keyName : keyName, ignoreCase: true, out Key key) ||
            !Enum.IsDefined(key))
        {
            return false;
        }

        var parsed = new HotKey(modifiers, key);
        if (!parsed.IsValid)
        {
            return false;
        }

        hotKey = parsed;
        return true;
    }

    /// <summary>Hotkey এর মূল key হতে পারে কিনা: modifier, IME বা অদ্ভুত key না।</summary>
    public static bool IsUsableKey(Key key) => key switch
    {
        Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or
        Key.ImeProcessed or Key.DeadCharProcessed or Key.Apps or Key.Capital or
        Key.NumLock or Key.Scroll => false,
        _ => KeyInterop.VirtualKeyFromKey(key) != 0,
    };

    private string Format(string keyName)
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private static string KeyDisplayName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (char)('0' + (key - Key.NumPad0)),
        Key.Snapshot => "PrtSc",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Next => "PgDn",
        Key.Prior => "PgUp",
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        _ => key.ToString(),
    };
}
