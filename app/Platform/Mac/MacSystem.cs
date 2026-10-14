using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bs1Cooler;

/// macOS: the cooler over USB only (Bluetooth needs CoreBluetooth bindings), and no temperature sensors
/// without root, so the fan follows CPU load (the load curve).
/// ponytail: no BLE and no temperatures here; add CoreBluetooth / SMC reads if Mac users ask.
public sealed class NoBle : IBleLink
{
    public ulong Address { get; set; }
    public bool Connected => false;
    public event Action<byte[]>? Frame { add { } remove { } }
    public event Action<bool>? LinkChanged { add { } remove { } }
    public void StartWatching(Action seen) { }
    public Task<bool> ConnectAsync() => Task.FromResult(false);
    public Task<bool> WriteAsync(byte[] frame) => Task.FromResult(false);
    public void Drop() { }
}

public sealed class MacTemps : ITemps
{
    public double? Load { get; private set; }

    public (double? cpu, double? gpu) Read()
    {
        // ps reports each process's CPU share of one core: the sum over all cores is the machine's load
        double sum = LinuxTemps.Run("ps", "-A -o %cpu").Split('\n').Skip(1)
            .Sum(l => double.TryParse(l.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0);
        Load = Math.Min(100, sum / Environment.ProcessorCount);
        return (null, null);
    }

    public string Dump() => $"load {Load:0}% (no temperature sensors on macOS without root)";
}

public static class MacSignals
{
    public static Signals Read() => new(false, !LinuxTemps.Run("pmset", "-g batt").Contains("AC Power"), false, false);
}
