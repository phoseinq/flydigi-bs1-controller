using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using C = Bs1Cooler.CoolerDevice.Cmd;

namespace Bs1Cooler;

/// Owns the cooler: smart fan curve, on/off, lighting, power events, (re)connection.
public sealed class Controller
{
    /// --dev: enables the file hooks below (debug.cmds, fake.txt, preview.hex). Off in releases: any user
    /// process can write %APPDATA%, and this process runs elevated.
    public static bool DevHooks;

    public readonly Settings S = Settings.Load();
    public readonly CoolerDevice Dev;
    public event Action? Changed;

    // live status for the UI
    public double? Cpu, Gpu;
    public System.Collections.Generic.IReadOnlyList<Part> Parts = [];
    public int CurrentRpm, TargetRpm, DeviceMode = -2;  // -1 gear 0 (stopped), 0-3 gears, 4 smart, -2 unknown
    public bool? DeviceOpen;
    public bool UserDisconnected { get; private set; }

    private readonly Platform platform;
    private ITemps? temps;
    private double? smoothTemp;   // asymmetric EMA of max(cpu, gpu): what the curve sees
    private double? rawTemp, prevRaw;
    private int lastSentRpm = -1, lastTempBand = -1;
    private DateTime lastSpeedChange;
    public bool MediaPlaying { get; private set; }
    private readonly MediaGate mediaGate = new();
    /// Why quiet mode is on ("Media", "Call", "Night", "Battery", "Do Not Disturb"), or null. Several reasons never
    /// stack: the curve drops by the same 8 °C at most.
    public string? QuietReason { get; private set; }
    public bool Quiet => QuietReason != null;
    /// Smoothed CPU/GPU load (%); Heavy above 40 (until it falls under 25): no quiet mode, more air.
    public double? Load { get; private set; }
    public bool Heavy { get; private set; }
    /// Temperatures were read and then stopped coming: the fan runs at SafeRpm until they return.
    public bool SensorsLost { get; private set; }
    private DateTime lastTempAt;
    /// Screen locked: lights off (cooling goes on).
    public bool Locked { get; private set; }
    private readonly MediaGate callGate = new(enter: TimeSpan.Zero);
    private Signals? sig;
    private DateTime lastSignals;
    /// Quiet mode lowers the curve by up to 8 °C, but only while the laptop is cool: a quieter cooler that
    /// lets the laptop's own (louder) fans ramp up is a net loss. Full at <= 60 °C, none from 70 °C.
    private double QuietOffset => Quiet && smoothTemp is double t ? 8 * Math.Clamp((70 - t) / 10, 0, 1) : 0;
    /// Lights at 30% while media plays (user pick).
    private int Brightness => Quiet ? S.Light.Brightness * 3 / 10 : S.Light.Brightness;
    private bool lastQuiet;
    private bool lightDirty = true, startupPending = true, poweringDown, connectRequested, justConnected;
    public int FirmwarePatch => fwPatch;
    /// The official firmware version (cmd 1), e.g. "0.0.6.02"; null until read.
    public string? FirmwareVersion { get; private set; }
    /// 0.0.6.02 added "start after a power cut"; older ones should be updated.
    public bool FirmwareOld => Version.TryParse(FirmwareVersion, out var v) && v < new Version(0, 0, 6, 2);
    private int fwPatch;  // our custom firmware's version (cmd 0x49 "BS1C" + n), 0 = official firmware
    private bool bootAnimPending = true;  // played once per app start (= per Windows logon)
    private bool reopen;  // the link dropped while the cooler was on: if it comes back off, power was lost
    private bool rgbReady;  // boot animation just left RGB on: the wave's first send goes straight in
    private readonly SemaphoreSlim tickGate = new(1, 1);
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime lastConnectTry;

    public Controller(Platform platform)
    {
        this.platform = platform;
        // every start (= every boot) undoes whatever the user fiddled with: Smart fan + the temperature wave
        S.FanMode = FanMode.Smart;
        S.Light = new Lighting();
        S.Save();
        platform.Ble.Address = S.Address;
        Dev = new CoolerDevice(platform.Ble);
        Dev.Pulse += OnPulse;
        Dev.ConnectionChanged += () =>
        {
            if (Dev.Connected) justConnected = true;
            else { reopen = DeviceOpen == true; DeviceOpen = null; DeviceMode = -2; CurrentRpm = TargetRpm = 0; }
            Changed?.Invoke();
            Kick();
        };
    }

