using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace Bs1Cooler.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest"))  // + the sealed firmware opens and is ours
        {
            int r = SelfTest.Run();
            if (r == 0 && (Flasher.CustomVersion() < 14 || Flasher.Image(Flasher.Edition.Official).Length < 100_000)) { Console.WriteLine("FAIL: sealed firmware"); return 1; }
            if (r == 0 && Tx.Collisions() is { Length: > 0 } c) { Console.WriteLine("FAIL: shared translations: " + string.Join("; ", c)); return 1; }
            return r;
        }
        if (args.Contains("--seal-firmware"))  // dev: plain images (never in git) -> sealed ones the build embeds
        {
            string fw = Path.GetFullPath(args.SkipWhile(a => a != "--seal-firmware").Skip(1).FirstOrDefault() ?? "firmware");  // run from the repo root
            foreach (var (plain, name) in new[] { ("bs1_0.0.6.02.bin", "official.bin.sealed"), (Path.Combine("custom", "out", "bs1_custom.bin"), "custom.bin.sealed") })
            {
                var data = File.ReadAllBytes(Path.Combine(fw, plain));
                var sealedData = FirmwareBox.Seal(data);
                if (!FirmwareBox.Open(sealedData).AsSpan().SequenceEqual(data)) return 1;
                File.WriteAllBytes(Path.Combine(fw, name), sealedData);
                Console.WriteLine($"{name}: {data.Length} bytes sealed");
            }
            return 0;
        }
        if (args.Contains("--signals"))  // dev: quiet-mode inputs now, and who holds a microphone
        {
#if !WINDOWS
            var t = OperatingSystem.IsMacOS() ? (ITemps)new MacTemps() : new LinuxTemps();
            t.Read(); Thread.Sleep(500); t.Read();
            Console.WriteLine($"{(OperatingSystem.IsMacOS() ? MacSignals.Read() : LinuxSignals.Read())}; load={t.Load:0}%; parts={string.Join(", ", t.Parts.Select(p => $"{p.Kind} {p.Name} {p.Temp:0}"))}");
#else
            Console.WriteLine($"{WinSignals.Read()}; sound={WinMedia.SoundPlaying()}");
            WinMedia.Trace = []; WinMedia.MicInUse();
            foreach (var pid in WinMedia.Trace) Console.WriteLine(pid == 0 ? "mic device checked" : $"mic: {pid} {System.Diagnostics.Process.GetProcessById((int)pid).ProcessName}");
#endif
            return 0;
        }
        bool smoke = args.Contains("--smoke");  // dev: open every page, no device, exit; catches XAML/style errors
        using var single = new Mutex(true, smoke ? @"Local\Bs1CoolerSmoke" : @"Local\Bs1Cooler", out bool first);
        if (!first) return 0;  // ponytail: second launch just exits; the tray icon is the way back in

        Controller.DevHooks = args.Contains("--dev");
        var ctl = new Controller(OsPlatform());
        if (OperatingSystem.IsWindows())
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend) ctl.Suspend();
                else if (e.Mode == Microsoft.Win32.PowerModes.Resume) ctl.Resume();
            };

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .AfterSetup(b => Setup((IClassicDesktopStyleApplicationLifetime)b.Instance!.ApplicationLifetime!, ctl, args))
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static Platform OsPlatform() =>
#if WINDOWS
        new() { Ble = new WinBle(), Temps = () => new Temps(), MediaPlaying = WinMedia.IsPlaying, Signals = WinSignals.Read };
#else
        OperatingSystem.IsMacOS()
            ? new() { Ble = new NoBle(), Temps = () => new MacTemps(), Signals = MacSignals.Read }
            : new() { Ble = new LinuxBle(), Temps = () => new LinuxTemps(), MediaPlaying = LinuxSignals.SoundPlaying, Signals = LinuxSignals.Read };
#endif

    private static void Setup(IClassicDesktopStyleApplicationLifetime life, Controller ctl, string[] args)
    {
        var win = new MainWindow(ctl);
        var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Bs1Cooler/Assets/cooler.ico")));
        var menu = new NativeMenu();
        void Add(string text, Action a) { var i = new NativeMenuItem(Tx.Tr(text)); i.Click += (_, _) => a(); menu.Add(i); }
        win.LanguageChanged += () => { foreach (var i in menu.Items) if (i is NativeMenuItem m && m.Header != null) m.Header = Tx.Retranslate(m.Header); };
        Add("Open", win.ShowNearTray);
        Add("Turn on", () => ctl.SetOn(true));
        Add("Turn off", () => ctl.SetOn(false));
        menu.Add(new NativeMenuItemSeparator());
        Add("Exit", () => life.Shutdown());
        var tray = new TrayIcon { Icon = icon, ToolTipText = "BS1 Cooler", Menu = menu };
        tray.Clicked += (_, _) => { if (win.IsVisible) win.Hide(); else win.ShowNearTray(); };
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
        var quietIcon = new WindowIcon(QuietIcon.Draw());
        bool shownQuiet = false;
        ctl.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ctl.Quiet != shownQuiet) { shownQuiet = ctl.Quiet; tray.Icon = shownQuiet ? quietIcon : icon; }
            string t = $"BS1 Cooler – {Tx.Tr(ctl.Status)}" + (ctl.QuietReason is { } why ? $" · {Tx.Tr("Quiet")}: {Tx.Tr(why)}" : "");
            tray.ToolTipText = t.Length > 63 ? t[..63] : t;  // Windows tooltip limit
        });

        // Windows shutdown/logoff: first in line, and hold the session end while the cooler is switched off
        if (OperatingSystem.IsWindows()) SetProcessShutdownParameters(0x3FF, 0);
        life.ShutdownRequested += (_, e) =>
        {
            var hwnd = OperatingSystem.IsWindows() ? win.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero : IntPtr.Zero;
            if (hwnd != IntPtr.Zero) ShutdownBlockReasonCreate(hwnd, Tx.Tr("Turning off the cooler"));
            ctl.PowerDown("session ending");
            if (hwnd != IntPtr.Zero) ShutdownBlockReasonDestroy(hwnd);
        };
        if (args.Contains("--smoke"))
        {
            if (args.Contains("--hidden"))  // drawn off every screen, never focused: nothing changes for the user
            {
                win.ShowInTaskbar = false;
                win.ShowActivated = false;
                win.Position = new PixelPoint(-32000, -32000);
                win.Show();
            }
            else win.ShowNearTray();
            win.SmokeTest(args.SkipWhile(a => a != "--show").Skip(1).FirstOrDefault(), args.SkipWhile(a => a != "--lang").Skip(1).FirstOrDefault());  // --show <page>: stay there (screenshots)
            string? png = args.SkipWhile(a => a != "--png").Skip(1).FirstOrDefault();
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (png != null) { win.Snapshot(png); QuietIcon.Draw().Save(png.Replace(".png", "-icon.png")); }  // drawn by the app itself: works even when the screen is covered
                Console.WriteLine("smoke ok");
                life.Shutdown();
            }, TimeSpan.FromSeconds(png != null ? 2 : 3));
            return;
        }
        Task.Run(ctl.RunAsync);
        if (!args.Contains("--tray")) win.ShowNearTray();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);
    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);
}
