using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PulseBar.ViewModels;
using Wpf.Ui.Controls;

namespace PulseBar.Views.Controls;

/// <summary>Card এ click বা Enter: কোন item, আর Shift চাপা ছিল কিনা (তখন শুধু copy)।</summary>
public sealed class ClipboardItemActivatedEventArgs(ClipboardItemViewModel item, bool copyOnly) : EventArgs
{
    public ClipboardItemViewModel Item { get; } = item;

    public bool CopyOnly { get; } = copyOnly;
}

public partial class ClipboardList : UserControl
{
    private ClipboardItemViewModel? _pressedItem;

    public ClipboardList()
    {
        InitializeComponent();

        // ListBox নিজে mouse capture করে আর button event handled করে দিতে পারে, তাই
        // handled event ও শোনা হয়। Selection এর উপর নির্ভর করা হয় না, যাতে আগে থেকে
        // select করা card এ click করলেও copy হয়।
        ItemList.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(ItemList_MouseDown), handledEventsToo: true);
        ItemList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(ItemList_MouseUp), handledEventsToo: true);
    }

    /// <summary>
    /// Card এ click বা Enter হলে। কেউ না শুনলে item টা শুধু copy হয় (popup এর tab);
    /// শুনলে কী হবে সেটা সে ঠিক করে (hotkey panel: copy, বন্ধ, paste)।
    /// </summary>
    public event EventHandler<ClipboardItemActivatedEventArgs>? ItemActivated;

    /// <summary>"Clear all" button দেখাবে কিনা। Confirm dialog এর জন্য window এ ContentDialogHost লাগে।</summary>
    public bool ShowClearAll
    {
        get => ClearAllButton.Visibility == Visibility.Visible;
        set => ClearAllButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private ClipboardViewModel? ViewModel => DataContext as ClipboardViewModel;

    /// <summary>Search box এ keyboard focus দেয় (লেখা থাকলে সব select)।</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    /// <summary>বাছাই করা item টা দেখা যায় এমনভাবে scroll করে।</summary>
    public void ScrollToSelected()
    {
        if (ViewModel?.SelectedItem is { } item)
        {
            ItemList.ScrollIntoView(item);
        }
    }

    private void Activate(ClipboardItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        bool copyOnly = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ItemActivated is { } handler)
        {
            handler(this, new ClipboardItemActivatedEventArgs(item, copyOnly));
        }
        else
        {
            ViewModel?.Copy(item);
        }
    }

    private void ItemList_MouseDown(object sender, MouseButtonEventArgs e) =>
        _pressedItem = CardItemAt(e.OriginalSource as DependencyObject);

    private void ItemList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedItem;
        _pressedItem = null;

        // চাপা আর ছাড়া একই card এ হতে হবে; delete button বা scrollbar এ হলে copy না
        if (pressed is not null && ReferenceEquals(CardItemAt(e.OriginalSource as DependencyObject), pressed))
        {
            Activate(pressed);
        }
    }

    /// <summary>Click টা কোন card এর উপরে; button বা scrollbar এর উপরে হলে null।</summary>
    private static ClipboardItemViewModel? CardItemAt(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualOrLogicalParent(node))
        {
            switch (node)
            {
                case ButtonBase:
                case ScrollBar:
                    return null;
                case ListBoxItem { DataContext: ClipboardItemViewModel item }:
                    return item;
            }
        }

        return null;
    }

    private static DependencyObject? VisualOrLogicalParent(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardItemViewModel item })
        {
            ViewModel?.Delete(item);
        }

        e.Handled = true;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Activate(vm.CurrentOrFirst());
                e.Handled = true;
                break;

            case Key.Down when vm.Items.Count > 0:
                // বাছাই করা item থাকলে (search এর পরে প্রথমটা) তার পরেরটায়, না থাকলে প্রথমটায়
                var current = vm.CurrentOrFirst();
                int index = current is not null && ReferenceEquals(vm.SelectedItem, current) ? vm.Items.IndexOf(current) + 1 : 0;
                vm.SelectedItem = vm.Items[Math.Clamp(index, 0, vm.Items.Count - 1)];
                FocusSelected();
                e.Handled = true;
                break;
        }
    }

    private void ItemList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SelectedItem is not { } selected)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Activate(selected);
                e.Handled = true;
                break;

            case Key.Delete:
                vm.Delete(selected);
                FocusSelected();
                e.Handled = true;
                break;

            case Key.Up when vm.Items.IndexOf(selected) == 0:
                // প্রথম item থেকে উপরে: আবার search box এ
                FocusSearch();
                e.Handled = true;
                break;
        }
    }

    /// <summary>বাছাই করা card এ keyboard focus দেয়, যাতে Up/Down সেখান থেকে চলে।</summary>
    private void FocusSelected()
    {
        if (ViewModel?.SelectedItem is not { } item)
        {
            SearchBox.Focus();
            return;
        }

        ItemList.ScrollIntoView(item);

        // Virtualized list এ container টা layout এর পরে তৈরি হয়
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ItemList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            {
                container.Focus();
            }
        });
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || Window.GetWindow(this) is not { } window ||
            ContentDialogHost.GetForWindow(window) is not { } host)
        {
            return;
        }

        var dialog = new ContentDialog(host)
        {
            Title = "Clear all?",
            Content = "সব clipboard history মুছে যাবে",
            PrimaryButtonText = "Clear all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            DialogMaxWidth = 320,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            vm.ClearAll();
        }
    }
}