    /// The host calls these on sleep / wake.
    public void Suspend() => PowerDown("suspend");

    public void Resume()
    {
        Log.Write("resume");
        poweringDown = false;
        startupPending = lightDirty = true;
        Kick();
    }

    private int pulses;

    /// Unsolicited status frame (5A A5 EF ...): live rpm without polling.
    private void OnPulse(byte[] f)
    {
        if (pulses++ == 0) Log.Write("first pulse: " + Convert.ToHexString(f));
        if (f.Length > 11)
        {
            CurrentRpm = f[7] | f[8] << 8;
            TargetRpm = f[9] | f[10] << 8;
            Changed?.Invoke();
        }
    }

    public string Status =>
        UserDisconnected ? "Disconnected"
        : !Dev.Connected ? (S.AutoConnect || connectRequested ? "Searching…" : "Not connected")
        : DeviceOpen == false ? "Powered off (button)"
        : S.SoftOff ? "Off"
        : $"{CurrentRpm} rpm" + (Dev.OverUsb ? " · USB" : "");

    public void Kick() => wake.TrySetResult();

    public void Save(bool lightsChanged = false)
    {
        if (lightsChanged) lightDirty = true;
        S.Save();
        Changed?.Invoke();
        Kick();
    }

    public void SetOn(bool on)
    {
        S.SoftOff = !on;
        if (on) poweringDown = false;  // a cancelled shutdown (or the dev test) left us parked
        Log.Write("user: " + (on ? "on" : "off"));
        Save(lightsChanged: true);
    }

    public void Connect()
    {
        UserDisconnected = false;
        connectRequested = true;
        lastConnectTry = default;
        Save();
    }

    public async void Disconnect()
    {
        UserDisconnected = true;
        await tickGate.WaitAsync();
        try { Dev.Disconnect(); }
        finally { tickGate.Release(); }
        Log.Write("user: disconnect");
        Changed?.Invoke();
    }

