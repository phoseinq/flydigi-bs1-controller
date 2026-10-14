using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Bs1Cooler.App;

/// The ISP bootloader on Linux, straight through usbfs (/dev/bus/usb): no driver, no libusb.
/// Access comes from the udev rule (99-bs1cooler.rules); bulk endpoints as in wchisp.
public sealed class UsbfsPort : IIspPort
{
    private const byte EpOut = 0x02, EpIn = 0x82;
    // _IOWR('U', 2, struct usbdevfs_bulktransfer), _IOR('U', 15, unsigned int), _IOR('U', 16, unsigned int)
    private const nuint Bulk = 0xC0185502, ClaimInterface = 0x8004550F, ReleaseInterface = 0x80045510;
    private readonly int fd;
    private static string? lastError;

    private UsbfsPort(int fd) => this.fd = fd;

    /// The first WCH ISP device (4348/1A86:55E0), or null.
    public static UsbfsPort? Open()
    {
        const string Devices = "/sys/bus/usb/devices";
        if (!Directory.Exists(Devices)) return null;  // no USB bus at all (containers, WSL before usbipd attaches one)
        foreach (var dev in Directory.EnumerateDirectories(Devices))
        {
            string? vid = Attr(dev, "idVendor"), pid = Attr(dev, "idProduct");
            if (vid is not ("4348" or "1a86") || pid != "55e0") continue;
            string path = $"/dev/bus/usb/{int.Parse(Attr(dev, "busnum")!):000}/{int.Parse(Attr(dev, "devnum")!):000}";
            int fd = open(path, 2 /* O_RDWR */);
            uint iface = 0;
            if (fd >= 0 && ioctl(fd, ClaimInterface, ref iface) >= 0) return new UsbfsPort(fd);
            int err = Marshal.GetLastPInvokeError();
            if (fd >= 0) close(fd);
            // once per kind of failure: the wizard polls every 150 ms
            string why = $"{path}: errno {err}{(err == 13 ? " (no permission: the udev rule isn't installed)" : "")}";
            if (why != lastError) Log.Write("bootloader " + (lastError = why));
        }
        return null;
    }

    private static string? Attr(string dev, string name)
    {
        try { return File.ReadAllText(Path.Combine(dev, name)).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Write(byte[] data)
    {
        if (Transfer(EpOut, data, data.Length, 2000) != data.Length) throw new InvalidOperationException("USB write failed");
    }

    public byte[] Read(int timeoutMs)
    {
        var buf = new byte[64];
        int n = Transfer(EpIn, buf, buf.Length, timeoutMs);
        if (n < 0) throw new InvalidOperationException("USB read failed");
        return buf[..n];
    }

    private int Transfer(byte ep, byte[] buf, int len, int timeoutMs)
    {
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            var t = new BulkTransfer { Ep = ep, Len = (uint)len, Timeout = (uint)timeoutMs, Data = h.AddrOfPinnedObject() };
            return ioctl(fd, Bulk, ref t);
        }
        finally { h.Free(); }
    }

    public void Dispose()
    {
        uint iface = 0;
        ioctl(fd, ReleaseInterface, ref iface);
        close(fd);
    }

    /// struct usbdevfs_bulktransfer: 3 x u32, then the data pointer (24 bytes on 64-bit).
    [StructLayout(LayoutKind.Sequential)]
    internal struct BulkTransfer { public uint Ep, Len, Timeout; public IntPtr Data; }

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, ref uint arg);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, ref BulkTransfer arg);
}
