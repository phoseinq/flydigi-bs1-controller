using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bs1Cooler;

public enum FanMode { Smart, Fixed }
public enum StartupMode { On, Off, Keep }

public class Settings
{
    public ulong Address { get; set; }  // 0 = not paired yet: take the first BS1 that advertises
    public bool SoftOff { get; set; }  // the user's on/off choice; also what StartupMode.Keep restores
    public FanMode FanMode { get; set; } = FanMode.Smart;
    public int FixedRpm { get; set; } = 1300;
    /// Smart curve: (°C, rpm), linear in between. Default = Space Station's own curve.
    public List<int[]> Curve { get; set; } = new() { new[] { 0, 1300 }, new[] { 50, 1700 }, new[] { 60, 2000 }, new[] { 70, 2300 }, new[] { 80, 2500 }, new[] { 85, 2700 }, new[] { 90, 3000 } };
    public bool OffOnShutdown { get; set; } = true;
    public bool QuietWhenMedia { get; set; } = true;
    /// Calibration: seconds one breath takes per unit of Period (cycle = 2 slots x Period x this).
    /// Measured by eye on the real cooler; used to resend the temperature wave exactly at its dark point.
    public double BreathSecondsPerUnit { get; set; } = 0.091;  // measured on the real cooler
    public StartupMode OnStartup { get; set; } = StartupMode.On;
    public bool AutoConnect { get; set; } = true;
    public Lighting Light { get; set; } = new();

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bs1Cooler");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opts) ?? new(); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        catch (Exception e) { Log.Write("settings unreadable, using defaults: " + e.Message); return new(); }
    }

    public static Lighting ParseLighting(string json) => JsonSerializer.Deserialize<Lighting>(json, Opts)!;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
    }

    public int CurveRpm(double temp)
    {
        var pts = Curve;
        if (temp <= pts[0][0]) return pts[0][1];
        for (int i = 1; i < pts.Count; i++)
        {
            if (temp > pts[i][0]) continue;
            var (t0, r0, t1, r1) = (pts[i - 1][0], pts[i - 1][1], pts[i][0], pts[i][1]);
            return (int)(r0 + (r1 - r0) * (temp - t0) / (t1 - t0));
        }
        return pts[^1][1];
    }
}

/// TemperatureBreathe (beta) is ours: a steady color that follows fan speed (name kept for saved settings).
/// Wave (beta). One color = all zones breathe together, each zone a bit dimmer (same hue, Breathe's timing);
/// several colors = a color wave, each zone a third of a cycle behind the previous one.
public enum LightMode { Static, Breathe, Twinkle, Fade, Temperature, TemperatureBreathe, Wave }

/// Defaults = the temperature wave as tuned with the user; restored on every app start.
public class Lighting
{
    public bool On { get; set; } = true;
    public LightMode Mode { get; set; } = LightMode.TemperatureBreathe;
    public List<string> Colors { get; set; } = new() { "#00FFBB" };
    public int Brightness { get; set; } = 22;  // 0-100
    /// Effect cycle length (byte 4 of the config). Space Station only used 2-15; higher = slower.
    public int Period { get; set; } = 15;
    /// Static only: one color per LED zone (the config has 3 zone groups). null = all zones use Colors[0].
    public List<string>? ZoneColors { get; set; }

    /// The 96-byte RGB config the cooler expects (RGBConfig.cs in Space Station).
    /// Blue 220° (≤40°C) -> cyan -> green -> yellow -> orange -> red 0° (≥85°C).
    /// Wave color by fan speed: 1500 rpm = blue, ~1800 (normal idle) = teal, 3000 = red.
    public static double RpmHue(int rpm) => Math.Clamp(220 - (rpm - 1500) * 220 / 1500.0, 0, 220);

    // 50 C (this CPU idles 50-60) = blue, 95 C = red
    public static double TemperatureHue(double temp) => Math.Clamp(220 - (temp - 50) * 220 / 45, 0, 220);

    /// TemperatureHue in 5° steps; step lets callers resend only on change.
    public static (int step, string color) TemperatureColor(double temp)
    {
        int step = (int)Math.Round(TemperatureHue(temp) / 5);
        return (step, HueColor(step * 5));
    }

    public static string HueColor(double hue)
    {
        double x = 1 - Math.Abs(hue / 60 % 2 - 1);
        var (r, g, b) = hue switch { < 60 => (1.0, x, 0.0), < 120 => (x, 1.0, 0.0), < 180 => (0.0, 1.0, x), _ => (0.0, x, 1.0) };
        return $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
    }

