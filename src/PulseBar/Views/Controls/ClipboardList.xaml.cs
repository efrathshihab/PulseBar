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

/// <summary>Click or Enter on a card: which item, and whether Shift was held (then copy only).</summary>
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

        // The ListBox may capture the mouse itself and mark button events handled, so
        // handled events are listened to as well. Selection isn't relied on, so clicking an
        // already selected card still copies.
        ItemList.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(ItemList_MouseDown), handledEventsToo: true);
        ItemList.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(ItemList_MouseUp), handledEventsToo: true);
    }

    /// <summary>
    /// Raised on click or Enter on a card. If nobody listens, the item is just copied (the popup tab);
    /// a listener decides what happens (hotkey panel: copy, close, paste).
    /// </summary>
    public event EventHandler<ClipboardItemActivatedEventArgs>? ItemActivated;

    /// <summary>Whether to show the "Clear all" button. The confirm dialog needs a ContentDialogHost in the window.</summary>
    public bool ShowClearAll
    {
        get => ClearAllButton.Visibility == Visibility.Visible;
        set => ClearAllButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private ClipboardViewModel? ViewModel => DataContext as ClipboardViewModel;

    /// <summary>Gives keyboard focus to the search box (selecting all text if any).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    /// <summary>Scrolls so the selected item is visible.</summary>
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

        // Press and release must be on the same card; not a copy on the delete button or scrollbar
        if (pressed is not null && ReferenceEquals(CardItemAt(e.OriginalSource as DependencyObject), pressed))
        {
            Activate(pressed);
        }
    }

    /// <summary>Which card the click is over; null over a button or the scrollbar.</summary>
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
                // With a selected item (the first one after a search), go to the next one, otherwise the first
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
                // Up from the first item: back to the search box
                FocusSearch();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Gives keyboard focus to the selected card so Up/Down continue from there.</summary>
    private void FocusSelected()
    {
        if (ViewModel?.SelectedItem is not { } item)
        {
            SearchBox.Focus();
            return;
        }

        ItemList.ScrollIntoView(item);

        // In a virtualized list the container is created after layout
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
            Content = "All clipboard history will be deleted",
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
