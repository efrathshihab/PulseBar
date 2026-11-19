using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Services;

namespace PulseBar.ViewModels;

/// <summary>
/// Sound tab: per-app volume আর mute। Popup লুকানো থাকলে UI কিছুই বদলায় না; controller এর
/// event শুধু "বদলেছে" চিহ্ন দেয়, popup খুললে তখন list মেলানো হয়।
/// </summary>
public sealed partial class SoundViewModel : ObservableObject, IDisposable
{
    private readonly AudioController _controller;
    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, AudioAppViewModel> _rows = [];
    private int _dirty = 1;

    public SoundViewModel(AudioController controller, AppState state, Dispatcher dispatcher)
    {
        _controller = controller;
        _state = state;
        _dispatcher = dispatcher;

        _controller.AppsChanged += OnAppsChanged;
        _state.PopupVisibilityChanged += OnPopupVisibilityChanged;
    }

    /// <summary>আগে System sounds, তারপর নাম অনুযায়ী।</summary>
    public ObservableCollection<AudioAppViewModel> Apps { get; } = [];

    /// <summary>System sounds ছাড়া কোনো app নেই।</summary>
    [ObservableProperty]
    public partial bool IsEmptyVisible { get; set; } = true;

    public void Dispose()
    {
        _controller.AppsChanged -= OnAppsChanged;
        _state.PopupVisibilityChanged -= OnPopupVisibilityChanged;
    }

    private void OnAppsChanged(object? sender, EventArgs e)
    {
        // Audio thread থেকে; একবারই UI thread এ পাঠানো হয়
        if (Interlocked.Exchange(ref _dirty, 1) == 0 && _state.IsPopupVisible)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, SyncIfDirty);
        }
    }

    private void OnPopupVisibilityChanged(object? sender, bool visible)
    {
        if (visible)
        {
            SyncIfDirty();
        }
        else
        {
            foreach (var row in _rows.Values)
            {
                row.Flush();
            }
        }
    }

    private void SyncIfDirty()
    {
        if (_state.IsPopupVisible && Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            Sync(_controller.GetApps());
        }
    }

    /// <summary>পুরনো row এর ViewModel আবার ব্যবহার করে শুধু Move/Insert/Remove, যাতে slider না হারায়।</summary>
    private void Sync(IReadOnlyList<AudioAppSession> apps)
    {
        var target = new List<AudioAppViewModel>(apps.Count);
        foreach (var app in apps)
        {
            if (_rows.TryGetValue(app.Key, out var row))
            {
                row.Apply(app);
            }
            else
            {
                row = new AudioAppViewModel(app, _controller, _dispatcher);
                _rows[app.Key] = row;
            }

            target.Add(row);
        }

        var keep = new HashSet<AudioAppViewModel>(target);
        foreach (string key in _rows.Keys.Where(key => !keep.Contains(_rows[key])).ToList())
        {
            _rows.Remove(key);
        }

        for (int i = Apps.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Apps[i]))
            {
                Apps.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < Apps.Count && ReferenceEquals(Apps[i], target[i]))
            {
                continue;
            }

            int existing = Apps.IndexOf(target[i]);
            if (existing >= 0)
            {
                Apps.Move(existing, i);
            }
            else
            {
                Apps.Insert(i, target[i]);
            }
        }

        IsEmptyVisible = !target.Any(row => !row.IsSystemSounds);
    }
}