    public async Task RunAsync()
    {
        try { temps = platform.Temps?.Invoke(); var (c, g) = temps?.Read() ?? default; Log.Write($"sensors: cpu={c?.ToString("0.0") ?? "none"} gpu={g?.ToString("0.0") ?? "none"}; parts: {string.Join(", ", temps?.Parts.Select(p => $"{p.Kind} {p.Name} {p.Temp:0}") ?? [])}"); }
        catch (Exception e) { Log.Write("temperature sensors unavailable: " + e.Message); }
        Dev.StartWatching(() => { if (!Dev.Connected) Kick(); });  // TryConnect decides whether to act
        Dev.WatchUsb();
        _ = Task.Run(TempWaveLoop);

        while (true)
        {
            try
            {
                ReadTemps();
                if (!Dev.Connected) await TryConnect();
                else await Tick();
            }
            catch (Exception e) { Log.Write("loop: " + e); }
            Changed?.Invoke();
            await Task.WhenAny(Task.Delay(2000), wake.Task);
            wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ReadTemps()
    {
        if (temps == null) return;
        try { (Cpu, Gpu) = temps.Read(); }
        catch (Exception e) { (Cpu, Gpu) = (null, null); Log.Write("sensors failed: " + e.Message); }
        Parts = temps.Parts;
        // a maxed core counts as heavy work even when CPU total looks idle (a game's main thread). ponytail: Windows
        // spreads one busy thread over several cores (~45% each), so this mostly fires on Linux; temperature covers the rest
        double? raw = temps.Load is double tl ? Math.Max(tl, temps.BusiestCore >= 90 ? 40 : 0) : null;
        if (raw is double l)  // up in ~6 s, down in ~25 s: one busy second isn't work, a pause in work isn't idle
        {
            Load = Load is double p ? p + (l - p) * (l > p ? 0.35 : 0.08) : l;
            bool heavy = Heavy ? Load >= 25 : Load >= 40;
            if (heavy != Heavy) { Heavy = heavy; Log.Write($"load {Load:0}%: {(heavy ? "heavy, no quiet" : "light")}"); }
        }
        rawTemp = Cpu is null && Gpu is null ? null : Math.Max(Cpu ?? 0, Gpu ?? 0);
        if (rawTemp is not double r)
        {
            // had temperatures and lost them (sensor or driver failure): don't keep cooling by a stale reading
            bool stale = lastTempAt != default && DateTime.UtcNow - lastTempAt > TimeSpan.FromSeconds(10);
            if (stale != SensorsLost) { SensorsLost = stale; Log.Write(stale ? "temperatures lost: safe fan speed" : "temperatures back"); }
            return;
        }
        lastTempAt = DateTime.UtcNow;
        if (SensorsLost) { SensorsLost = false; Log.Write("temperatures back"); }
        // laptop CPUs spike for a second or two all the time: use the lower of the last two samples (a lone
        // spike never counts), then follow heat up in ~12 s, down in ~25 s (2 s ticks). The old instant
        // snap at 88 C let single package spikes pin the fan near 2900 rpm at 6% load.
        double t = Math.Min(r, prevRaw ?? r);
        prevRaw = r;
        if (r >= Critical) smoothTemp = Math.Max(smoothTemp ?? r, r);  // too hot to wait for smoothing
        else if (smoothTemp is not double s) smoothTemp = t;  // first sample
        else smoothTemp = s + (t - s) * (t > s ? 0.15 : 0.08);
    }

    private async Task TryConnect()
    {
        if (UserDisconnected || poweringDown || !(S.AutoConnect || connectRequested)) return;
        if (DateTime.Now - lastConnectTry < TimeSpan.FromSeconds(5)) return;
        lastConnectTry = DateTime.Now;
        if (await Dev.ConnectAsync())
        {
            connectRequested = false;
            if (Dev.Address != S.Address) { S.Address = Dev.Address; S.Save(); }
        }
    }

    private async Task Tick()
    {
        if (!await tickGate.WaitAsync(0)) return;
        try
        {
            if (poweringDown || !Dev.Connected) return;

            bool wasClosed = DeviceOpen == false;
            if (await Dev.SendAsync(C.IsOpen) is [var open, ..]) DeviceOpen = open == 1;  // a lost reply must not look like "closed"
            if (await Dev.SendAsync(C.Consult) is { Length: >= 4 } c)
            {
                CurrentRpm = c[0] | c[1] << 8;
                TargetRpm = c[2] | c[3] << 8;
            }
            // reply is mode+1, like cmd 8's payload: 0 = gear 0 (stopped), 1-4 = gears, 5 = smart
            if (await Dev.SendAsync(C.DeviceMode) is [var m, ..]) DeviceMode = m - 1;

            if (justConnected)
            {
                justConnected = false;
                fwPatch = await Dev.ProbeAsync(0x49) is [0x42, 0x53, 0x31, 0x43, var v, ..] ? v : 0;
                if (await Dev.SendAsync(1) is [var v0, var v1, var v2, var v3, ..]) FirmwareVersion = $"{v0}.{v1}.{v2}.{v3:00}";
                Log.Write($"firmware {FirmwareVersion}{(fwPatch > 0 ? $" + custom {fwPatch}" : "")}");
                lastSentRpm = lastTempBand = -1;
                lightDirty = true;
                if (startupPending)
                {
                    startupPending = false;
                    if (S.OnStartup != StartupMode.Keep) { S.SoftOff = S.OnStartup == StartupMode.Off; S.Save(); }
                    if (S.SoftOff || !S.Light.On) bootAnimPending = false;  // after the rule: it may turn us on
                    Log.Write($"startup rule {S.OnStartup} => {(S.SoftOff ? "off" : "on")}");
                    reopen = true;  // app start: the rule decides, not a leftover button press
                }
                // a power cut leaves the cooler off until its button is pressed; switching it off with the
                // button while connected never gets here, so that choice is still respected
                if (reopen && DeviceOpen == false && !S.SoftOff) await OpenAsync();
                reopen = false;
            }
            else if (wasClosed && DeviceOpen == true && S.SoftOff)
            {
                // closed -> open while connected = physical power button: the user wants it on
                Log.Write("power button: on");
                S.SoftOff = false;
                S.Save();
                lightDirty = true;
            }

            if (DeviceOpen != true) return;  // powered off with the button: leave it alone
            if (S.SoftOff) { if (DeviceMode == 4 || TargetRpm != 0 || lightDirty) await OffAsync(); return; }

            if (DeviceMode != 4)
            {
                var r = await Dev.SendAsync(C.StartSmart);  // PC-driven speed; reply 1 or 3 = ok
                Log.Write($"start smart (device mode was {DeviceMode}): {(r is null ? "no reply" : r[0].ToString())}");
                lastSentRpm = -1;
            }
            await UpdateMedia();
            int want;
            bool due;
            if (S.FanMode == FanMode.Fixed)
            {
                want = S.FixedRpm;
                due = want != lastSentRpm;
            }
            else
            {
                (want, due) = FanTarget(S, smoothTemp, rawTemp, QuietOffset, Load, SensorsLost, lastSentRpm, DateTime.Now - lastSpeedChange);
            }
            want = (int)Math.Round(Math.Clamp(want, 1300, 3000) / 50.0) * 50;
            if (due && want != lastSentRpm)
            {
                if (await Dev.AckAsync(C.SetSpeed, (byte)want, (byte)(want >> 8)))
                {
                    lastSentRpm = want;
                    lastSpeedChange = DateTime.Now;
                    Log.Write($"fan -> {want} rpm (cpu {Cpu:0.0} gpu {Gpu:0.0} smooth {smoothTemp:0.0}{(QuietOffset > 0 ? $" quiet -{QuietOffset:0.0}" : "")}, now {CurrentRpm})");
                }
            }
            await ApplyLights();
        }
        finally { tickGate.Release(); }
    }

    private static readonly string PreviewPath = System.IO.Path.Combine(Settings.Dir, "preview.hex");
    private DateTime previewStamp;

    // Temperature color. Every config apply (cmd 67) has a ~3% chance of hanging the cooler's firmware
    // (no reply, Bluetooth drops ~3.5 s later; tested 2026-10-09, not timing or pulse related), so send few:
    // one config carries a 10-slot hue ramp the cooler fades through itself, resent before its loop wraps.
    private const double HueStep = 2;  // degrees per slot
    private const int RampPeriod = 5;  // 0.5 s per slot: 4°/s, the 10-slot loop lasts 5 s
    private static readonly TimeSpan RampResend = TimeSpan.FromSeconds(3);  // next ramp well before the wrap
    private double? waveHue;  // start of the current ramp (= rampTo when steady); kept across reconnects
    private double rampTo;
    private DateTime rampAt;
    private bool colorShown;  // our color is on the cooler (false after a reconnect, boot or mode change)
    private double? loggedHue;
    private static readonly string FakePath = System.IO.Path.Combine(Settings.Dir, "fake.txt");
    private bool fakeOn;

    /// Where the cooler's fade is now.
    private double ShownHue(double from)
    {
        double slots = (DateTime.Now - rampAt).TotalSeconds / (RampPeriod * S.BreathSecondsPerUnit);
        return from + Math.Clamp(rampTo - from, -HueStep * slots, HueStep * slots);
    }

    /// Steady color (no breathing) following fan speed: ramps toward the target, then holds a static config.
    private async Task TempWaveLoop()
    {
        while (true)
        {
            try
            {
                if (TempWaveSource() is not var (L, rawTarget)) { colorShown = false; await Task.Delay(300); continue; }
                if (fwPatch >= 10) { await SendHue(L, (int)Math.Round(rawTarget)); continue; }
                double target = Math.Round(rawTarget), start;
                if (waveHue is not double from) start = target;
                else
                {
                    start = ShownHue(from);
                    if (colorShown)
                    {
                        bool steady = from == rampTo, rampDone = Math.Abs(rampTo - start) < 0.5;
                        if (steady && Math.Abs(target - start) < 1) { await Task.Delay(300); continue; }
                        if (!steady && !rampDone && DateTime.Now - rampAt < RampResend) { await Task.Delay(200); continue; }
                    }
                }
                if (!colorShown && !rgbReady)
                {
                    await Dev.SendAsync(C.RgbOnOff, 1);
                    await Dev.SendAsync(68, 0);  // leave the built-in temperature effects
                }
                rgbReady = false;
                // one talker at a time: interleaving with Tick's polls made the cooler drop commands
                if (!await tickGate.WaitAsync(1000)) continue;
                if (poweringDown) { tickGate.Release(); continue; }
                bool sent;
                try { sent = await SendRamp(L, start, target); }
                finally { tickGate.Release(); }
                if (!sent) await Task.Delay(1000);
            }
            catch (Exception e) { Log.Write("temp wave: " + e.Message); await Task.Delay(1000); }
        }
    }

    private (int hue, int bright) hueSent;
    private DateTime hueSentAt;

    /// Custom firmware 10+: one short command with the target hue; the cooler walks there itself (10°/s, a
    /// jump over 60° fades out and back in). Resent every 10 s in case something else set the lights.
    private async Task SendHue(Lighting L, int hue)
    {
        var want = (hue, Brightness);
        if (colorShown && want == hueSent && DateTime.Now - hueSentAt < TimeSpan.FromSeconds(10)) { await Task.Delay(300); return; }
        if (!await tickGate.WaitAsync(1000)) return;
        try
        {
            if (!colorShown && !rgbReady) await Dev.SendAsync(C.RgbOnOff, 1);  // before 0x4D: it resets the cooler to stock lighting
            rgbReady = false;
            if (!await Dev.AckAsync(0x4D, (byte)hue, (byte)(hue >> 8), (byte)Math.Clamp(Brightness, 0, 100), 10, 60)) { await Task.Delay(1000); return; }
            if (want != hueSent) Log.Write($"temp hue -> {hue} ({Lighting.HueColor(hue)})");
            (hueSent, hueSentAt, colorShown) = (want, DateTime.Now, true);
        }
        finally { tickGate.Release(); }
    }

    /// What the temperature wave should show now, or null when it shouldn't run.
    private (Lighting L, double hue)? TempWaveSource()
    {
        if (!Dev.Connected || DeviceOpen != true || S.SoftOff || poweringDown || UserDisconnected || bootAnimPending) return null;
        var L = S.Light;
        if (ReadFake() is var (_, fr))
            return (new Lighting { Mode = LightMode.TemperatureBreathe, Colors = L.Colors, Brightness = L.Brightness, Period = L.Period }, Lighting.RpmHue(fr));
        if (!L.On || Locked || L.Mode != LightMode.TemperatureBreathe || previewStamp != default) return null;
        return (L, Lighting.RpmHue(lastSentRpm));
    }

    private async Task<bool> SendRamp(Lighting L, double start, double target)
    {
        bool steady = Math.Abs(target - start) < 1;
        var hues = new double[10];
        for (int i = 0; i < 10; i++) hues[i] = start + Math.Clamp(target - start, -HueStep * i, HueStep * i);
        if (!await SendLightConfig(steady ? L.Config(Lighting.HueColor(target)) : L.Ramp(hues, RampPeriod))) return false;
        rampAt = DateTime.Now;  // the loop restarts at the finish
        (waveHue, rampTo) = steady ? (target, target) : (start, target);
        colorShown = true;
        if (loggedHue is not double lh || Math.Abs(start - lh) >= 20 || steady && lh != target)
        {
            Log.Write(steady ? $"temp color: hue {target:0} {Lighting.HueColor(target)}" : $"temp color: ramp {start:0} -> {target:0}");
            loggedHue = steady ? target : start;
        }
        return true;
    }



    private static readonly string ScriptPath = System.IO.Path.Combine(Settings.Dir, "debug.cmds");

    /// Dev: run raw cooler commands once, then delete the file. Lines: "<cmd> [hexpayload] [wait ms]",
    // dev-only: raw commands into an elevated process, so it runs only with --dev (DevHooks)
    // ponytail: dev-only backdoor into an elevated process; strip or gate it before a public release
    /// Every temperature sensor, for the debug tools.
    public string SensorDump() => temps?.Dump() ?? "no sensors";

    private async Task RunDebugScript()
    {
        if (!DevHooks || !System.IO.File.Exists(ScriptPath)) return;
        string[] lines;
        try { lines = System.IO.File.ReadAllLines(ScriptPath); System.IO.File.Delete(ScriptPath); }
        catch { return; }
        foreach (var raw in lines)
        {
            var p = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p[0].StartsWith('#')) continue;
            try
            {
                var t0 = DateTime.Now;
                if (p[0] == "wait") { await Task.Delay(int.Parse(p[1])); continue; }
                if (p[0] == "sensors") { Log.Write("all temps: " + temps?.Dump()); continue; }
                // parked 6 s so the off state is visible before normal operation resumes
                if (p[0] == "drop") { Dev.Disconnect(); Log.Write("debug: link dropped"); continue; }  // a lost link, as when a cable is pulled
                if (p[0] == "powerdown") { _ = Task.Run(async () => { PowerDown("session test"); await Task.Delay(6000); poweringDown = false; lightDirty = true; }); continue; }
                if (p[0] == "light")
                {
                    bool ok = await SendLightConfig(Convert.FromHexString(p[1]), fast: p.Length > 2);
                    Log.Write($"debug light fast={p.Length > 2}: {ok} {(DateTime.Now - t0).TotalMilliseconds:0} ms");
                    continue;
                }
                bool ack = p[0] == "ack";
                int i = ack ? 1 : 0;
                byte cmd = byte.Parse(p[i]);
                byte[] pay = p.Length > i + 1 ? Convert.FromHexString(p[i + 1]) : Array.Empty<byte>();
                if (ack) Log.Write($"debug {cmd} {Convert.ToHexString(pay)} -> {Convert.ToHexString(await Dev.SendAsync(cmd, pay) ?? Array.Empty<byte>())}");
                else { await Dev.WriteAsync(cmd, pay); Log.Write($"debug {cmd} {Convert.ToHexString(pay)} sent"); }
            }
            catch (Exception e) { Log.Write($"debug line '{raw}': {e.Message}"); }
        }
    }

    /// Dev/preview: %APPDATA%\Bs1Cooler\fake.txt = "<°C> <rpm>" drives the temperature wave with fake values.
    private static (double temp, int rpm)? ReadFake()
    {
        try
        {
            if (!DevHooks || !System.IO.File.Exists(FakePath)) return null;
            var p = System.IO.File.ReadAllText(FakePath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[1]));
        }
        catch { return null; }
    }

