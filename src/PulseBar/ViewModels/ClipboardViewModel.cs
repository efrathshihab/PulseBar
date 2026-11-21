using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

public enum ClipboardFilter
{
    All,
    Text,
    Images,
    Screenshots,
}

/// <summary>
/// Used by both the Clipboard tab and the hotkey clipboard panel (only one is open at a time):
/// filter, search, copy, delete, clear all. While both are hidden it updates nothing and runs no
/// timers; it reconciles the list when one opens.
/// </summary>
public sealed partial class ClipboardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ClockInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailedFeedback = TimeSpan.FromSeconds(2);

    private readonly ClipboardManager _manager;
    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly Dictionary<string, ClipboardItemViewModel> _cache = [];
    private List<ClipboardItemViewModel> _all = [];
    private int _dirty = 1;
    private bool _shown;

    /// <summary>What is shown after filter and search, newest first.</summary>
    public ObservableCollection<ClipboardItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ClipboardFilter Filter { get; set; } = ClipboardFilter.All;

    [ObservableProperty]
    public partial ClipboardItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool HasAny { get; set; }

    [ObservableProperty]
    public partial bool IsEmptyVisible { get; set; } = true;

    /// <summary>True when history is empty (clipboard icon), false when search has no match (search icon).</summary>
    [ObservableProperty]
    public partial bool IsHistoryEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = NothingCopied;

    private const string NothingCopied = "Nothing copied yet";
    private const string NothingFound = "No results";

    public ClipboardViewModel(ClipboardManager manager, AppState state, Dispatcher dispatcher)
    {
        _manager = manager;
        _state = state;
        _dispatcher = dispatcher;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SearchDelay };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter(selectFirst: true);
        };

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = ClockInterval };
        _clockTimer.Tick += (_, _) => RefreshTimes();

        _manager.HistoryChanged += OnHistoryChanged;
        _state.PopupVisibilityChanged += OnVisibilityChanged;
        _state.ClipboardPanelVisibilityChanged += OnVisibilityChanged;
    }

    /// <summary>Either the popup or the panel is open.</summary>
    private bool IsShown => _state.IsPopupVisible || _state.IsClipboardPanelVisible;

    public void Dispose()
    {
        _searchTimer.Stop();
        _clockTimer.Stop();
        _manager.HistoryChanged -= OnHistoryChanged;
        _state.PopupVisibilityChanged -= OnVisibilityChanged;
        _state.ClipboardPanelVisibilityChanged -= OnVisibilityChanged;
    }

    /// <summary>Puts the item on the clipboard, moves it to the top and shows "Copied" for 1 second.</summary>
    public void Copy(ClipboardItemViewModel? item) => _ = CopyAsync(item);

    /// <summary>Like <see cref="Copy"/>; completes once the clipboard is written (true on success).</summary>
    public async Task<bool> CopyAsync(ClipboardItemViewModel? item)
    {
        if (item is null)
        {
            return false;
        }

        SelectedItem = item;
        item.IsCopied = false;
        item.IsCopyFailed = false;

        // On success the manager moves the item to the top; on failure the reason is logged
        string? error = await _manager.CopyToClipboardAsync(item.Id);
        bool ok = error is null;
        item.IsCopied = ok;
        item.IsCopyFailed = !ok;
        _ = ClearFeedbackAsync(item, ok ? CopiedFeedback : FailedFeedback);
        return ok;
    }

    private static async Task ClearFeedbackAsync(ClipboardItemViewModel item, TimeSpan delay)
    {
        await Task.Delay(delay);
        item.IsCopied = false;
        item.IsCopyFailed = false;
    }

    /// <summary>
    /// Which item Enter in the search box takes: the selected item, or the first result if none. Even if
    /// Enter is pressed before the debounce ends, it uses the latest text.
    /// </summary>
    public ClipboardItemViewModel? CurrentOrFirst()
    {
        FlushSearch();

        if (SelectedItem is { } selected && Items.Contains(selected))
        {
            return selected;
        }

        return Items.Count > 0 ? Items[0] : null;
    }

    /// <summary>
    /// When the hotkey panel opens: clears search and filter and selects the first item.
    /// </summary>
    public void ResetForQuickPick()
    {
        SearchText = string.Empty;
        _searchTimer.Stop();
        Filter = ClipboardFilter.All;
        ApplyFilter(selectFirst: true);
    }

    /// <summary>Deletes the item and selects the next one (for deleting several in a row with the keyboard).</summary>
    public void Delete(ClipboardItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        int index = Items.IndexOf(item);
        bool wasSelected = ReferenceEquals(SelectedItem, item);

        _manager.Delete(item.Id);
        Refresh();

        if (wasSelected && Items.Count > 0 && index >= 0)
        {
            SelectedItem = Items[Math.Min(index, Items.Count - 1)];
        }
    }

    public void ClearAll()
    {
        _manager.ClearAll();
        Refresh();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnFilterChanged(ClipboardFilter value) => ApplyFilter(selectFirst: true);

    private void FlushSearch()
    {
        if (_searchTimer.IsEnabled)
        {
            _searchTimer.Stop();
            ApplyFilter(selectFirst: true);
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        // May arrive from any thread; posted to the UI thread only once
        if (Interlocked.Exchange(ref _dirty, 1) == 0 && IsShown)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshIfDirty);
        }
    }

    private void OnVisibilityChanged(object? sender, bool _)
    {
        bool visible = IsShown;
        if (visible == _shown)
        {
            return;
        }

        _shown = visible;
        if (visible)
        {
            RefreshIfDirty();
            RefreshTimes();
            _clockTimer.Start();
            return;
        }

        // No timers while hidden; any pending search is applied now
        _clockTimer.Stop();
        FlushSearch();
    }

    private void RefreshIfDirty()
    {
        if (IsShown && Volatile.Read(ref _dirty) == 1)
        {
            Refresh();
        }
    }

    /// <summary>Reconciles the item ViewModels with the manager's history (reusing old ViewModels).</summary>
    private void Refresh()
    {
        Interlocked.Exchange(ref _dirty, 0);

        var now = DateTimeOffset.Now;
        var items = _manager.GetItems();
        var all = new List<ClipboardItemViewModel>(items.Length);
        var seen = new HashSet<string>();

        foreach (var item in items)
        {
            seen.Add(item.Id);
            if (_cache.TryGetValue(item.Id, out var vm))
            {
                if (!ReferenceEquals(vm.Item, item))
                {
                    vm.Update(item, now);
                }
            }
            else
            {
                vm = new ClipboardItemViewModel(item, now);
                _cache[item.Id] = vm;
            }

            all.Add(vm);
        }

        foreach (string id in _cache.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _cache.Remove(id);
        }

        _all = all;
        ApplyFilter();
    }

    /// <param name="selectFirst">
    /// True when search/filter changed: the first result is selected so the item Enter will take is highlighted.
    /// </param>
    private void ApplyFilter(bool selectFirst = false)
    {
        string query = SearchText.Trim();

        var target = _all.Where(vm => Filter switch
        {
            ClipboardFilter.Text => vm.IsText,
            ClipboardFilter.Images => vm.IsImage,
            ClipboardFilter.Screenshots => vm.IsScreenshot,
            _ => true,
        });

        if (query.Length > 0)
        {
            target = target.Where(vm => vm.Matches(query));
        }

        Sync(target.ToList());

        HasAny = _all.Count > 0;
        IsHistoryEmpty = _all.Count == 0;
        IsEmptyVisible = Items.Count == 0;
        EmptyMessage = IsHistoryEmpty ? NothingCopied : NothingFound;

        if (selectFirst)
        {
            SelectedItem = Items.Count > 0 ? Items[0] : null;
        }
        else if (SelectedItem is not null && !Items.Contains(SelectedItem))
        {
            SelectedItem = null;
        }
    }

    /// <summary>
    /// Uses only Move/Insert/Remove instead of replacing the whole collection, so scroll and selection
    /// aren't lost and virtualized containers don't have to be rebuilt.
    /// </summary>
    private void Sync(List<ClipboardItemViewModel> target)
    {
        var keep = new HashSet<ClipboardItemViewModel>(target);
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Items[i]))
            {
                Items.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            var wanted = target[i];
            if (i < Items.Count && ReferenceEquals(Items[i], wanted))
            {
                continue;
            }

            int existing = Items.IndexOf(wanted);
            if (existing >= 0)
            {
                Items.Move(existing, i);
            }
            else
            {
                Items.Insert(i, wanted);
            }
        }
    }

    private void RefreshTimes()
    {
        var now = DateTimeOffset.Now;
        foreach (var vm in _all)
        {
            vm.RefreshTime(now);
        }
    }
}
