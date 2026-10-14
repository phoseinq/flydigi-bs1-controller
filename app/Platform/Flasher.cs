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
        Ch375Port? port;
        bool warned = false;
        while ((port = Ch375Port.Open()) == null)
        {
            ct.ThrowIfCancellationRequested();
            if (!warned && BootloaderWithoutDriver()) { warned = true; driverMissing(); }
            await Task.Delay(150, ct);
        }
        Log.Write($"flash {e}: bootloader found");
        using (port) await Task.Run(() => new WchIsp(port).Flash(image, progress), CancellationToken.None);
        Log.Write($"flash {e}: done");
    }

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

    /// WCH's signed driver package (shipped next to the exe). The app runs elevated, so no prompt.
    public static bool InstallDriver()
    {
        string inf = Path.Combine(AppContext.BaseDirectory, "ch375driver", "ch375wdm.inf");
        var p = Process.Start(new ProcessStartInfo("pnputil.exe", $"/add-driver \"{inf}\" /install") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(60000);
        Log.Write($"driver install: exit {p.ExitCode}");
        return p.ExitCode is 0 or 3010;  // 3010 = reboot suggested
    }
}