    /// Try an effect on the cooler without changing settings: write the 96-byte config as hex to
    /// %APPDATA%\Bs1Cooler\preview.hex; delete the file to go back to the saved lighting.
    private async Task<bool> ApplyPreview()
    {
        if (!DevHooks || !System.IO.File.Exists(PreviewPath))
        {
            if (previewStamp != default) { previewStamp = default; lightDirty = true; lastTempBand = -1; Log.Write("preview off"); }
            return false;
        }
        var stamp = System.IO.File.GetLastWriteTimeUtc(PreviewPath);
        if (stamp == previewStamp) return true;
        try
        {
            byte[] cfg = Convert.FromHexString(System.IO.File.ReadAllText(PreviewPath).Trim());
            if (cfg.Length != 96) throw new FormatException($"{cfg.Length} bytes, need 96");
            await Dev.SendAsync(C.RgbOnOff, 1);
            await Dev.SendAsync(68, 0);
            if (await SendLightConfig(cfg)) { previewStamp = stamp; Log.Write("preview on"); }
        }
        catch (Exception e) { previewStamp = stamp; Log.Write("preview ignored: " + e.Message); }
        return true;
    }

    public const double Critical = 90;
    public const int SafeRpm = 2500;

    /// Smart mode's speed and whether to send it now. Temperature curve (read lower in quiet mode) or load
    /// floor, whichever is higher; a safe speed when the temperatures were lost. Fewer, bigger steps: an
    /// audible change at most every 20 s, unless heat jumps; leftovers of 50+ rpm settle after a minute.
    public static (int want, bool due) FanTarget(Settings s, double? smoothTemp, double? rawTemp, double quietOffset, double? load,
        bool sensorsLost, int lastSent, TimeSpan sinceChange)
    {
        int want = sensorsLost ? SafeRpm : Math.Max(s.CurveRpm((smoothTemp ?? 60) - quietOffset), s.LoadRpm(load ?? 0));
        int delta = want - lastSent;
        bool due = lastSent < 0 || delta >= 300 || rawTemp >= Critical || sensorsLost
                   || (Math.Abs(delta) >= 150 && sinceChange > TimeSpan.FromSeconds(20))
                   || (Math.Abs(delta) >= 50 && sinceChange > TimeSpan.FromSeconds(60));
        return (want, due);
    }

