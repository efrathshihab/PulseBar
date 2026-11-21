# Changelog

Each release's notes come from its section below (`## [x.y.z]`). The release workflow and
`Scripts/build-installer.ps1` copy that section into the installer and the GitHub Release.

## [0.9.1]

Test release for checking automatic updates.

## [0.9.0]

Test release for checking the installer.

## [1.0.0]

First release.

- Tray icon with live CPU % and a Fluent popup next to the tray: Overview, Sound, Clipboard and Settings tabs
- Overview: live CPU, GPU (usage, VRAM, temperature), RAM, drives, CPU temperature and network up/down, updated every second
- Taskbar overlay with live values next to the tray or the widgets (Compact or Balanced), colored by threshold; it never covers app buttons and shrinks to Compact or hides when the taskbar is full
- Sound: per-app volume and mute, output device switching and master volume
- Clipboard history for text and images with filters, search, copy and delete, and a retention of 1 hour, 24 hours or 7 days
- Clipboard panel on Ctrl+Alt+V with auto-paste (Enter pastes, Shift+Enter copies)
- Screenshots: Win+Shift+S captures and new Screenshots folder images go to history, plus an area-capture hotkey (Ctrl+Alt+S)
- Settings: System/Light/Dark theme, launch at login (normal or with admin for CPU temperature), customizable hotkeys
- Installer and automatic updates from GitHub Releases
