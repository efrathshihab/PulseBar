using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PulseBar.Models;
using PulseBar.ViewModels;

namespace PulseBar.Views.Controls;

/// <summary>
/// Hotkey বদলানোর box। Click করলে "Press keys…", তারপর যে combo চাপা হয় সেটা নেয়। অন্তত একটা
/// modifier লাগে; Esc বা focus হারালে বাতিল। নেওয়া না গেলে নিচে লাল লেখায় কারণ।
/// DataContext: <see cref="HotKeySettingViewModel"/>।
/// </summary>
public partial class HotKeyBox : UserControl
{
    private const string PromptText = "Press keys…";

    private bool _capturing;

    public HotKeyBox()
    {
        InitializeComponent();

        // Popup লুকালে অপেক্ষা বাতিল, যাতে hotkey গুলো ছাড়া অবস্থায় পড়ে না থাকে
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

        // Enter/Space এ button এর click বা window এর Esc যেন না চলে
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
            // শুধু modifier চাপা: এখন পর্যন্ত কী চাপা হয়েছে দেখানো
            ShowPartial(modifiers);
            return;
        }

        if (modifiers == ModifierKeys.None)
        {
            // শুধু একটা অক্ষর hotkey হবে না
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
    /// Hotkey গুলো আবার register হওয়ার আগে চলে, যাতে নতুনটা register করে দেখা যায় আর পুরনোটা ফিরে না আসে।
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

        // Key ছাড়া HotKey এর লেখা "Ctrl+Alt+None" হয়, তাই None এর জায়গায় "…"
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
