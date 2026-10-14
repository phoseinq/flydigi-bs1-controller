using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Bs1Cooler.App;

/// The ISP bootloader on macOS through libusb (macOS has no usbfs; the bootloader needs no driver).
/// The release bundles libusb-1.0.dylib next to the app; Homebrew's copy is used as a fallback.
/// Experimental: written and CI-checked without a Mac and a cooler to flash.
public sealed class LibusbPort : IIspPort
{
    private const string Lib = "libusb-1.0";
    private const byte EpOut = 0x02, EpIn = 0x82;
    private static IntPtr ctx;
    private static string? lastError;
    private readonly IntPtr handle;

    static LibusbPort()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibusbPort).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var dir in new[] { AppContext.BaseDirectory, "/opt/homebrew/lib", "/usr/local/lib" })
                if (NativeLibrary.TryLoad(Path.Combine(dir, "libusb-1.0.dylib"), out var h)) return h;
            return IntPtr.Zero;  // default probing, then DllNotFoundException
        });
    }

    private LibusbPort(IntPtr handle) => this.handle = handle;

    /// libusb loads and can list devices (CI check: `--usb-probe`).
    public static string Probe()
    {
        if (!Init()) return "libusb unavailable";
        long n = libusb_get_device_list(ctx, out var list);
        if (n >= 0) libusb_free_device_list(list, 1);
        return n >= 0 ? $"libusb ok, {n} devices" : $"libusb error {n}";
    }

    private static bool Init()
    {
        try { return ctx != IntPtr.Zero || libusb_init(out ctx) == 0; }
        catch (DllNotFoundException) { return false; }
    }

    /// The first WCH ISP device (4348/1A86:55E0), or null.
    public static LibusbPort? Open()
    {
        if (!Init()) { Note("libusb-1.0.dylib not found"); return null; }
        foreach (ushort vid in new ushort[] { 0x4348, 0x1A86 })
        {
            var h = libusb_open_device_with_vid_pid(ctx, vid, 0x55E0);
            if (h == IntPtr.Zero) continue;
            int r = libusb_claim_interface(h, 0);
            if (r == 0) return new LibusbPort(h);
            libusb_close(h);
            Note($"claim interface: libusb error {r}");
        }
        return null;
    }

    private static void Note(string why)  // once per kind of failure: the wizard polls every 150 ms
    {
        if (why != lastError) Log.Write("bootloader: " + (lastError = why));
    }

    public void Write(byte[] data)
    {
        if (libusb_bulk_transfer(handle, EpOut, data, data.Length, out int n, 2000) != 0 || n != data.Length)
            throw new InvalidOperationException("USB write failed");
    }

    public byte[] Read(int timeoutMs)
    {
        var buf = new byte[64];
        if (libusb_bulk_transfer(handle, EpIn, buf, buf.Length, out int n, (uint)timeoutMs) != 0) throw new InvalidOperationException("USB read failed");
        return buf[..n];
    }

    public void Dispose()
    {
        libusb_release_interface(handle, 0);
        libusb_close(handle);
    }

    [DllImport(Lib)] private static extern int libusb_init(out IntPtr ctx);
    [DllImport(Lib)] private static extern long libusb_get_device_list(IntPtr ctx, out IntPtr list);
    [DllImport(Lib)] private static extern void libusb_free_device_list(IntPtr list, int unrefDevices);
    [DllImport(Lib)] private static extern IntPtr libusb_open_device_with_vid_pid(IntPtr ctx, ushort vid, ushort pid);
    [DllImport(Lib)] private static extern int libusb_claim_interface(IntPtr handle, int iface);
    [DllImport(Lib)] private static extern int libusb_release_interface(IntPtr handle, int iface);
    [DllImport(Lib)] private static extern void libusb_close(IntPtr handle);
    [DllImport(Lib)] private static extern int libusb_bulk_transfer(IntPtr handle, byte endpoint, byte[] data, int length, out int transferred, uint timeout);
}
