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
}

/// <summary>
/// Clipboard tab: filter, search, copy, delete, clear all।
/// Popup লুকানো থাকলে কিছুই update করে না আর কোনো timer চালায় না; খুললে তখন list মেলায়।
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

    /// <summary>Filter আর search এর পরে যা দেখা যাবে, নতুনটা আগে।</summary>
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

    /// <summary>History খালি হলে true (clipboard icon), search এ না মিললে false (search icon)।</summary>
    [ObservableProperty]
    public partial bool IsHistoryEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = NothingCopied;

    private const string NothingCopied = "এখনো কিছু copy করা হয়নি";
    private const string NothingFound = "কিছু পাওয়া যায়নি";

    public ClipboardViewModel(ClipboardManager manager, AppState state, Dispatcher dispatcher)
    {
        _manager = manager;
        _state = state;
        _dispatcher = dispatcher;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SearchDelay };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = ClockInterval };
        _clockTimer.Tick += (_, _) => RefreshTimes();

        _manager.HistoryChanged += OnHistoryChanged;
        _state.PopupVisibilityChanged += OnPopupVisibilityChanged;
    }

    public void Dispose()
    {
        _searchTimer.Stop();
        _clockTimer.Stop();
        _manager.HistoryChanged -= OnHistoryChanged;
        _state.PopupVisibilityChanged -= OnPopupVisibilityChanged;
    }

    /// <summary>Item টা clipboard এ দেয়, সবার উপরে তোলে আর ১ সেকেন্ড "Copied" দেখায়।</summary>
    public async void Copy(ClipboardItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedItem = item;
        item.IsCopied = false;
        item.IsCopyFailed = false;

        // সফল হলে manager item টা উপরে তোলে; fail হলে কারণ log এ লেখা হয়
        string? error = await _manager.CopyToClipboardAsync(item.Id);
        bool ok = error is null;
        item.IsCopied = ok;
        item.IsCopyFailed = !ok;

        await Task.Delay(ok ? CopiedFeedback : FailedFeedback);
        item.IsCopied = false;
        item.IsCopyFailed = false;
    }

    /// <summary>Search box এ Enter: প্রথম result copy।</summary>
    public void CopyFirst()
    {
        // Debounce শেষ হওয়ার আগে Enter চাপলেও সর্বশেষ লেখা অনুযায়ী
        if (_searchTimer.IsEnabled)
        {
            _searchTimer.Stop();
            ApplyFilter();
        }

        if (Items.Count > 0)
        {
            Copy(Items[0]);
        }
    }

    /// <summary>Item মুছে পরের item টা বাছাই করে রাখে (keyboard দিয়ে পরপর মোছার জন্য)।</summary>
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

    partial void OnFilterChanged(ClipboardFilter value) => ApplyFilter();

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        // যেকোনো thread থেকে আসতে পারে; একবারই UI thread এ পাঠানো হয়
        if (Interlocked.Exchange(ref _dirty, 1) == 0 && _state.IsPopupVisible)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshIfDirty);
        }
    }

    private void OnPopupVisibilityChanged(object? sender, bool visible)
    {
        if (visible)
        {
            RefreshIfDirty();
            RefreshTimes();
            _clockTimer.Start();
            return;
        }

        // লুকানো অবস্থায় কোনো timer চলবে না; বাকি থাকা search এখনই বসানো হয়
        _clockTimer.Stop();
        if (_searchTimer.IsEnabled)
        {
            _searchTimer.Stop();
            ApplyFilter();
        }
    }

    private void RefreshIfDirty()
    {
        if (_state.IsPopupVisible && Volatile.Read(ref _dirty) == 1)
        {
            Refresh();
        }
    }

    /// <summary>Manager এর history থেকে item ViewModel গুলো মেলায় (পুরনো ViewModel আবার ব্যবহার হয়)।</summary>
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

    private void ApplyFilter()
    {
        string query = SearchText.Trim();

        var target = _all.Where(vm => Filter switch
        {
            ClipboardFilter.Text => vm.IsText,
            ClipboardFilter.Images => vm.IsImage,
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

        if (SelectedItem is not null && !Items.Contains(SelectedItem))
        {
            SelectedItem = null;
        }
    }

    /// <summary>
    /// পুরো collection বদলে না দিয়ে শুধু Move/Insert/Remove করে, যাতে scroll আর selection
    /// না হারায় আর virtualized container গুলো আবার বানাতে না হয়।
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
