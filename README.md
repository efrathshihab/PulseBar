# PulseBar

Windows 10/11 এর জন্য একটা system tray utility: live system stats, per-app volume, clipboard history আর screenshot, সব tray থেকে।

> এখন দিন ১: tray এ icon দেখায়, right-click করে Quit করা যায়।

## দরকার
- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## চালানো
```powershell
dotnet run --project src/PulseBar
```

## Build
```powershell
dotnet build PulseBar.slnx -c Release
```

## Stack
C#, .NET 10, WPF, [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon)
