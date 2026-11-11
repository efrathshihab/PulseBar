# PulseBar: Windows system tray utility

Windows 10/11 এর জন্য একটা tray app, macOS এর NexaBar এর Windows version।

## Stack
C#, .NET 10, WPF
- H.NotifyIcon.Wpf: tray icon
- WPF-UI (lepoco): Fluent/Mica design
- NAudio: audio (WASAPI)
- LibreHardwareMonitorLib: CPU temperature
- Velopack: installer + auto-update (GitHub Releases)

## Features (v1.0)
- Tray icon এ click করলে tray এর পাশে Fluent popup খুলবে, ৪টা tab: Overview, Sound, Clipboard, Settings
- Overview: live CPU, GPU (usage, VRAM, temp), RAM, SSD, CPU temp, network up/down, প্রতি ১ সেকেন্ডে update
- Sound: per-app volume/mute (WASAPI audio sessions), output device switch, master volume
- Clipboard: text + image history, All/Text/Images filter, search, copy/delete, 24h retention,
  Ctrl+Shift+V global hotkey দিয়ে আলাদা panel (Enter = copy, Esc = বন্ধ)
- Screenshot: Win+Shift+S এর ছবি history তে যোগ হবে, Screenshots folder watcher, নিজস্ব area-capture hotkey
- Settings: System/Light/Dark theme, launch at login, Compact/Balanced mode
- Taskbar overlay: tray এর ^ arrow এর বাম পাশে taskbar এর উপর 2x2 live মান (CPU, GPU, RAM, GPU temp),
  threshold অনুযায়ী রং, click করলে popup; tray menu তে "Show on taskbar" দিয়ে চালু/বন্ধ

## Folder structure
```
PulseBar/
├─ src/PulseBar/
│  ├─ App.xaml / App.xaml.cs
│  ├─ Views/        ← popup, tab গুলো
│  ├─ ViewModels/   ← প্রতিটা page এর ViewModel (CommunityToolkit.Mvvm)
│  ├─ Services/     ← SystemMonitor, ClipboardManager, AudioController, HotKeyManager, ScreenshotWatcher, UpdateManager
│  ├─ Models/       ← AppState, settings
│  ├─ Utilities/
│  └─ Assets/       ← icon, ছবি
├─ Scripts/
├─ .github/workflows/
├─ AGENTS.md
├─ README.md
└─ .gitignore
```

## Rules
- Namespace: PulseBar
- প্রতিটা Service আলাদা class হবে, UI থেকে আলাদা থাকবে (MVVM ধাঁচে)
- Admin permission না পেলে বা কোনো sensor কাজ না করলে app crash করবে না, "N/A" দেখাবে
- CPU temperature (LibreHardwareMonitorLib) এর জন্য admin + PawnIO driver দুটোই লাগে; না থাকলে শুরুতে একবার চেষ্টা করে থামবে, "N/A" আর tooltip এ কারণ দেখাবে। app.manifest এ requireAdministrator দেবে না
- নিজের app যেন খুব কম CPU/RAM খায়
- একবারে শুধু আজকের task এর কাজ করবে, বাড়তি feature যোগ করবে না
- একসাথে একটাই PulseBar চলবে
- কাজ শেষে বলবে কোন command দিয়ে চালাতে হবে আর কী কী test করতে হবে
- Commit message এ কোনো Co-Authored-By, Claude-Session বা AI attribution লাইন দেবে না

## 15 দিনের plan
| দিন | কাজ | দিন শেষে যা থাকবে |
|---|---|---|
| ১ | WPF project setup, tray icon, right-click এ Quit, .gitignore, README | Tray এ icon, Quit কাজ করে |
| ২ | WPF-UI দিয়ে popup: tray এর পাশে খুলবে, বাইরে click করলে বন্ধ; ৪টা tab skeleton | খালি ৪টা tab |
| ৩ | SystemMonitor: CPU, RAM, SSD, প্রতি সেকেন্ডে update | Live সংখ্যা |
| ৪ | Network speed + CPU temperature (LibreHardwareMonitorLib) | সব metric পড়া যায় |
| ৫ | Overview UI: ring gauge, card, AppState; tray icon এ CPU % এঁকে দেখানো | Overview সম্পূর্ণ |
| ৬ | ClipboardManager: AddClipboardFormatListener, text/image ধরা, save (JSON/SQLite) | History জমা হয় |
| ৭ | Clipboard tab: filter, search, copy/delete, clear all, 24h retention | Clipboard tab সম্পূর্ণ |
| ৮ | HotKeyManager: Ctrl+Shift+V panel, Enter/Esc | Hotkey কাজ করে |
| ৯ | AudioController: app audio session list, per-app volume/mute | Per-app volume |
| ১০ | Output device list + switch (IPolicyConfig), master volume, Sound tab UI | Sound tab সম্পূর্ণ |
| ১১ | Screenshot: Win+Shift+S → history, folder watcher, area-capture hotkey | Screenshot feature |
| ১২ | Settings: theme, launch at login (Registry HKCU Run), Compact/Balanced, settings save | Settings সম্পূর্ণ |
| ১৩ | Velopack: Setup.exe, auto-update, GitHub Actions release workflow | Auto-update |
| ১৪ | Fresh PC/VM এ test, bug fix, নিজের CPU/RAM usage চেক | Stable build |
| ১৫ | Buffer: বাকি কাজ, README, screenshot, v1.0 release | Public release |

> Taskbar overlay দিন ৫ এর পরে বাড়তি যোগ হয়েছে (plan এ ছিল না)। "Show on taskbar" এখন শুধু memory তে থাকে, দিন ১২ এর settings এ save হবে।
