using System;
using System.Runtime.InteropServices;

namespace Bs1Cooler.App;

/// The ISP bootloader through WCH's own Windows driver (CH372/CH375, WHQL; Space Station installs it too).
/// CH375DLL64.dll ships next to the exe. Same calls as wchisp's CH375 backend.
public sealed class Ch375Port : IIspPort
{
    private const string Dll = "CH375DLL64.dll";
    private readonly uint index;

    private Ch375Port(uint index) => this.index = index;

    /// The first WCH ISP device (4348/1A86:55E0), or null.
    public static Ch375Port? Open()
    {
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                if (CH375OpenDevice(i) == uint.MaxValue) continue;
                var d = new byte[18];
                uint len = (uint)d.Length;
                CH375GetDeviceDescr(i, d, ref len);
                int vid = d[8] | d[9] << 8, pid = d[10] | d[11] << 8;
                if ((vid == 0x4348 || vid == 0x1A86) && pid == 0x55E0) return new Ch375Port(i);
                CH375CloseDevice(i);
            }
        }
        catch (DllNotFoundException) { Log.Write("CH375DLL64.dll missing"); }
        return null;
    }

    public void Write(byte[] data)
    {
        uint len = (uint)data.Length;
        if (!CH375WriteData(index, data, ref len)) throw new InvalidOperationException("USB write failed");
    }

    public byte[] Read(int timeoutMs)
    {
        CH375SetTimeoutEx(index, (uint)timeoutMs, (uint)timeoutMs, (uint)timeoutMs, (uint)timeoutMs);
        var buf = new byte[64];
        uint len = (uint)buf.Length;
        if (!CH375ReadData(index, buf, ref len)) throw new InvalidOperationException("USB read failed");
        return buf[..(int)len];
    }

    public void Dispose() => CH375CloseDevice(index);

    [DllImport(Dll)] private static extern uint CH375OpenDevice(uint index);
    [DllImport(Dll)] private static extern void CH375CloseDevice(uint index);
    [DllImport(Dll)] private static extern bool CH375GetDeviceDescr(uint index, byte[] buf, ref uint len);
    [DllImport(Dll)] private static extern bool CH375WriteData(uint index, byte[] buf, ref uint len);
    [DllImport(Dll)] private static extern bool CH375ReadData(uint index, byte[] buf, ref uint len);
    [DllImport(Dll)] private static extern bool CH375SetTimeoutEx(uint index, uint write, uint read, uint auxWrite, uint auxRead);
}
