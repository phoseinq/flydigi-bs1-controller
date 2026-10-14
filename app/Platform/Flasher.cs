using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Bs1Cooler.App;

/// Puts firmware on the cooler: waits for its bootloader (button held while plugging in), then flashes.
public static class Flasher
{
    public enum Edition { Official, Custom }

    public static byte[] Image(Edition e)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(e == Edition.Official ? "official.bin" : "custom.bin")!;
        var b = new byte[s.Length];
        s.ReadExactly(b);
        return FirmwareBox.Open(b);
    }

    /// The custom image's patch version (cmd 0x49 answers it), read from the "BS1C" + version bytes.
    public static int CustomVersion()
    {
        var img = Image(Edition.Custom);
        int at = img.AsSpan().IndexOf("BS1C"u8);
        return at >= 0 && at + 4 < img.Length ? img[at + 4] : 0;
    }

    /// Waits for the bootloader, then flashes. `waiting` fires once the device is seen as "no driver".
    public static async Task Run(Edition e, Action<string, double> progress, Action driverMissing, CancellationToken ct)
    {
        var image = Image(e);
        IIspPort? port;
        bool warned = false;
        while ((port = OpenPort()) == null)
        {
            ct.ThrowIfCancellationRequested();
            if (!warned && OperatingSystem.IsWindows() && BootloaderWithoutDriver()) { warned = true; driverMissing(); }
            await Task.Delay(150, ct);
        }
        Log.Write($"flash {e}: bootloader found");
        using (port) await Task.Run(() => new WchIsp(port).Flash(image, progress), CancellationToken.None);
        Log.Write($"flash {e}: done");
    }

    /// Windows: WCH's driver. Linux: usbfs, no driver needed (the udev rule gives access). macOS: libusb (experimental).
#if WINDOWS
    private static IIspPort? OpenPort() => Ch375Port.Open();
#else
    private static IIspPort? OpenPort() => OperatingSystem.IsLinux() ? UsbfsPort.Open() : LibusbPort.Open();
#endif

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// Plugged in, in bootloader mode, but Windows has no driver bound to it.
    private static bool BootloaderWithoutDriver()
    {
        try
        {
            using var q = new ManagementObjectSearcher("SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_4348&PID_55E0%' OR DeviceID LIKE 'USB\\\\VID_1A86&PID_55E0%'");
            foreach (var d in q.Get()) if (Convert.ToInt32(d["ConfigManagerErrorCode"]) != 0) return true;
        }
        catch { }
        return false;
    }

    /// The exit code, or -1 when the process didn't finish in time (then it's killed: ExitCode would throw).
    public static int ExitCode(Process p, int ms)
    {
        using (p)
        {
            if (p.WaitForExit(ms)) return p.ExitCode;
            try { p.Kill(entireProcessTree: true); } catch { }
            return -1;
        }
    }

    /// WCH's signed driver package (shipped next to the exe). The app runs elevated, so no prompt.
    public static bool InstallDriver()
    {
        string inf = Path.Combine(AppContext.BaseDirectory, "ch375driver", "ch375wdm.inf");
        var p = Process.Start(new ProcessStartInfo("pnputil.exe", $"/add-driver \"{inf}\" /install") { CreateNoWindow = true, UseShellExecute = false })!;
        int code = ExitCode(p, 60000);
        Log.Write($"driver install: exit {code}");
        return code is 0 or 3010;  // 3010 = reboot suggested
    }
}
