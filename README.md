# Flydigi BS1 Controller

A small Windows tray app that controls the **Flydigi BS1** laptop cooler over Bluetooth, as a lighter replacement for Flydigi Space Station.

[فارسی](#فارسی)

## Features

- **Smart fan:** fan speed follows CPU/GPU temperature on an editable curve, or runs at a fixed speed.
- **Quieter during video or music** (optional).
- **Lights:** static, breathe, twinkle, fade, wave, or a steady color that follows fan speed (blue when quiet, red at full speed). Brightness and colors are adjustable, with optional per-zone colors.
- **Turns the cooler off** when Windows shuts down or sleeps, and turns it back on at logon.
- Connects automatically when the cooler is in range.

## Install

1. Download `BS1Cooler-Setup-x.y.z.exe` from [Releases](../../releases) and run it (English or Persian).
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

---

## فارسی

یه برنامه‌ی کوچیک برای ویندوز که کول‌پد **Flydigi BS1** رو با بلوتوث کنترل می‌کنه. جایگزین سبک‌تر برای Flydigi Space Station.

**امکانات**

- **فن هوشمند:** سرعت فن با دمای CPU و GPU طبق یه منحنی قابل تنظیم عوض می‌شه، یا روی یه سرعت ثابت می‌مونه.
- **کم‌صداتر موقع پخش فیلم یا موزیک** (اختیاری).
- **نور:** ثابت، تنفسی، چشمک، محو، موج، یا یه رنگ ثابت که با سرعت فن عوض می‌شه (آبی وقتی آرومه، قرمز با حداکثر سرعت). روشنایی و رنگ‌ها قابل تنظیمن و هر ناحیه می‌تونه رنگ جدا داشته باشه.
- **خاموش کردن کولر** موقع خاموش شدن یا Sleep ویندوز، و روشن کردنش بعد از ورود به ویندوز.
- وقتی کولر در دسترس باشه، خودش وصل می‌شه.

**نصب**

1. فایل `BS1Cooler-Setup-x.y.z.exe` رو از [Releases](../../releases) دانلود و اجرا کنید (فارسی یا انگلیسی).
2. Flydigi Space Station رو ببندید و از Startup حذفش کنید. هم‌زمان فقط یه برنامه می‌تونه به کولر وصل بشه.

برنامه با دسترسی ادمین اجرا می‌شه، چون خوندن دمای CPU و GPU بهش نیاز داره. نصب‌کننده یه task می‌سازه که موقع ورود به ویندوز، بدون پنجره‌ی UAC اجراش می‌کنه.

**نکته‌ها**

- ویندوز ۱۰ (نسخه‌ی 2004) یا جدیدتر، ۶۴ بیتی، با بلوتوث LE.
- دما با [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) خونده می‌شه. Windows Defender روی بعضی سیستم‌ها به درایور WinRing0 اون گیر می‌ده؛ اگه این‌طور شد، فن کار می‌کنه ولی ممکنه دما خونده نشه.
- این پروژه غیررسمیه و ربطی به شرکت Flydigi نداره.