    /// Why quiet mode is on, or null. Heavy work overrides everything: cooling first. `call` and `media` come
    /// already gated (and already off when their setting is off).
    public static string? QuietReasonFor(Settings s, bool heavy, bool call, bool media, Signals? sig, TimeOnly now, bool sensorsLost = false) =>
        heavy || sensorsLost ? null
        : call ? "Call"
        : media ? "Media"
        : s.QuietOnDnd && sig?.DoNotDisturb == true ? "Do Not Disturb"
        : s.QuietAtNight && Settings.InRange(now, s.NightFrom, s.NightTo) ? "Night"
        : s.QuietOnBattery && sig?.OnBattery == true ? "Battery"
        : null;

    private async Task UpdateMedia()
    {
        var now = DateTime.UtcNow;
        bool playing = false;
        if (S.QuietWhenMedia && platform.MediaPlaying is { } query)
        {
            try { playing = await query(); }
            catch (Exception e) { Log.Write("media status unavailable: " + e.Message); }
        }
        MediaPlaying = mediaGate.Update(playing, now);
        // every 6 s is plenty for these, and on Linux each read starts a few small processes
        if (now - lastSignals >= TimeSpan.FromSeconds(6))
        {
            lastSignals = now;
            try { sig = platform.Signals?.Invoke(); }
            catch (Exception e) { Log.Write("signals unavailable: " + e.Message); }
        }
        bool call = callGate.Update(S.QuietOnCalls && sig?.MicInUse == true, now);
        string? reason = QuietReasonFor(S, Heavy, call, MediaPlaying, sig, TimeOnly.FromDateTime(DateTime.Now), SensorsLost);
        if (reason != QuietReason) Log.Write(reason != null ? $"quiet: {reason}" : "quiet off");
        QuietReason = reason;
        bool locked = S.LightsOffWhenLocked && sig?.Locked == true;
        if (locked != Locked) { Locked = locked; lightDirty = true; Log.Write(locked ? "locked: lights off" : "unlocked"); }
        if (Quiet != lastQuiet) { lastQuiet = Quiet; lightDirty = true; }  // dim / full brightness
    }

