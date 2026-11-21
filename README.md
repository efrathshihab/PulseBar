# PulseBar

A system tray utility for Windows 10/11: live system stats, per-app volume, clipboard history and screenshots, all from the tray.

## Install
1. Download `PulseBarApp-win-Setup.exe` from the latest [Release](https://github.com/efrathshihab/PulseBar/releases/latest).
2. Run it. PulseBar installs for your user only (no admin needed) and adds a Start menu shortcut.
3. The installer isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC".
   Click **More info → Run anyway**.

PulseBar updates itself from GitHub Releases. It installs to `%LocalAppData%\PulseBarApp`, and
keeps your settings, clipboard history and logs in `%LocalAppData%\PulseBar`, which uninstalling
and updating never delete.

## Requirements (development)
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

## Installer
```powershell
./Scripts/build-installer.ps1            # version from PulseBar.csproj
./Scripts/build-installer.ps1 -Version 1.0.1
```
Output goes to `releases/`. Pushing a `v*` tag (e.g. `v1.0.1`) runs `.github/workflows/release.yml`,
which builds the installer and publishes a GitHub Release with the notes from `CHANGELOG.md`.

## Stack
C#, .NET 10, WPF, [WPF-UI](https://github.com/lepoco/wpfui), [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon),
NAudio, LibreHardwareMonitorLib, [Velopack](https://velopack.io)
