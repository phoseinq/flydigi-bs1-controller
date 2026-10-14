using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Bs1Cooler;

/// Linux temperatures and load: hwmon (coretemp/k10temp, amdgpu, nvme, acpitz...), nvidia-smi, /proc/stat.
public sealed class LinuxTemps : ITemps
{
    private List<Part> parts = [];
    public IReadOnlyList<Part> Parts => parts;
    public double? Load { get; private set; }
    private (long busy, long all)? lastCpu;

    public (double? cpu, double? gpu) Read()
    {
        var found = new List<Part>();
        foreach (var dir in Directory.Exists("/sys/class/hwmon") ? Directory.GetDirectories("/sys/class/hwmon") : [])
        {
            string name = Text(Path.Combine(dir, "name"));
            var temps = Directory.GetFiles(dir, "temp*_input")
                .Select(f => (label: Text(f.Replace("_input", "_label")), c: Num(f) / 1000.0))
                .Where(t => t.c is > 0 and < 120).ToList();
            if (temps.Count == 0) continue;
            double Pick(params string[] labels) => temps.FirstOrDefault(t => labels.Any(l => t.label.StartsWith(l))) is var p && p.label != null && p.c > 0 ? p.c : temps.Max(t => t.c);
            switch (name)
            {
                case "coretemp" or "k10temp" or "zenpower":
                    found.Add(new("cpu", CpuName(), Pick("Package", "Tctl", "Tdie"))); break;
                case "amdgpu" or "radeon" or "nouveau" or "i915" or "xe":
                    found.Add(new("gpu", name, Pick("edge", "junction"))); break;
                case "nvme" or "drivetemp":
                    found.Add(new("disk", Text(Path.Combine(dir, "device", "model")) is { Length: > 0 } m ? m : name, Pick("Composite"))); break;
                case "acpitz":
                    if (!found.Any(p => p.Kind == "board")) found.Add(new("board", BoardName(), temps.Max(t => t.c))); break;
            }
        }
        double? gpuLoad = null;
        // NVIDIA's own driver has no hwmon: ask nvidia-smi (absent elsewhere: skipped)
        foreach (var line in Run("nvidia-smi", "--query-gpu=name,temperature.gpu,utilization.gpu --format=csv,noheader,nounits").Split('\n'))
        {
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length == 3 && double.TryParse(f[1], out var t)) found.Add(new("gpu", f[0], t));
            if (f.Length == 3 && double.TryParse(f[2], out var u)) gpuLoad = Math.Max(gpuLoad ?? 0, u);
        }
        foreach (var busy in Directory.Exists("/sys/class/drm") ? Directory.GetFiles("/sys/class/drm", "gpu_busy_percent", SearchOption.AllDirectories) : [])
            gpuLoad = Math.Max(gpuLoad ?? 0, Num(busy));
        Load = Max(CpuLoad(), gpuLoad);
        parts = found;
        double? cpu = found.Where(p => p.Kind == "cpu").Select(p => (double?)p.Temp).Max();
        double? gpu = found.Where(p => p.Kind == "gpu").Select(p => (double?)p.Temp).Max();
        return (cpu, gpu);
    }

    /// Busy share of all CPU time since the last read, from /proc/stat's first line.
    private double? CpuLoad()
    {
        var f = Text("/proc/stat").Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        if (f.Length < 4) return null;
        long idle = f[3] + (f.Length > 4 ? f[4] : 0), all = f.Sum();
        var prev = lastCpu;
        lastCpu = (all - idle, all);
        return prev is var (b0, a0) && all > a0 ? 100.0 * (all - idle - b0) / (all - a0) : null;
    }

    public string Dump() => string.Join("; ", Directory.Exists("/sys/class/hwmon") ? Directory.GetDirectories("/sys/class/hwmon")
        .SelectMany(d => Directory.GetFiles(d, "temp*_input").Select(f => $"{Text(Path.Combine(d, "name"))}/{Text(f.Replace("_input", "_label"))}={Num(f) / 1000.0:0.0}")) : []);

    private static string? cpuName;
    private static string CpuName() => cpuName ??= Text("/proc/cpuinfo").Split('\n').FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "CPU";
    private static string BoardName() => $"{Text("/sys/class/dmi/id/board_vendor")} {Text("/sys/class/dmi/id/board_name")}".Trim() is { Length: > 0 } n ? n : "Motherboard";

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    internal static string Text(string path) { try { return File.ReadAllText(path).Trim(); } catch { return ""; } }
    private static double Num(string path) => double.TryParse(Text(path), out var v) ? v : 0;

    /// stdout of a short command, "" when it's missing or slow.
    internal static string Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(1500)) { p.Kill(); return ""; }
            return output.Result;
        }
        catch { return ""; }  // not installed
    }
}

/// Linux quiet-mode inputs: PulseAudio / PipeWire (pactl), power supply, logind, GNOME's Do Not Disturb.
public static class LinuxSignals
{
    /// A stream that is playing (not corked) on any output.
    public static Task<bool> SoundPlaying() => Task.FromResult(LinuxTemps.Run("pactl", "list sink-inputs").Contains("Corked: no"));

    public static Signals Read() => new(MicInUse(), OnBattery(), DoNotDisturb(), Locked());

    /// Any app recording from a real source (monitor sources are other apps' output, not a mic).
    private static bool MicInUse()
    {
        var monitors = LinuxTemps.Run("pactl", "list short sources").Split('\n')
            .Select(l => l.Split('\t')).Where(f => f.Length > 1 && f[1].EndsWith(".monitor")).Select(f => f[0]).ToHashSet();
        return LinuxTemps.Run("pactl", "list short source-outputs").Split('\n')
            .Select(l => l.Split('\t')).Any(f => f.Length > 1 && !monitors.Contains(f[1]));
    }

    private static bool OnBattery()
    {
        var mains = Directory.Exists("/sys/class/power_supply") ? Directory.GetDirectories("/sys/class/power_supply")
            .Where(d => LinuxTemps.Text(Path.Combine(d, "type")) == "Mains").ToList() : [];
        return mains.Count > 0 && mains.All(d => LinuxTemps.Text(Path.Combine(d, "online")) == "0");
    }

    private static bool DoNotDisturb() => LinuxTemps.Run("gsettings", "get org.gnome.desktop.notifications show-banners").Trim() == "false";

    private static bool Locked() => LinuxTemps.Run("loginctl", "show-session auto -p LockedHint --value").Trim() == "yes";
}
