using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Bs1Cooler;

/// CPU/GPU temperatures via LibreHardwareMonitor (CPU needs admin for its ring0 driver).
public sealed class Temps : ITemps, IDisposable
{
    private readonly Computer pc = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true, IsMotherboardEnabled = true };
    private List<Part> parts = [];

    public IReadOnlyList<Part> Parts => parts;
    public double? Load { get; private set; }
    public double? BusiestCore { get; private set; }

    public Temps() => pc.Open();

    public (double? cpu, double? gpu) Read()
    {
        double? cpu = null, gpu = null;
        var found = new List<Part>();
        double? load = null, busiest = null;
        foreach (var hw in pc.Hardware)
        {
            try { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); } catch { continue; }  // one broken sensor must not kill the rest
            var temps = hw.Sensors.Concat(hw.SubHardware.SelectMany(h => h.Sensors))
                .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 120).ToList();
            // CPU Total / GPU Core (NVIDIA, AMD) / D3D 3D (Intel); not Video Decode: playing a film isn't work
            if (hw.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
                && hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name is "CPU Total" or "GPU Core" or "D3D 3D")?.Value is float l)
                load = Math.Max(load ?? 0, l);
            if (hw.HardwareType == HardwareType.Cpu)
                foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #") && s.Value is float))
                    busiest = Math.Max(busiest ?? 0, s.Value!.Value);
            if (temps.Count == 0) continue;
            if (hw.HardwareType == HardwareType.Storage)
            {
                // NVMe "Composite" is the drive's own summary
                found.Add(new("disk", hw.Name, (temps.FirstOrDefault(s => s.Name == "Composite") ?? temps[0]).Value!.Value));
                continue;
            }
            if (hw.HardwareType == HardwareType.Motherboard)
            {
                found.Add(new("board", hw.Name, temps.Max(s => s.Value!.Value)));
                continue;
            }
            if (hw.HardwareType == HardwareType.Cpu)
            {
                // Intel "Core Average" (Package follows the single hottest core: 62 C with cores at ~50 on this
                // i7-13700H), AMD "Core (Tctl/Tdie)", else Package; never the "Distance to TjMax" sensors
                temps.RemoveAll(s => s.Name.Contains("Distance"));
                var main = temps.FirstOrDefault(s => s.Name == "Core Average")
                           ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl") || s.Name.Contains("Package"));
                double v = main?.Value ?? temps.Max(s => s.Value!.Value);
                cpu = Max(cpu, (float)v);
                found.Add(new("cpu", hw.Name, v));
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                var core = temps.FirstOrDefault(s => s.Name == "GPU Core");
                double v = core?.Value ?? temps.Max(s => s.Value!.Value);
                gpu = Max(gpu, (float)v);
                found.Add(new("gpu", hw.Name, v));
            }
        }
        if (!found.Any(p => p.Kind == "board") && BoardZone() is double z) found.Add(new("board", boardName ??= BoardName(), z));
        parts = found;
        Load = load;
        BusiestCore = busiest;
        return (cpu, gpu);
    }

    // laptops: LHM sees no board sensor, but Windows exposes the ACPI thermal zone (no admin needed)
    private string? boardName;
    private bool noZone;

    private double? BoardZone()
    {
        if (noZone) return null;
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            var k = q.Get().Cast<System.Management.ManagementObject>().Select(o => Convert.ToDouble(o["HighPrecisionTemperature"])).Where(v => v > 0).ToList();
            if (k.Count > 0) return k.Max() / 10 - 273.15;  // tenths of a kelvin
        }
        catch { }
        noZone = true;  // not on this PC: stop asking
        return null;
    }

    private static string BoardName()
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            var b = q.Get().Cast<System.Management.ManagementObject>().First();
            return $"{b["Manufacturer"]} {b["Product"]}".Trim();
        }
        catch { return "Motherboard"; }
    }

    /// Dev: every temperature sensor, for checking which one we pick.
    public string Dump() => string.Join("; ", pc.Hardware.SelectMany(h => h.Sensors)
        .Where(s => s.SensorType == SensorType.Temperature)
        .Select(s => $"{s.Hardware.Name}/{s.Name}={s.Value:0.0}"));

    private static double? Max(double? a, float? b) => b is null ? a : Math.Max(a ?? double.MinValue, b.Value);

    public void Dispose() => pc.Close();
}
