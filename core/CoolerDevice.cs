using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace Bs1Cooler;

/// Flydigi BS1 over USB HID (preferred: while USB data is connected the cooler's BLE only flaps) or BLE.
/// Frame: 5A A5 | cmd | len(=2+payload) | payload | sum8(cmd..payload).
/// Protocol reversed from Space Station's BLEServiceController.dll.
public sealed class CoolerDevice
{
    public static class Cmd
    {
        public const byte IsOpen = 2, OpenDevice = 5, ManualGear = 8,
            SetSpeed = 33, Consult = 34, StartSmart = 35, EndSmart = 36, DeviceMode = 37,
            RgbIsOn = 69, RgbOnOff = 70, Pulse = 0xEF;
    }

    public const string DeviceName = "Flydigi BS1";  // EVA edition uses the same name and protocol

    public event Action? ConnectionChanged;
    public event Action<byte[]>? Pulse;  // unsolicited 0xEF status frames

    public ulong Address => ble.Address;
    public bool Connected { get; private set; }
    public bool OverUsb => hid != null;

    private readonly IBleLink ble;
    private readonly SemaphoreSlim io = new(1, 1);
    private TaskCompletionSource<byte[]>? pending;
    private byte pendingCmd;
    private int silentCommands;  // consecutive commands without a reply
    private HidStream? hid;      // set while connected over USB
    private const int UsbVid = 0x04A0, UsbPid = 0x2501;

    public CoolerDevice(IBleLink ble)
    {
        this.ble = ble;
        ble.Frame += OnFrame;
        ble.LinkChanged += up =>
        {
            if (OverUsb || up == Connected) return;
            Connected = up;
            Log.Write(up ? "BLE link up" : "BLE link down");
            ConnectionChanged?.Invoke();
        };
    }

    public static byte[] Frame(byte cmd, ReadOnlySpan<byte> payload)
    {
        var f = new byte[5 + payload.Length];
        f[0] = 0x5A; f[1] = 0xA5; f[2] = cmd; f[3] = (byte)(2 + payload.Length);
        payload.CopyTo(f.AsSpan(4));
        byte sum = 0;
        for (int i = 2; i < f.Length - 1; i++) sum += f[i];
        f[^1] = sum;
        return f;
    }

    public void StartWatching(Action seen) => ble.StartWatching(seen);

    public async Task<bool> ConnectAsync()
    {
        if (Connected) return true;
        switch (TryUsb())
        {
            case true: return true;
            case null: return false;  // plugged in but not ready yet: its BLE is off, so don't wait on BLE
        }
        Drop();
        if (!await ble.ConnectAsync()) return false;
        Connected = true;
        Log.Write($"connected to {Address:X12}");
        ConnectionChanged?.Invoke();
        return true;
    }

    /// true = connected, false = not plugged in, null = plugged in but could not be opened (yet).
    private bool? TryUsb()
    {
        var d = DeviceList.Local.GetHidDevices(UsbVid, UsbPid).FirstOrDefault(x => x.GetMaxOutputReportLength() >= 32);
        if (d == null) return false;
        if (!d.TryOpen(out HidStream stream)) { Log.Write("USB device present but could not be opened"); return null; }
        Drop();
        stream.ReadTimeout = Timeout.Infinite;
        hid = stream;
        Connected = true;
        Log.Write("connected over USB");
        _ = Task.Run(() => UsbReader(stream));
        ConnectionChanged?.Invoke();
        return true;
    }

    private void UsbReader(HidStream stream)
    {
        var buf = new byte[stream.Device.GetMaxInputReportLength()];
        try
        {
            while (true)
            {
                int n = stream.Read(buf, 0, buf.Length);
                // [report id][5A A5 frame...]: find the header instead of trusting the offset
                int at = buf.AsSpan(0, Math.Min(n, 4)).IndexOf((byte)0x5A);
                if (at >= 0 && at + 5 <= n) OnFrame(buf.AsSpan(at, Math.Min(n - at, buf[at + 3] + 3)).ToArray());  // drop report padding
            }
        }
        catch (Exception e)
        {
            if (hid != stream) return;  // we closed it ourselves
            Log.Write("USB link down: " + e.Message);
            Drop();
            ConnectionChanged?.Invoke();
        }
    }