    private async Task ApplyLights()
    {
        var L = S.Light;
        await RunDebugScript();
        if (bootAnimPending)
        {
            bootAnimPending = false;
            if (fwPatch < 4)  // custom firmware 4+ plays the boot animation itself when a PC connects
                await PlayAnimation(L.BootAnimation(), Lighting.BootPeriod, slots: 10);  // until the yellow has fully faded out
            waveHue = null;  // fresh start at the target color
            colorShown = false;
            rgbReady = true;
            Log.Write("boot animation done");
            // temperature wave: TempWaveLoop takes over from the dark end; a lightDirty pass here would
            // reset it again right after its first breath
            lightDirty = L.Mode != LightMode.TemperatureBreathe;
            if (!lightDirty) return;
        }
        if (ReadFake() is not null)  // TempWaveLoop plays it
        {
            if (!fakeOn) { fakeOn = true; Log.Write("fake temperature on"); }
            return;
        }
        if (fakeOn) { fakeOn = false; lightDirty = true; Log.Write("fake temperature off"); }
        if (await ApplyPreview()) return;
        if (lightDirty)
        {
            if (!L.On || Locked)
            {
                if (await Dev.AckAsync(C.RgbOnOff, 0)) lightDirty = false;
                return;
            }
            await Dev.SendAsync(C.RgbOnOff, 1);
            if (L.Mode is LightMode.Temperature or LightMode.TemperatureBreathe) { lastTempBand = -1; colorShown = false; }
            else if (fwPatch >= 11 && L.Mode == LightMode.Static)
            {
                // custom firmware: steady colors in one command, cross-faded from whatever was shown
                if (!await Dev.AckAsync(0x4E, [.. L.ZoneRgb(), (byte)Math.Clamp(Brightness, 0, 100)])) return;
            }
            else
            {
                await Dev.SendAsync(68, 0);  // leave temperature-driven mode
                if (!await SendLightConfig(L.Config())) return;
            }
            lightDirty = false;
            Log.Write($"lights: {L.Mode} {string.Join(",", L.ZoneColors ?? L.Colors)} {L.Brightness}% period {L.Period}");
        }
        if (L.On && L.Mode == LightMode.Temperature)
        {
            // Space Station's "Inverter": built-in effects 1-4 by temperature band (cmd 68)
            double t = smoothTemp ?? 0;
            int band = t >= 90 ? 4 : t >= 85 ? 3 : t >= 60 ? 2 : 1;
            if (band != lastTempBand && await Dev.AckAsync(68, (byte)band)) lastTempBand = band;
        }
    }

