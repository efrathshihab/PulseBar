using System.Windows.Input;

namespace PulseBar.Models;

/// <summary>একটা global hotkey: modifier গুলো আর একটা key, যেমন Ctrl+Alt+V।</summary>
public readonly record struct HotKey(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
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

        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
