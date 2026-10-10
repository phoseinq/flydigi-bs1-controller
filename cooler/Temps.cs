using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Bs1Cooler;

/// CPU/GPU temperatures via LibreHardwareMonitor (CPU needs admin for its ring0 driver).
public sealed class Temps : IDisposable
{
    private readonly Computer pc = new() { IsCpuEnabled = true, IsGpuEnabled = true };

    public Temps() => pc.Open();

    public (double? cpu, double? gpu) Read()
    {
        double? cpu = null, gpu = null;
        foreach (var hw in pc.Hardware)
        {
            try { hw.Update(); } catch { continue; }  // one broken sensor must not kill the rest
            var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 120).ToList();
            if (temps.Count == 0) continue;
            if (hw.HardwareType == HardwareType.Cpu)
            {
                // Intel "Core Average" (Package follows the single hottest core: 62 C with cores at ~50 on this
                // i7-13700H), AMD "Core (Tctl/Tdie)", else Package; never the "Distance to TjMax" sensors
                temps.RemoveAll(s => s.Name.Contains("Distance"));
                var main = temps.FirstOrDefault(s => s.Name == "Core Average")
                           ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl") || s.Name.Contains("Package"));
                cpu = Max(cpu, main?.Value ?? temps.Max(s => s.Value));
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                var core = temps.FirstOrDefault(s => s.Name == "GPU Core");
                gpu = Max(gpu, core?.Value ?? temps.Max(s => s.Value));
            }
        }
        return (cpu, gpu);
    }

    /// Dev: every temperature sensor, for checking which one we pick.
    public string Dump() => string.Join("; ", pc.Hardware.SelectMany(h => h.Sensors)
        .Where(s => s.SensorType == SensorType.Temperature)
        .Select(s => $"{s.Hardware.Name}/{s.Name}={s.Value:0.0}"));

    private static double? Max(double? a, float? b) => b is null ? a : Math.Max(a ?? double.MinValue, b.Value);

    public void Dispose() => pc.Close();
}