    /// 65 prepare -> 71 x10 (index + 10 bytes) -> 67 finish.
    /// fast: chunks go out back-to-back without waiting for each ack (~0.15 s instead of ~0.6 s), so the
    /// cooler's blank-at-prepare and restart-at-finish merge into one dip that hides in the breath's dark point.
    private async Task<bool> SendLightConfig(byte[] cfg, bool fast = false)
    {
        if (!await Dev.AckAsync(65)) return false;
        for (int i = 0; i < 10; i++)
        {
            var chunk = new byte[11];
            chunk[0] = (byte)i;
            Array.Copy(cfg, i * 10, chunk, 1, Math.Min(10, cfg.Length - i * 10));
            // fast: the last chunk still waits for its ack: the cooler handles chunks in order, so that ack means
            // it's done; otherwise it drops the finish (67) and the retry 0.8 s later flashes a second time
            if (fast && i < 9)
            {
                if (!await Dev.WriteAsync(71, chunk)) return false;
                await Task.Delay(12);  // ponytail: spacing guessed; raise it if colors come out wrong
            }
            else if (!await Dev.AckAsync(71, chunk)) { Log.Write($"light chunk {i} failed"); return false; }
        }
        return await Dev.AckAsync(67);
    }

    /// Cmd 5 (no payload) switches a button-off cooler on, like its power button (tested 2026-10-12).
    private async Task OpenAsync()
    {
        var r = await Dev.SendAsync(C.OpenDevice);
        if (await Dev.SendAsync(C.IsOpen) is [var open, ..]) DeviceOpen = open == 1;
        Log.Write($"open device: reply {(r is null ? "-" : Convert.ToHexString(r))}, open={DeviceOpen}");
    }

