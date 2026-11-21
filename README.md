# PulseBar

A system tray utility for Windows 10/11: live system stats, per-app volume, clipboard history and screenshots, all from the tray.

> Currently at day 1: shows an icon in the tray, and right-click lets you Quit.

## Requirements
- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Run
```powershell
dotnet run --project src/PulseBar
```

## Build
```powershell
dotnet build PulseBar.slnx -c Release
```

## Stack
C#, .NET 10, WPF, [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon)
