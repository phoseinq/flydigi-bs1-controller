using System;
using System.Linq;

namespace Bs1Cooler;

/// The CH571's ROM USB bootloader (VID 4348/1A86, PID 55E0): raw request/response over bulk endpoints.
/// Windows: WCH's own driver (CH375DLL64); Linux/macOS: libusb.
public interface IIspPort : IDisposable
{
    void Write(byte[] data);
    byte[] Read(int timeoutMs);
}

/// WCH ISP protocol, ported from wchisp (github.com/ch32-rs/wchisp): request = cmd | len16 LE | payload,
/// response = cmd | 0 | len16 LE | payload. Program/verify data is XORed with a key derived from the chip UID.
public sealed class WchIsp
{
    public const byte ChipId = 0x71, DeviceType = 0x13;  // CH571
    private const int Chunk = 56;

    private readonly IIspPort port;
    private byte[] key = new byte[8];

    public WchIsp(IIspPort port) => this.port = port;

    /// identify -> erase -> program -> verify -> reset. `progress` gets (what, 0..1).
    public void Flash(byte[] image, Action<string, double>? progress = null)
    {
        // the bootloader programs whole blocks: a short last chunk is dropped and fails verify. Pad with
        // erased-flash 0xFF to a 1 KB multiple, like wchisp does.
        int padded = (image.Length + 1023) / 1024 * 1024;
        if (padded != image.Length) image = [.. image, .. Enumerable.Repeat((byte)0xFF, padded - image.Length)];
        progress?.Invoke("identify", 0);
        var id = Transfer(Identify());
        if (id[0] != ChipId || id[1] != DeviceType) throw new InvalidOperationException($"not a CH571 (chip {id[0]:X2} type {id[1]:X2})");
        var cfg = Transfer([0xA7, 0x02, 0x00, 0x1F, 0x00]);  // read config, all fields
        key = Key(cfg.AsSpan(18, 8));

        progress?.Invoke("erase", 0);
        uint sectors = (uint)(image.Length / 1024 + 1);
        Transfer([0xA4, 0x04, 0x00, (byte)sectors, (byte)(sectors >> 8), (byte)(sectors >> 16), (byte)(sectors >> 24)], 5000);

        SendKey();
        for (int at = 0; at < image.Length; at += Chunk)
        {
            Transfer(Data(0xA5, (uint)at, image.AsSpan(at, Math.Min(Chunk, image.Length - at))), 300);
            progress?.Invoke("write", (double)at / image.Length);
        }
        Transfer(Data(0xA5, (uint)image.Length, default), 300);  // an empty write finishes programming

        SendKey();
        for (int at = 0; at < image.Length; at += Chunk)
        {
            var r = Transfer(Data(0xA6, (uint)at, image.AsSpan(at, Math.Min(Chunk, image.Length - at))));
            if (r[0] != 0) throw new InvalidOperationException($"verify failed at 0x{at:X}");
            progress?.Invoke("verify", (double)at / image.Length);
        }
        progress?.Invoke("restart", 1);
        try { Transfer([0xA2, 0x01, 0x00, 0x01]); } catch { }  // reset: the device may vanish before it answers
    }

    public static byte[] Identify() => [0xA1, 0x12, 0x00, 0x00, 0x00, .. "MCU ISP & WCH.CN"u8];

    /// key = sum(uid) x 8, last byte + chip id
    public static byte[] Key(ReadOnlySpan<byte> uid)
    {
        byte sum = 0;
        foreach (var b in uid) sum += b;
        var k = Enumerable.Repeat(sum, 8).ToArray();
        k[7] += ChipId;
        return k;
    }

    /// Program (0xA5) / verify (0xA6): address32 LE, a padding byte, data XOR key.
    public byte[] Data(byte cmd, uint address, ReadOnlySpan<byte> data)
    {
        var b = new byte[8 + data.Length];
        b[0] = cmd;
        b[1] = (byte)(b.Length - 3);
        b[2] = (byte)((b.Length - 3) >> 8);
        BitConverter.TryWriteBytes(b.AsSpan(3), address);
        b[7] = (byte)Random.Shared.Next(256);
        for (int i = 0; i < data.Length; i++) b[8 + i] = (byte)(data[i] ^ key[i % 8]);
        return b;
    }

    private void SendKey()
    {
        var r = Transfer([0xA3, 0x1E, 0x00, .. new byte[0x1E]]);  // all-zero seed, like wchisp
        byte sum = 0;
        foreach (var b in key) sum += b;
        if (r[0] != sum) throw new InvalidOperationException("ISP key mismatch");
    }

    /// Sends one request; returns the response payload, or throws.
    private byte[] Transfer(byte[] req, int timeoutMs = 1000)
    {
        port.Write(req);
        var r = port.Read(timeoutMs);
        if (r.Length < 4 || r[0] != req[0]) throw new InvalidOperationException($"bad reply to 0x{req[0]:X2}");
        int len = r[2] | r[3] << 8;
        if (r.Length - 4 != len) throw new InvalidOperationException($"bad reply length to 0x{req[0]:X2}");
        return r[4..];
    }
}
