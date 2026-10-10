using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Bs1Cooler;

/// Flydigi BS1 over BLE. Frame: 5A A5 | cmd | len(=2+payload) | payload | sum8(cmd..payload).
/// Protocol reversed from Space Station's BLEServiceController.dll.
public sealed class CoolerDevice
{
    public static class Cmd
    {
        public const byte IsOpen = 2, OpenDevice = 5, ManualGear = 8,
            SetSpeed = 33, Consult = 34, StartSmart = 35, EndSmart = 36, DeviceMode = 37,
            RgbIsOn = 69, RgbOnOff = 70, Pulse = 0xEF;
    }

    private static readonly Guid Service = Uuid(0xFFF0), WriteChar = Uuid(0xFFF2), NotifyChar = Uuid(0xFFF1);
    private static Guid Uuid(ushort s) => new($"0000{s:x4}-0000-1000-8000-00805f9b34fb");

    public const string DeviceName = "Flydigi BS1";  // EVA edition uses the same name and protocol

    public event Action? ConnectionChanged;
    public event Action<byte[]>? Pulse;  // unsolicited 0xEF status frames

    public ulong Address { get; private set; }
    public bool Connected { get; private set; }

    private BluetoothLEDevice? dev;
    private GattSession? session;
    // must stay referenced: once GC'd, WinRT closes the service and notifications silently stop
    private GattDeviceService? service;
    private GattCharacteristic? write, notify;
    private readonly SemaphoreSlim io = new(1, 1);
    private TaskCompletionSource<byte[]>? pending;
    private byte pendingCmd;
    private BluetoothLEAdvertisementWatcher? watcher;
    private int silentCommands;  // consecutive commands without a reply

    public CoolerDevice(ulong address) => Address = address;

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

    /// Fires `seen` whenever the cooler advertises (it only advertises while not connected).
    public void StartWatching(Action seen)
    {
        if (watcher != null) return;
        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, a) =>
        {
            // once we know our cooler, ignore other BS1s nearby
            if (Address != 0 ? a.BluetoothAddress == Address : a.Advertisement.LocalName == DeviceName)
            {
                if (Address == 0) Log.Write($"found {DeviceName} at {a.BluetoothAddress:X12} (rssi {a.RawSignalStrengthInDBm})");
                Address = a.BluetoothAddress;
                seen();
            }
        };
        watcher.Start();
    }

    public void StopWatching()
    {
        watcher?.Stop();
        watcher = null;
    }

    public async Task<bool> ConnectAsync()
    {
        if (Connected) return true;
        if (Address == 0) return false;
        try
        {
            Drop();
            dev = await BluetoothLEDevice.FromBluetoothAddressAsync(Address);
            if (dev == null) return false;  // not seen since boot: wait for an advertisement
            session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
            session.MaintainConnection = true;  // Windows keeps reconnecting while in range

            var svcs = await dev.GetGattServicesForUuidAsync(Service, BluetoothCacheMode.Uncached);
            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0) { Drop(); return false; }
            service = svcs.Services[0];
            var cr = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            write = cr.Characteristics.FirstOrDefault(c => c.Uuid == WriteChar);
            notify = cr.Characteristics.FirstOrDefault(c => c.Uuid == NotifyChar);
            if (write == null || notify == null) { Log.Write($"characteristics unavailable: {cr.Status}"); Drop(); return false; }
            notify.ValueChanged += OnNotify;
            var st = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (st != GattCommunicationStatus.Success) { Drop(); return false; }

            dev.ConnectionStatusChanged += (d, _) =>
            {
                bool now = d.ConnectionStatus == BluetoothConnectionStatus.Connected;
                if (now == Connected) return;
                Connected = now;
                Log.Write(now ? "BLE link up" : "BLE link down");
                ConnectionChanged?.Invoke();
            };
            Connected = true;
            Log.Write($"connected to {Address:X12}");
            ConnectionChanged?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            Log.Write("connect failed: " + e.Message);
            Drop();
            return false;
        }
    }

    public void Disconnect()
    {
        bool was = Connected;
        Drop();
        if (was) ConnectionChanged?.Invoke();
    }

    private void Drop()
    {
        Connected = false;
        if (notify != null) notify.ValueChanged -= OnNotify;
        write = notify = null;
        service?.Dispose(); service = null;
        session?.Dispose(); session = null;
        dev?.Dispose(); dev = null;
    }

    private void OnNotify(GattCharacteristic _, GattValueChangedEventArgs args)
    {
        byte[] f = args.CharacteristicValue.ToArray();
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
                var w = write;
                if (w == null) return null;
                pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingCmd = cmd;
                try
                {
                    var st = await w.WriteValueAsync(Frame(cmd, payload).AsBuffer(), GattWriteOption.WriteWithoutResponse);
                    if (st != GattCommunicationStatus.Success) { Log.Write($"cmd {cmd} write: {st}"); continue; }
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
        try
        {
            var w = write;
            return w != null && await w.WriteValueAsync(Frame(cmd, payload).AsBuffer(), GattWriteOption.WriteWithoutResponse) == GattCommunicationStatus.Success;
        }
        catch { return false; }
        finally { io.Release(); }
    }

    /// true when the reply's first payload byte is 1 (the app's ack convention).
    public async Task<bool> AckAsync(byte cmd, params byte[] payload) => (await SendAsync(cmd, payload)) is [1, ..];
}