    /// A data cable plugged in while on BLE: drop BLE so the next connect picks USB.
    public void WatchUsb() => DeviceList.Local.Changed += (_, _) =>
    {
        if (Connected && !OverUsb && DeviceList.Local.GetHidDevices(UsbVid, UsbPid).Any())
        {
            Log.Write("USB cable detected, switching from BLE");
            Drop();
            ConnectionChanged?.Invoke();
        }
    };

    public void Disconnect()
    {
        bool was = Connected;
        Drop();
        if (was) ConnectionChanged?.Invoke();
    }

    private void Drop()
    {
        Connected = false;
        var h = hid;
        hid = null;
        h?.Dispose();
        ble.Drop();
    }

    private void OnFrame(byte[] f)
    {
        if (f.Length < 5 || f[0] != 0x5A || f[1] != 0xA5) return;
        if (f[2] == Cmd.Pulse) { Pulse?.Invoke(f); return; }
        if (f[2] == pendingCmd) pending?.TrySetResult(f);
    }

    /// Sends a command and returns the reply payload (bytes after the length byte, without checksum),
    /// or null after 3 unanswered tries.
    public async Task<byte[]?> SendAsync(byte cmd, params byte[] payload)
    {
        await io.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!Connected) return null;
                pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingCmd = cmd;
                try
                {
                    if (!await WriteFrame(Frame(cmd, payload))) continue;
                    var done = await Task.WhenAny(pending.Task, Task.Delay(800));
                    if (done != pending.Task) Log.Write($"cmd {cmd}: retry {attempt + 1}");
                    if (done == pending.Task)
                    {
                        byte[] r = pending.Task.Result;
                        int len = Math.Min(r[3] - 2, r.Length - 5);
                        silentCommands = 0;
                        return r.AsSpan(4, Math.Max(len, 0)).ToArray();
                    }
                }
                catch (Exception e) { Log.Write($"cmd {cmd} write failed: {e.Message}"); }
            }
            Log.Write($"cmd {cmd}: no reply");
            // Windows can silently re-establish the link and drop our notify subscription:
            // writes still "succeed" but nothing comes back. Tear down and reconnect from scratch.
            if (++silentCommands >= 2 && Connected)
            {
                Log.Write("link stale, reconnecting");
                silentCommands = 0;
                Drop();
                ConnectionChanged?.Invoke();
            }
            return null;
        }
        finally
        {
            pending = null;
            io.Release();
        }
    }

    /// Fire-and-forget write (its reply is ignored). For bursts where waiting on each ack is too slow.
    public async Task<bool> WriteAsync(byte cmd, params byte[] payload)
    {
        await io.WaitAsync();
        try { return await WriteFrame(Frame(cmd, payload)); }
        catch { return false; }
        finally { io.Release(); }
    }

    private async Task<bool> WriteFrame(byte[] frame)
    {
        if (hid is HidStream h)
        {
            // output report: [01][frame][zero pad][sum8 of bytes 0-30 at 31] (Space Station's LibHID layout)
            var r = new byte[h.Device.GetMaxOutputReportLength()];
            r[0] = 1;
            frame.CopyTo(r, 1);
            if (r.Length >= 32) { byte sum = 0; for (int i = 0; i < 31; i++) sum += r[i]; r[31] = sum; }
            await h.WriteAsync(r);
            return true;
        }
        return await ble.WriteAsync(frame);
    }

    /// true when the reply's first payload byte is 1 (the app's ack convention).
    public async Task<bool> AckAsync(byte cmd, params byte[] payload) => (await SendAsync(cmd, payload)) is [1, ..];
}
