# PulseBar: Windows system tray utility

A tray app for Windows 10/11, the Windows version of macOS's NexaBar.

## Stack
C#, .NET 10, WPF
- H.NotifyIcon.Wpf: tray icon
- WPF-UI (lepoco): Fluent/Mica design
- NAudio: audio (WASAPI)
- LibreHardwareMonitorLib: CPU temperature
- Velopack: installer + auto-update (GitHub Releases)

## Features (v1.0)
- Clicking the tray icon opens a Fluent popup next to the tray, with 4 tabs: Overview, Sound, Clipboard, Settings
- Overview: live CPU, GPU (usage, VRAM, temp), RAM, SSD, CPU temp, network up/down, updated every second
- Sound: per-app volume/mute (WASAPI audio sessions), output device switch, master volume
- Clipboard: text + image history, All/Text/Images filter, search, copy/delete, 24h retention,
  a separate panel via the Ctrl+Alt+V global hotkey (Enter = paste, Shift+Enter = copy, Esc = close)
- Screenshot: Win+Shift+S images are added to history, Screenshots folder watcher, own area-capture hotkey
- Settings: System/Light/Dark theme, launch at login, Compact/Balanced mode
- Taskbar overlay: 2x2 live values (CPU, GPU, RAM, GPU temp) on the taskbar, left of the tray's ^ arrow,
  colored by threshold, click opens the popup; toggled with "Show on taskbar" in the tray menu

## Folder structure
```
PulseBar/
├─ src/PulseBar/
│  ├─ App.xaml / App.xaml.cs
│  ├─ Views/        ← popup, tabs
│  ├─ ViewModels/   ← ViewModel for each page (CommunityToolkit.Mvvm)
│  ├─ Services/     ← SystemMonitor, ClipboardManager, AudioController, HotKeyManager, ScreenshotWatcher, UpdateManager
│  ├─ Models/       ← AppState, settings
│  ├─ Utilities/
│  └─ Assets/       ← icons, images
├─ Scripts/
├─ .github/workflows/
├─ AGENTS.md
├─ README.md
└─ .gitignore
```

## Rules
- Namespace: PulseBar
- Each Service is a separate class, kept apart from the UI (MVVM style)
- If admin permission is missing or a sensor doesn't work, the app must not crash; show "N/A"
- CPU temperature (LibreHardwareMonitorLib) needs both admin and the PawnIO driver; without them, try once at startup and stop, showing "N/A" and the reason in the tooltip. Don't put requireAdministrator in app.manifest
- The app itself must use very little CPU/RAM
- Work only on today's task at a time; don't add extra features
- Only one PulseBar runs at a time
- PulseBar never deletes, moves or changes any file in the user's Screenshots folder; it only reads them.
  History holds PulseBar's own copy (%LocalAppData%\PulseBar\clipboard\images), and 24h
  retention and Delete/Clear all apply only to that copy
- When done, say which command runs it and what needs to be tested
- No Co-Authored-By, Claude-Session or AI attribution lines in commit messages
- Don't touch the user's mouse, keyboard, screen or clipboard while testing.
  If a UI test is needed, tell the user what to test.
- All UI text, code comments, log messages, docs and commit messages must be in English.

## 15-day plan
| Day | Work | At the end of the day |
|---|---|---|
| 1 | WPF project setup, tray icon, Quit on right-click, .gitignore, README | Icon in the tray, Quit works |
| 2 | Popup with WPF-UI: opens next to the tray, closes on outside click; 4 tab skeletons | 4 empty tabs |
| 3 | SystemMonitor: CPU, RAM, SSD, updated every second | Live numbers |
| 4 | Network speed + CPU temperature (LibreHardwareMonitorLib) | All metrics readable |
| 5 | Overview UI: ring gauges, cards, AppState; CPU % drawn on the tray icon | Overview complete |
| 6 | ClipboardManager: AddClipboardFormatListener, capture text/images, save (JSON/SQLite) | History is collected |
| 7 | Clipboard tab: filter, search, copy/delete, clear all, 24h retention | Clipboard tab complete |
| 8 | HotKeyManager: Ctrl+Alt+V panel, Enter/Esc, auto-paste | Hotkey works |
| 9 | AudioController: app audio session list, per-app volume/mute | Per-app volume |
| 10 | Output device list + switch (IPolicyConfig), master volume, Sound tab UI | Sound tab complete |
| 11 | Screenshot: Win+Shift+S → history, folder watcher, area-capture hotkey | Screenshot feature |
| 12 | Settings: theme, launch at login (Registry HKCU Run), Compact/Balanced, settings save | Settings complete |
| 13 | Velopack: Setup.exe, auto-update, GitHub Actions release workflow | Auto-update |
| 14 | Test on a fresh PC/VM, bug fixes, check own CPU/RAM usage | Stable build |
| 15 | Buffer: remaining work, README, screenshots, v1.0 release | Public release |

> The taskbar overlay was added after day 5 (it wasn't in the plan). "Show on taskbar" and the Compact/Balanced layout are saved in settings.json since day 12.
