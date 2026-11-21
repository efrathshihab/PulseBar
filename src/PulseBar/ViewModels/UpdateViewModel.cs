using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Services;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>The popup's "Update ready" banner and the update part of Settings > About.</summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    public const string DisabledText = "Updates are disabled in dev builds";

    private readonly UpdateService _service;
    private readonly Dispatcher _dispatcher;
    private readonly Action _restart;
    private int _refreshQueued;

    /// <param name="restart">Applies the ready update and restarts PulseBar.</param>
    public UpdateViewModel(UpdateService service, Dispatcher dispatcher, Action restart)
    {
        _service = service;
        _dispatcher = dispatcher;
        _restart = restart;
        _service.StateChanged += OnServiceChanged;
        _service.UpdateReady += OnServiceChanged;
        Refresh();
    }

    public bool IsEnabled => _service.IsEnabled;

    [ObservableProperty]
    public partial bool IsUpdateReady { get; private set; }

    /// <summary>e.g. "Update 1.0.1 ready".</summary>
    [ObservableProperty]
    public partial string BannerText { get; private set; } = "";

    /// <summary>About: when it was last checked and the result, or why updates are off.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial bool IsChecking { get; private set; }

    /// <summary>Release notes as plain text.</summary>
    public string ReleaseNotesText => MarkdownText.ToPlain(_service.ReadyNotesMarkdown);

    public string? ReadyVersion => _service.ReadyVersion;

    private bool CanCheckNow => IsEnabled && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNowAsync() => Task.Run(_service.CheckAsync);

    [RelayCommand]
    private void Restart() => _restart();

    public void Dispose()
    {
        _service.StateChanged -= OnServiceChanged;
        _service.UpdateReady -= OnServiceChanged;
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        // From any thread; posted to the UI thread once
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            });
        }
    }

    private void Refresh()
    {
        IsChecking = _service.IsChecking;
        IsUpdateReady = _service.ReadyVersion is not null;
        BannerText = IsUpdateReady ? $"Update {_service.ReadyVersion} ready" : "";
        OnPropertyChanged(nameof(ReleaseNotesText));
        OnPropertyChanged(nameof(ReadyVersion));

        if (!_service.IsEnabled)
        {
            StatusText = DisabledText;
        }
        else if (_service.IsChecking)
        {
            StatusText = "Checking for updates…";
        }
        else if (_service.LastChecked is { } checkedAt)
        {
            StatusText = $"Last checked {checkedAt.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture)}" +
                (_service.LastResult is { } result ? $" · {result}" : "");
        }
        else
        {
            StatusText = "Not checked yet";
        }
    }
}