    /// Fan stop + lights off. Smart mode never goes below 1300 rpm, so leave it and use manual gear 0.
    private async Task OffAsync()
    {
        var end = await Dev.SendAsync(C.EndSmart);
        bool gear0 = await Dev.AckAsync(C.ManualGear, 0);
        bool rgb = await Dev.AckAsync(C.RgbOnOff, 0);
        lightDirty = false;
        lastSentRpm = -1;
        Log.Write($"OFF: endSmart={(end is null ? "-" : end[0].ToString())} gear0={gear0} rgbOff={rgb}");
    }

    /// Sends a one-shot animation config and waits until it has played `slots` steps.
    private async Task PlayAnimation(byte[] cfg, int period, int slots)
    {
        await Dev.SendAsync(C.RgbOnOff, 1);
        await Dev.SendAsync(68, 0);
        // no fast burst here: during session end the BLE stack drops unacked chunks -> corrupt config, dark lights
        if (!await SendLightConfig(cfg)) return;
        await Task.Delay(TimeSpan.FromSeconds(slots * period * S.BreathSecondsPerUnit));
    }

    // Session end comes from Program via WPF's Application.SessionEnding (main thread). SystemEvents'
    // own thread was useless there: WPF shuts the app down right after and kills the blocked thread.
    public void PowerDown(string why)
    {
        Log.Write("power down: " + why);
        if (!S.OffOnShutdown) return;
        if (!Dev.Connected && !UserDisconnected)
        {
            // the link dropped shortly before (cable pulled, Bluetooth still reconnecting): one quick try, since
            // Windows takes Bluetooth down 2-4 s into a shutdown. Off the UI thread: its continuations would deadlock here.
            var c = Task.Run(Dev.ConnectAsync);
            bool ok = c.Wait(3000) && c.Result;
            Log.Write($"power down: reconnect {(ok ? "ok" : "failed")}");
            if (!ok) return;
        }
        if (!Dev.Connected) return;
        poweringDown = true;
        // blocks the caller (Program holds a ShutdownBlockReason meanwhile, so Windows waits)
        Task.Run(async () =>
        {
            bool held = await tickGate.WaitAsync(700);
            if (!held) Log.Write("power down: tick busy, sending anyway");
            try
            {
                // straight off: Windows can take Bluetooth down 2-4 s into a shutdown
                if (fwPatch >= 6) Log.Write($"close: {await Dev.AckAsync(0x4C)}");  // like the button; keeps the gear for auto-start
                else await OffAsync();  // official firmware: gear 0 is stored, so auto-start comes back with the fan stopped
                Log.Write("power down done");
            }
            finally { if (held) tickGate.Release(); }
        }).Wait(9000);
        // S.SoftOff untouched: StartupMode.Keep restores the user's own choice
    }
}

/// Turns the raw "sound right now?" samples into quiet mode on/off without flicker: on after 10 s of steady sound
/// (a silence over 7 s starts the count again, so notifications don't add up), off after 30 s of silence
/// (quiet film scenes, gaps between songs).
public sealed class MediaGate(TimeSpan? enter = null)
{
    // Gap > two sampling intervals (2 s wait + the pass: 2.5-3.5 s each): one quiet sample must not break a film
    public static readonly TimeSpan Leave = TimeSpan.FromSeconds(30), Gap = TimeSpan.FromSeconds(7);
    private readonly TimeSpan Enter = enter ?? TimeSpan.FromSeconds(10);
    private DateTime? since;  // start of the current run of sound
    private DateTime last;    // last time sound was heard
    public bool On { get; private set; }

    public bool Update(bool sound, DateTime now)
    {
        if (sound) { if (now - last > Gap) since = now; since ??= now; last = now; }  // sound after a gap: a new run
        else if (now - last > Gap) since = null;  // the run broke before quiet mode started
        if (!On && since is DateTime s && now - s >= Enter) On = true;
        else if (On && now - last >= Leave) On = false;
        return On;
    }
}
