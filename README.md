# Flydigi BS1 Controller

A small Windows tray app that controls the **Flydigi BS1** laptop cooler over Bluetooth, as a lighter replacement for Flydigi Space Station.

## Features

- **Smart fan:** fan speed follows CPU/GPU temperature on an editable curve, or runs at a fixed speed.
- **Quieter during video or music** (optional).
- **Lights:** static, breathe, twinkle, fade, wave, or a steady color that follows fan speed (blue when quiet, red at full speed). Brightness and colors are adjustable, with optional per-zone colors.
- **Turns the cooler off** when Windows shuts down or sleeps, and turns it back on at logon.
- Connects automatically when the cooler is in range.

## Install

1. Download `BS1Cooler-Setup-x.y.z.exe` from [Releases](../../releases) and run it.
2. Close Flydigi Space Station and remove it from startup. Only one app can talk to the cooler at a time.

The app runs with admin rights because reading CPU/GPU temperatures needs them. Setup adds a logon task that starts it elevated without a UAC prompt each time.

## Notes

- Windows 10 (2004) or newer, x64, with Bluetooth LE.
- Temperatures are read with [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor). Its WinRing0 driver is flagged by Windows Defender on some PCs; if that happens, the fan still works but the temperature reading may not.
- This is an unofficial project and is not affiliated with Flydigi.

## Build from source

Needs the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet build cooler -c Release        # app only
installer\build.ps1                  # self-contained installer -> installer\out\
```

## License

MIT