    public byte[] Config(string? colorOverride = null, int? periodOverride = null)
    {
        var cfg = new byte[96];
        cfg[1] = 2;  // version 00 02
        cfg[5] = (byte)Math.Clamp(Brightness, 0, 100);
        var rgb = colorOverride != null ? new() { Parse(colorOverride) } : Colors.ConvertAll(Parse);
        if (rgb.Count == 0) rgb.Add((0, 0xFF, 0xBB));
        var units = new (byte r, byte g, byte b)[10];
        int waveShift = 0;
        double[]? zoneLevel = null;  // per-zone brightness, same hue
        if (Mode is LightMode.Wave)
        {
            int len;
            if (rgb.Count == 1)
            {
                // Breathe's own 2-slot loop (off, color): the cooler fades smoothly at Breathe speed.
                // Mixing toward white for "lighter" shades turned teal green on the LEDs, so only dim.
                len = 2;
                units[1] = rgb[0];
                zoneLevel = new[] { 1.0, 0.65, 0.4 };
            }
            else
            {
                len = rgb.Count * (10 / rgb.Count);  // whole color cycles that fit in the 10 slots
                for (int i = 0; i < len; i++) units[i] = rgb[i % rgb.Count];
                // a third of one color cycle (not of the whole loop, which repeats the cycle)
                waveShift = Math.Max(1, (int)Math.Round(rgb.Count / 3.0));
            }
            cfg[3] = (byte)(len - 1);
            cfg[4] = (byte)Math.Clamp(periodOverride ?? Period, 1, 255);
        }
        else if (Mode is LightMode.Static or LightMode.TemperatureBreathe)
        {
            cfg[3] = 0;
            cfg[4] = 15;
            units[0] = rgb[0];
        }
        else
        {
            int n = rgb.Count;
            cfg[3] = (byte)Math.Min(2 * n - 1, 9);
            cfg[4] = (byte)Math.Clamp(periodOverride ?? Period, 1, 255);
            // odd slots carry the colors, even slots are black gaps (Fade fills them too)
            for (int i = 0; i < 10 && i <= 2 * n; i++)
                if (i % 2 == 1 || Mode == LightMode.Fade) units[i] = rgb[i / 2 % n];
        }
        bool zoned = Mode == LightMode.Static && ZoneColors is { Count: 3 };
        for (int group = 0; group < 3; group++)  // 3 LED zones
        {
            if (zoned) units[0] = Parse(ZoneColors![group]);
            int len = cfg[3] + 1;
            for (int i = 0; i < 10; i++)
            {
                int o = 6 + group * 30 + i * 3;
                // wave: zone g starts g thirds later in the same loop
                var u = waveShift > 0 && i < len ? units[(i + group * waveShift) % len] : units[i];
                if (zoneLevel != null) u = Scale(u, zoneLevel[group]);
                (cfg[o], cfg[o + 1], cfg[o + 2]) = u;
            }
        }
        return cfg;
    }

    // ---- one-shot animations: a single config the cooler plays itself (no resends mid-way).
    // The loop would repeat, so the caller replaces/turns off the lights before it wraps.
    public const int BootPeriod = 7;

    /// Boot, stacking blocks: a yellow pulse runs zone 1 -> 3 (leaving a fading trail) and lands at 3, the
    /// next lands at 2, the last at 1; a landing flares then settles, and all three glow at the end.
    /// The cooler fades between slots, so the pulse glides from zone to zone.
    private static readonly double[][] BootFrames =
    {
        // moving pulse at 0.45 (trail 0.15), landed zones at full: easy to tell apart
        new[] { 0.0, 0, 0 },
        new[] { 0.45, 0, 0 },         // wave 1
        new[] { 0.15, 0.45, 0 },
        new[] { 0.0, 0.15, 1 },       // lands on 3
        new[] { 0.45, 0, 1 },         // wave 2
        new[] { 0.15, 1, 1 },         // lands on 2
        new[] { 1.0, 1, 1 },          // wave 3 lands on 1
        new[] { 1.0, 1, 1 },
        new[] { 0.0, 0, 0 },          // fade to dark: the wave takes over here (2 dark slots = slack for
        new[] { 0.0, 0, 0 },          // a slow BLE send, otherwise the loop replays the boot)
    };

    public byte[] BootAnimation() => Animation((zone, slot) => Scale(Parse("#FFC400"), BootFrames[slot][zone]), period: BootPeriod);

    /// Temperature color ramp: one hue per slot (all zones); the cooler fades from slot to slot by itself.
    public byte[] Ramp(double[] hues, int period) => Animation((_, slot) => Parse(HueColor(hues[slot])), period);

    private byte[] Animation(Func<int, int, (byte, byte, byte)> color, int period)
    {
        var cfg = new byte[96];
        cfg[1] = 2;
        cfg[3] = 9;
        cfg[4] = (byte)period;
        cfg[5] = (byte)Math.Clamp(Brightness, 0, 100);
        for (int zone = 0; zone < 3; zone++)
            for (int slot = 0; slot < 10; slot++)
            {
                var (r, g, b) = color(zone, slot);
                int o = 6 + zone * 30 + slot * 3;
                (cfg[o], cfg[o + 1], cfg[o + 2]) = (r, g, b);
            }
        return cfg;
    }

    private static (byte, byte, byte) Scale((byte r, byte g, byte b) c, double f) =>
        ((byte)Math.Round(c.r * f), (byte)Math.Round(c.g * f), (byte)Math.Round(c.b * f));

    private static (byte, byte, byte) Parse(string hex)
    {
        uint v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

public static class Log
{
    private static readonly object Lock = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Settings.Dir);
                string p = Path.Combine(Settings.Dir, "bs1cooler.log");
                // ponytail: crude rotation, keeps the log from growing forever
                if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) File.Delete(p);
                File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
