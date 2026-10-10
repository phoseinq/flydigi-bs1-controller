using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Bs1Cooler;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--selftest")) { Environment.Exit(SelfTest()); return; }
        // dev helper: Bs1Cooler.dll --light '{"Mode":"Wave","Colors":["#3EC1B5"]}' -> hex for preview.hex
        if (args is ["--anim", var bright])  // dev helper: boot animation hex
        {
            Console.WriteLine(Convert.ToHexString(new Lighting { Brightness = int.Parse(bright) }.BootAnimation()));
            return;
        }
        if (args is ["--light", var json, ..])
        {
            // optional: °C, rpm (temperature wave color / tempo)
            var light = Settings.ParseLighting(json);
            string? color = args.Length > 2 ? Lighting.TemperatureColor(double.Parse(args[2])).color : null;
            Console.WriteLine(Convert.ToHexString(light.Config(color)));
            return;
        }
        using var single = new Mutex(true, @"Local\Bs1Cooler", out bool first);
        if (!first) return;  // ponytail: second launch just exits; the tray icon is the way back in

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Dark };
        var ctl = new Controller();
        var win = new MainWindow(ctl);

        using var tray = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/cooler.ico")).Stream),
            Text = "BS1 Cooler",
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };
        var menu = tray.ContextMenuStrip;
        menu.Items.Add("Open", null, (_, _) => ShowNearTray(win));
        menu.Items.Add("Turn on", null, (_, _) => ctl.SetOn(true));
        menu.Items.Add("Turn off", null, (_, _) => ctl.SetOn(false));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Left) return;
            if (win.IsVisible) win.Hide(); else ShowNearTray(win);
        };
        ctl.Changed += () => app.Dispatcher.BeginInvoke(() =>
        {
            string t = $"BS1 Cooler – {ctl.Status}";
            tray.Text = t.Length > 63 ? t[..63] : t;  // NotifyIcon limit
        });

        SetProcessShutdownParameters(0x3FF, 0);  // first in line for session end: more Bluetooth time
        app.SessionEnding += (_, e) =>
        {
            // reason = Windows waits for us (shows it only if we're slow) instead of killing us at ~5 s
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
            ShutdownBlockReasonCreate(hwnd, "Turning off the cooler");
            ctl.PowerDown($"session ending ({e.ReasonSessionEnding})");
            ShutdownBlockReasonDestroy(hwnd);
        };
        Controller.DevHooks = args.Contains("--dev");
        Task.Run(ctl.RunAsync);
        if (!args.Contains("--tray")) ShowNearTray(win);
        app.Run();
        tray.Visible = false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    /// Checks the byte-level bits against frames captured from Space Station.
    private static int SelfTest()
    {
        static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
        try
        {
            Check(Convert.ToHexString(CoolerDevice.Frame(70, new byte[] { 0 })) == "5AA546030049", "frame checksum");
            var teal = new Lighting { Mode = LightMode.Static, Colors = new() { "#00FFBB" }, Brightness = 20 }.Config();
            Check(teal.Length == 96, "config length");
            Check(Convert.ToHexString(teal, 0, 10) == "000200000F1400FFBB00", "static config head (Space Station chunk 0)");
            Check(Convert.ToHexString(teal, 36, 3) == "00FFBB" && Convert.ToHexString(teal, 66, 3) == "00FFBB", "all 3 zones");
            var zoned = new Lighting { Mode = LightMode.Static, ZoneColors = new() { "#FF0000", "#00FF00", "#0000FF" } }.Config();
            Check(Convert.ToHexString(zoned, 6, 3) + Convert.ToHexString(zoned, 36, 3) + Convert.ToHexString(zoned, 66, 3) == "FF000000FF000000FF", "per-zone");
            var breathe = new Lighting { Mode = LightMode.Breathe, Colors = new() { "#FF0000", "#0000FF" }, Period = 30 }.Config();
            Check(breathe[3] == 3 && breathe[4] == 30, "breathe loop end / period");
            Check(Convert.ToHexString(breathe, 6, 15) == "000000FF0000000000" + "0000FF" + "000000", "breathe slots: gap,c1,gap,c2,gap");
            Check(Lighting.TemperatureColor(30) == (44, "#0055FF") && Lighting.TemperatureColor(95) == (0, "#FF0000"), "temperature color ends");
            Check(Lighting.TemperatureColor(72.5).color == "#00FF00" || Lighting.TemperatureColor(72.5).step == 22, "temperature color middle ~green");
            var tb = new Lighting { Mode = LightMode.TemperatureBreathe, Period = 19 }.Config("#FF0000");
            Check(tb[3] == 0 && Convert.ToHexString(tb, 6, 3) + Convert.ToHexString(tb, 36, 3) + Convert.ToHexString(tb, 66, 3) == "FF0000FF0000FF0000", "temperature: steady color, all zones");
            var wave = new Lighting { Mode = LightMode.Wave, Colors = new() { "#FF0000", "#00FF00", "#0000FF" }, Period = 3 }.Config();
            Check(wave[3] == 8 && wave[4] == 3, "wave loop: 3 colors x 3 = 9 slots");
            // zone 0 starts red, zone 1 one third later (green), zone 2 two thirds later (blue)
            Check(Convert.ToHexString(wave, 6, 3) + Convert.ToHexString(wave, 36, 3) + Convert.ToHexString(wave, 66, 3) == "FF000000FF000000FF", "wave phase shift");
            var water = new Lighting { Mode = LightMode.Wave, Colors = new() { "#0060FF" }, Period = 10 }.Config();
            Check(water[3] == 1, "water: breathe's 2-slot loop");
            Check(Convert.ToHexString(water, 6, 6) + Convert.ToHexString(water, 36, 6) + Convert.ToHexString(water, 66, 6)
                  == "0000000060FF" + "000000003EA6" + "000000002666", "water: off together, zones 100/65/40%");
            Check(Lighting.RpmHue(1500) == 220 && Lighting.RpmHue(1800) is > 170 and < 180 && Lighting.RpmHue(3000) == 0 && Lighting.RpmHue(1000) == 220, "wave color by rpm");
            var ramp = new Lighting().Ramp(new double[] { 0, 2, 4, 6, 8, 120, 120, 120, 120, 240 }, 5);
            Check(ramp[3] == 9 && ramp[4] == 5 && Convert.ToHexString(ramp, 6, 3) == "FF0000" && Convert.ToHexString(ramp, 36 + 15, 3) == "00FF00"
                  && Convert.ToHexString(ramp, 66 + 27, 3) == "0000FF", "ramp: one hue per slot, all zones");
            var boot = new Lighting().BootAnimation();
            Check(Convert.ToHexString(boot, 6, 3) + Convert.ToHexString(boot, 36, 3) + Convert.ToHexString(boot, 66, 3) == "000000000000000000", "boot: starts dark");
            Check(boot[6 + 3] < 0x80 && boot[36 + 6] < 0x80 && boot[66 + 9] == 0xFF && boot[6 + 9] == 0, "boot: dim pulse runs 1 -> 3, lands full");
            Check(boot[66 + 12] == 0xFF && boot[6 + 12] < 0x80 && boot[36 + 15] == 0xFF && boot[6 + 18] == 0xFF && boot[4] == 7, "boot: zone 3 stays, next lands at 2, then 1; slower");
            Check(boot[6 + 24] == 0 && boot[66 + 27] == 0 && boot[6 + 21] == 0xFF, "boot: glow, then 2 dark slots for the handover");
            Check(new Lighting().Mode == LightMode.TemperatureBreathe && new Lighting().Brightness == 22, "defaults = temperature wave");
            var s = new Settings();
            Check(s.CurveRpm(0) == 1300 && s.CurveRpm(55) == 1850 && s.CurveRpm(95) == 3000, "curve");
            Console.WriteLine("selftest ok");
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e.Message); return 1; }
    }

    private static void ShowNearTray(Window w)
    {
        var area = SystemParameters.WorkArea;
        w.Show();
        w.Left = area.Right - w.ActualWidth - 12;
        w.Top = area.Bottom - w.ActualHeight - 12;
        w.Activate();
    }
}
