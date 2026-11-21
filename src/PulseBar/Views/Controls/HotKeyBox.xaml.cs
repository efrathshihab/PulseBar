using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PulseBar.Models;
using PulseBar.ViewModels;

namespace PulseBar.Views.Controls;

/// <summary>
/// Box for changing a hotkey. Click shows "Press keys…", then takes the combo that is pressed. At least one
/// modifier is required; Esc or losing focus cancels. If it can't be used, the reason shows in red below.
/// DataContext: <see cref="HotKeySettingViewModel"/>।
/// </summary>
public partial class HotKeyBox : UserControl
{
    private const string PromptText = "Press keys…";

    private bool _capturing;

    public HotKeyBox()
    {
        InitializeComponent();

        // Cancel waiting when the popup hides, so the hotkeys aren't left released
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                EndCapture();
            }
        };
    }

    private HotKeySettingViewModel? ViewModel => DataContext as HotKeySettingViewModel;

    private void Box_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            EndCapture();
            return;
        }

        if (ViewModel is not { } vm)
        {
            return;
        }

        _capturing = true;
        vm.BeginCapture();
        BoxText.SetCurrentValue(TextBlock.TextProperty, PromptText);
        Box.Focus();
        Keyboard.Focus(Box);
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        // So Enter/Space don't click the button and Esc doesn't reach the window
        e.Handled = true;

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        var modifiers = CurrentModifiers();
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            EndCapture();
            return;
        }

        if (!HotKey.IsUsableKey(key))
        {
            // Only modifiers pressed: show what has been pressed so far
            ShowPartial(modifiers);
            return;
        }

        if (modifiers == ModifierKeys.None)
        {
            // A single letter can't be a hotkey
            ShowPartial(modifiers);
            return;
        }

        var hotKey = new HotKey(modifiers, key);
        var vm = ViewModel;
        EndCapture(apply: () => vm?.TryApply(hotKey));
    }

    private void Box_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_capturing)
        {
            e.Handled = true;
            ShowPartial(CurrentModifiers());
        }
    }

    private void Box_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturing)
        {
            EndCapture();
        }
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            EndCapture();
        }

        ViewModel?.ResetToDefault();
    }

    /// <param name="apply">
    /// Runs before the hotkeys are registered again, so the new one can be test-registered and the old one doesn't come back.
    /// </param>
    private void EndCapture(Action? apply = null)
    {
        if (!_capturing)
        {
            return;
        }

        _capturing = false;
        apply?.Invoke();
        ViewModel?.EndCapture();
        BindingOperations.GetBindingExpression(BoxText, TextBlock.TextProperty)?.UpdateTarget();
    }

    private void ShowPartial(ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.None)
        {
            BoxText.SetCurrentValue(TextBlock.TextProperty, PromptText);
            return;
        }

        // A HotKey without a key reads "Ctrl+Alt+None", so "…" replaces None
        string text = new HotKey(modifiers, Key.None).ToString();
        BoxText.SetCurrentValue(TextBlock.TextProperty, text[..^"None".Length] + "…");
    }

    private static ModifierKeys CurrentModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= ModifierKeys.Windows;
        }

        return modifiers;
    }
}
