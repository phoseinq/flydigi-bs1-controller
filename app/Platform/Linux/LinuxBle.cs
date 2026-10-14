using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;

namespace Bs1Cooler;

/// The cooler's BLE side on Linux (BlueZ over D-Bus): service FFF0, write FFF2, notify FFF1, as on Windows.
public sealed class LinuxBle : IBleLink
{
    private const string Service = "0000fff0-0000-1000-8000-00805f9b34fb", WriteChar = "0000fff2-0000-1000-8000-00805f9b34fb",
        NotifyChar = "0000fff1-0000-1000-8000-00805f9b34fb";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public ulong Address { get; set; }
    public bool Connected => write != null;
    public event Action<byte[]>? Frame;
    public event Action<bool>? LinkChanged;

    private Adapter? adapter;
    private Device? dev;
    private GattCharacteristic? write, notify;

    public void StartWatching(Action seen) => _ = Watch(seen);

    private async Task Watch(Action seen)
    {
        try
        {
            adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
            if (adapter == null) { Log.Write("no Bluetooth adapter"); return; }
            adapter.DeviceFound += async (_, e) =>
            {
                try
                {
                    ulong addr = Parse(await e.Device.GetAddressAsync());
                    // once we know our cooler, ignore other BS1s nearby
                    if (Address != 0 ? addr != Address : await e.Device.GetNameAsync() != CoolerDevice.DeviceName) return;
                    if (Address == 0) Log.Write($"found {CoolerDevice.DeviceName} at {addr:X12}");
                    Address = addr;
                    seen();
                }
                catch { }  // a device without a name or one that went away
            };
            await adapter.StartDiscoveryAsync();
        }
        catch (Exception e) { Log.Write("Bluetooth unavailable: " + e.Message); }
    }

    public async Task<bool> ConnectAsync()
    {
        if (Address == 0 || adapter == null) return false;
        try
        {
            Drop();
            dev = await adapter.GetDeviceAsync(Format(Address));
            if (dev == null) return false;  // not seen yet
            await dev.ConnectAsync();
            await dev.WaitForPropertyValueAsync("Connected", true, Limit);
            await dev.WaitForPropertyValueAsync("ServicesResolved", true, Limit);
            var svc = await dev.GetServiceAsync(Service);
            var w = svc == null ? null : await svc.GetCharacteristicAsync(WriteChar);
            notify = svc == null ? null : await svc.GetCharacteristicAsync(NotifyChar);
            if (w == null || notify == null) { Log.Write("characteristics unavailable"); Drop(); return false; }
            notify.Value += OnNotify;  // subscribing turns notifications on
            dev.Disconnected += (d, _) => { if (d == dev) { LinkChanged?.Invoke(false); } return Task.CompletedTask; };
            write = w;
            return true;
        }
        catch (Exception e)
        {
            Log.Write("connect failed: " + e.Message);
            Drop();
            return false;
        }
    }

    public void Drop()
    {
        if (notify != null) notify.Value -= OnNotify;
        write = notify = null;
        var d = dev;
        dev = null;
        if (d != null) _ = d.DisconnectAsync().ContinueWith(_ => d.Dispose());
    }

    private Task OnNotify(GattCharacteristic _, GattCharacteristicValueEventArgs e)
    {
        Frame?.Invoke(e.Value);
        return Task.CompletedTask;
    }

    public async Task<bool> WriteAsync(byte[] frame)
    {
        var w = write;
        if (w == null) return false;
        try
        {
            // write without response, like Windows; give up when the link dies under it
            await w.WriteValueAsync(frame, new Dictionary<string, object> { ["type"] = "command" }).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"cmd {frame[2]} write: {e.Message}");
            return false;
        }
    }

    private static ulong Parse(string mac) => Convert.ToUInt64(mac.Replace(":", ""), 16);
    private static string Format(ulong a) => string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (40 - 8 * i)) & 0xFF).ToString("X2")));
}
