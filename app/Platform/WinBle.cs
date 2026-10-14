using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Bs1Cooler;

/// The cooler's BLE side on Windows (WinRT GATT): service FFF0, write FFF2, notify FFF1.
public sealed class WinBle : IBleLink
{
    private static readonly Guid Service = Uuid(0xFFF0), WriteChar = Uuid(0xFFF2), NotifyChar = Uuid(0xFFF1);
    private static Guid Uuid(ushort s) => new($"0000{s:x4}-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);  // WinRT GATT calls can hang when the link dies

    public ulong Address { get; set; }
    public event Action<byte[]>? Frame;
    public event Action<bool>? LinkChanged;

    private BluetoothLEDevice? dev;
    private GattSession? session;
    // must stay referenced: once GC'd, WinRT closes the service and notifications silently stop
    private GattDeviceService? service;
    private GattCharacteristic? write, notify;
    private BluetoothLEAdvertisementWatcher? watcher;

    public void StartWatching(Action seen)
    {
        if (watcher != null) return;
        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, a) =>
        {
            // once we know our cooler, ignore other BS1s nearby
            if (Address != 0 ? a.BluetoothAddress == Address : a.Advertisement.LocalName == CoolerDevice.DeviceName)
            {
                if (Address == 0) Log.Write($"found {CoolerDevice.DeviceName} at {a.BluetoothAddress:X12} (rssi {a.RawSignalStrengthInDBm})");
                Address = a.BluetoothAddress;
                seen();
            }
        };
        watcher.Start();
    }

    public async Task<bool> ConnectAsync()
    {
        if (Address == 0) return false;
        try
        {
            Drop();
            dev = await BluetoothLEDevice.FromBluetoothAddressAsync(Address);
            if (dev == null) return false;  // not seen since boot: wait for an advertisement
            session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
            session.MaintainConnection = true;  // Windows keeps reconnecting while in range

            var svcs = await dev.GetGattServicesForUuidAsync(Service, BluetoothCacheMode.Uncached).AsTask().WaitAsync(Limit);
            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0) { Drop(); return false; }
            service = svcs.Services[0];
            var cr = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(Limit);
            var w = cr.Characteristics.FirstOrDefault(c => c.Uuid == WriteChar);
            notify = cr.Characteristics.FirstOrDefault(c => c.Uuid == NotifyChar);
            if (w == null || notify == null) { Log.Write($"characteristics unavailable: {cr.Status}"); Drop(); return false; }
            notify.ValueChanged += OnNotify;
            var st = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().WaitAsync(Limit);
            if (st != GattCommunicationStatus.Success) { Drop(); return false; }

            dev.ConnectionStatusChanged += (d, _) =>
            {
                if (d != dev) return;  // a dropped device can still report in
                LinkChanged?.Invoke(d.ConnectionStatus == BluetoothConnectionStatus.Connected);
            };
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
        if (notify != null) notify.ValueChanged -= OnNotify;
        write = notify = null;
        service?.Dispose(); service = null;
        session?.Dispose(); session = null;
        dev?.Dispose(); dev = null;
    }

    private void OnNotify(GattCharacteristic _, GattValueChangedEventArgs args) => Frame?.Invoke(args.CharacteristicValue.ToArray());

    public async Task<bool> WriteAsync(byte[] frame)
    {
        var w = write;
        if (w == null) return false;
        // a write can hang forever when the link dies under it (e.g. a USB cable takes over): it would hold
        // the device's command lock and freeze every command, so give up after 2 s
        var st = await w.WriteValueAsync(frame.AsBuffer(), GattWriteOption.WriteWithoutResponse).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        if (st != GattCommunicationStatus.Success) Log.Write($"cmd {frame[2]} write: {st}");
        return st == GattCommunicationStatus.Success;
    }
}
