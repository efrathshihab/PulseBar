using PulseBar.Models;
using PulseBar.Utilities;
using Velopack;
using Velopack.Sources;

namespace PulseBar.Services;

/// <summary>
/// Auto-update from GitHub Releases through Velopack (stable releases only). Checks 60 seconds after
/// start and then every 6 hours (when <see cref="AppSettings.AutoCheckUpdates"/> is on), downloads a
/// new version in the background and keeps it ready. It is applied when the user clicks Restart,
/// otherwise when PulseBar quits. When the app isn't installed (dotnet run, Debug builds) everything
/// here is off. Network or GitHub errors are only logged. Never throws.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepoUrl = "https://github.com/efrathshihab/PulseBar";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly AppSettings _settings;
    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private Timer? _timer;
    private VelopackAsset? _ready;
    private bool _restartRequested;
    private volatile bool _disposed;

    public UpdateService(AppSettings settings)
    {
        _settings = settings;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (manager.IsInstalled)
            {
                _manager = manager;
                CurrentVersion = manager.CurrentVersion?.ToString();
            }
            else
            {
                Log.Info("Updates: not installed (dev build), updates are disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Updates: could not create the update manager; updates are disabled", ex);
        }
    }

    /// <summary>Raised (from any thread) when checking starts/ends or the state changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised (from a background thread) once a new version is downloaded and ready.</summary>
    public event EventHandler? UpdateReady;

    /// <summary>False when not installed by Velopack.</summary>
    public bool IsEnabled => _manager is not null;

    public string? CurrentVersion { get; }

    public bool IsChecking { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Result of the last check, e.g. "You're up to date"; null before the first check.</summary>
    public string? LastResult { get; private set; }

    /// <summary>The downloaded version waiting to be applied, e.g. "1.0.1"; null if none.</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>Release notes (markdown) of the ready version.</summary>
    public string? ReadyNotesMarkdown { get; private set; }

    /// <summary>Starts the periodic checks. UI thread, once.</summary>
    public void Start()
    {
        if (_manager is null || _timer is not null)
        {
            return;
        }

        _timer = new Timer(_ =>
        {
            if (_settings.AutoCheckUpdates)
            {
                _ = CheckAsync();
            }
        }, null, FirstCheckDelay, CheckInterval);
    }

    /// <summary>Checks now and downloads a new version if there is one. Does nothing while a check is running.</summary>
    public async Task CheckAsync()
    {
        if (_manager is not { } manager || _disposed || !await _checkGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        IsChecking = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            LastChecked = DateTimeOffset.Now;
            if (info is null)
            {
                LastResult = "You're up to date";
                Log.Info("Updates: up to date");
                return;
            }

            var target = info.TargetFullRelease;
            if (_ready is not null && _ready.Version == target.Version)
            {
                LastResult = $"Update {ReadyVersion} is ready";
                return;
            }

            Log.Info($"Updates: downloading {target.Version}");
            LastResult = $"Downloading {target.Version}…";
            StateChanged?.Invoke(this, EventArgs.Empty);

            await manager.DownloadUpdatesAsync(info).ConfigureAwait(false);

            _ready = target;
            ReadyVersion = target.Version.ToString();
            ReadyNotesMarkdown = target.NotesMarkdown;
            LastResult = $"Update {ReadyVersion} is ready";
            Log.Info($"Updates: {ReadyVersion} downloaded and ready");
            UpdateReady?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // No internet, GitHub rate limit or outage: try again at the next check
            LastChecked = DateTimeOffset.Now;
            LastResult = "Couldn't check for updates";
            Log.Error("Updates: check failed", ex);
        }
        finally
        {
            IsChecking = false;
            _checkGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Starts Velopack's updater, which waits for PulseBar to exit, applies the update and starts the new
    /// version. The caller must then quit normally. False if nothing is ready or it couldn't start.
    /// </summary>
    public bool PrepareRestart()
    {
        if (_manager is not { } manager || _ready is not { } ready)
        {
            return false;
        }

        try
        {
            manager.WaitExitThenApplyUpdates(ready, silent: false, restart: true, Array.Empty<string>());
            _restartRequested = true;
            Log.Info($"Updates: restarting to apply {ready.Version}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Updates: could not start the updater", ex);
            return false;
        }
    }

    /// <summary>On quit: if an update is ready (and Restart wasn't used), apply it silently after exit without restarting.</summary>
    public void ApplyOnExit()
    {
        if (_manager is not { } manager || _ready is not { } ready || _restartRequested)
        {
            return;
        }

        try
        {
            manager.WaitExitThenApplyUpdates(ready, silent: true, restart: false, Array.Empty<string>());
            Log.Info($"Updates: {ready.Version} will be applied after exit");
        }
        catch (Exception ex)
        {
            Log.Error("Updates: could not apply on exit", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
