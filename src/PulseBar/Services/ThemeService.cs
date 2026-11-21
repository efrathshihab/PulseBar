using System.Windows;
using System.Windows.Interop;
using PulseBar.Models;
using PulseBar.Utilities;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PulseBar.Services;

/// <summary>
/// App এর theme: System হলে Windows এর app theme অনুসরণ করে (SystemThemeWatcher), Light/Dark হলে
/// সেটাই বসায়। নিবন্ধিত সব window এর Mica সাথে সাথে নতুন রঙে যায়। শুধু UI thread থেকে।
/// </summary>
public sealed class ThemeService
{
    private readonly List<Window> _windows = [];
    private bool _watching;

    public AppTheme Theme { get; private set; } = AppTheme.System;

    /// <summary>যে window গুলোর রং theme এর সাথে বদলাবে।</summary>
    public void Register(Window window)
    {
        _windows.Add(window);
        if (_watching)
        {
            SystemThemeWatcher.Watch(window);
        }
        else
        {
            UpdateBackground(window);
        }
    }

    public void Apply(AppTheme theme)
    {
        Theme = theme;
        try
        {
            if (theme == AppTheme.System)
            {
                ApplicationThemeManager.ApplySystemTheme();
                if (!_watching)
                {
                    _watching = true;
                    foreach (var window in _windows)
                    {
                        SystemThemeWatcher.Watch(window);
                    }
                }
            }
            else
            {
                if (_watching)
                {
                    _watching = false;
                    foreach (var window in _windows)
                    {
                        TryUnwatch(window);
                    }
                }

                ApplicationThemeManager.Apply(
                    theme == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
                    WindowBackdropType.Mica,
                    updateAccent: true);
            }

            foreach (var window in _windows)
            {
                UpdateBackground(window);
            }

            Log.Info($"Theme: {theme} ({ApplicationThemeManager.GetAppTheme()})");
        }
        catch (Exception ex)
        {
            Log.Error($"Theme {theme}", ex);
        }
    }

    private static void UpdateBackground(Window window)
    {
        // Handle না থাকলে প্রথম Show এর সময় FluentWindow নিজেই এখনকার theme নেয়
        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            WindowBackgroundManager.UpdateBackground(window, ApplicationThemeManager.GetAppTheme(), WindowBackdropType.Mica);
        }
        catch (Exception ex)
        {
            Log.Error("Window background", ex);
        }
    }

    private static void TryUnwatch(Window window)
    {
        try
        {
            SystemThemeWatcher.UnWatch(window);
        }
        catch (Exception ex)
        {
            Log.Error("Theme unwatch", ex);
        }
    }
}
