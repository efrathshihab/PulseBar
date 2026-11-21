using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>One ComboBox option.</summary>
public sealed record Option<T>(T Value, string Label);

/// <summary>
/// The Settings tab. Simple values are bound directly to <see cref="Settings"/>; App applies their changes
/// to the services right away. Launch at login and hotkeys can fail, so they are changed
/// through their services from here, and the setting changes only on success.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public const string GitHubUrl = "https://github.com/efrathshihab/PulseBar";

    private readonly StartupManager _startup;
    private readonly Action<string> _notify;
    private LaunchAtLogin _launchAtLogin;

    public SettingsViewModel(AppSettings settings, HotKeyBindings hotKeys, StartupManager startup, Action<string> notify)
    {
        Settings = settings;
        _startup = startup;
        _notify = notify;
        _launchAtLogin = settings.LaunchAtLogin;
        PanelHotKey = new HotKeySettingViewModel(hotKeys, HotKeyAction.ClipboardPanel, settings);
        ScreenshotHotKey = new HotKeySettingViewModel(hotKeys, HotKeyAction.Screenshot, settings);
        Settings.PropertyChanged += OnSettingsChanged;
        UpdateDebugWarning();

        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        VersionText = $"Version {version.Major}.{version.Minor}.{version.Build}" +
            (BuildInfo.DevLabel is { } dev ? $" ({dev})" : "");
    }

    public AppSettings Settings { get; }

    public HotKeySettingViewModel PanelHotKey { get; }

    public HotKeySettingViewModel ScreenshotHotKey { get; }

    public IReadOnlyList<Option<AppTheme>> ThemeOptions { get; } =
    [
        new(AppTheme.System, "System"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark"),
    ];

    public IReadOnlyList<Option<OverlayLayout>> LayoutOptions { get; } =
    [
        new(OverlayLayout.Compact, "Compact"),
        new(OverlayLayout.Balanced, "Balanced"),
    ];

    public IReadOnlyList<Option<ClipboardRetention>> RetentionOptions { get; } =
    [
        new(ClipboardRetention.OneHour, "1 hour"),
        new(ClipboardRetention.TwentyFourHours, "24 hours"),
        new(ClipboardRetention.SevenDays, "7 days"),
    ];

    public IReadOnlyList<Option<LaunchAtLogin>> LaunchOptions { get; } =
    [
        new(LaunchAtLogin.Off, "Off"),
        new(LaunchAtLogin.Normal, "Normal"),
        new(LaunchAtLogin.WithAdmin, "With admin"),
    ];

    public string VersionText { get; }

    /// <summary>
    /// The ComboBox value. Changing it starts updating the Run key/task; on failure or "No" in UAC it reverts.
    /// </summary>
    public LaunchAtLogin LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (_launchAtLogin == value || IsLaunchBusy)
            {
                return;
            }

            _launchAtLogin = value;
            OnPropertyChanged();
            _ = ApplyLaunchAsync(value);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLaunchEnabled))]
    public partial bool IsLaunchBusy { get; private set; }

    public bool IsLaunchEnabled => !IsLaunchBusy;

    /// <summary>Result of the last change (such as No in UAC); null if none.</summary>
    [ObservableProperty]
    public partial string? LaunchMessage { get; private set; }

    /// <summary>A problem found by the startup check (couldn't fix without admin); "Fix" tries again.</summary>
    [ObservableProperty]
    public partial string? LaunchIssue { get; private set; }

    [ObservableProperty]
    public partial bool ShowDebugWarning { get; private set; }

    /// <summary>Result of the startup check. UI thread.</summary>
    public void SetLaunchIssue(string? issue) => LaunchIssue = issue;

    [RelayCommand]
    private async Task FixLaunchAsync()
    {
        if (IsLaunchBusy)
        {
            return;
        }

        await ApplyLaunchAsync(_launchAtLogin);
    }

    [RelayCommand]
    private void OpenGitHub() => ShellOpen(GitHubUrl);

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
        }
        catch (Exception ex)
        {
            Log.Error("Logs folder", ex);
        }

        ShellOpen(Log.Directory);
    }

    /// <summary>All settings to default (after confirming). Launch at login becomes Off, which may show UAC.</summary>
    public async Task ResetToDefaultsAsync()
    {
        Log.Info("Settings: reset to defaults");
        Settings.ResetSimpleValues();
        PanelHotKey.ResetToDefault();
        ScreenshotHotKey.ResetToDefault();

        if (_launchAtLogin != LaunchAtLogin.Off && !IsLaunchBusy)
        {
            _launchAtLogin = LaunchAtLogin.Off;
            OnPropertyChanged(nameof(LaunchAtLogin));
            await ApplyLaunchAsync(LaunchAtLogin.Off);
        }
    }

    public void Dispose()
    {
        Settings.PropertyChanged -= OnSettingsChanged;
        PanelHotKey.Dispose();
        ScreenshotHotKey.Dispose();
    }

    private async Task ApplyLaunchAsync(LaunchAtLogin mode)
    {
        var previous = Settings.LaunchAtLogin;
        IsLaunchBusy = true;
        LaunchMessage = null;
        try
        {
            // Registry, COM and schtasks stay off the UI thread
            var result = await Task.Run(() => _startup.ApplyAsync(mode));
            if (result.Outcome == StartupApplyOutcome.Ok)
            {
                Settings.LaunchAtLogin = mode;
                LaunchIssue = null;
                Log.Info($"Launch at login: {mode}");
                return;
            }

            // Revert; the Run key and task are unchanged
            _launchAtLogin = previous;
            OnPropertyChanged(nameof(LaunchAtLogin));
            LaunchMessage = result.Message ?? "Couldn't change it";
            _notify(LaunchMessage);
        }
        finally
        {
            IsLaunchBusy = false;
            UpdateDebugWarning();
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.LaunchAtLogin))
        {
            UpdateDebugWarning();
        }
    }

    private void UpdateDebugWarning() =>
        ShowDebugWarning = BuildInfo.IsDebug && Settings.LaunchAtLogin != LaunchAtLogin.Off;

    private static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Open {target}", ex);
        }
    }
}
