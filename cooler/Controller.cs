using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Media.Control;
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
    public int CurrentRpm, TargetRpm, DeviceMode = -2;  // -1 gear 0 (stopped), 0-3 gears, 4 smart, -2 unknown
    public bool? DeviceOpen;
    public bool UserDisconnected { get; private set; }

    private Temps? temps;
    private double? smoothTemp;   // asymmetric EMA of max(cpu, gpu): what the curve sees
    private double? rawTemp, prevRaw;
    private int lastSentRpm = -1, lastTempBand = -1;
    private DateTime lastSpeedChange;
    private GlobalSystemMediaTransportControlsSessionManager? media;
    public bool MediaPlaying { get; private set; }
    public bool Quiet => S.QuietWhenMedia && MediaPlaying && (rawTemp ?? 0) < 85;
    private bool lightDirty = true, startupPending = true, poweringDown, connectRequested, justConnected;
    private bool bootAnimPending = true;  // played once per app start (= per Windows logon)
    private bool rgbReady;  // boot animation just left RGB on: the wave's first send goes straight in
    private readonly SemaphoreSlim tickGate = new(1, 1);
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime lastConnectTry;

    public Controller()
    {
        // every start (= every boot) undoes whatever the user fiddled with: Smart fan + the temperature wave
        S.FanMode = FanMode.Smart;
        S.Light = new Lighting();
        S.Save();
        Dev = new CoolerDevice(S.Address);
        Dev.Pulse += OnPulse;
        Dev.ConnectionChanged += () =>
        {
            if (Dev.Connected) justConnected = true;
            else { DeviceOpen = null; DeviceMode = -2; CurrentRpm = TargetRpm = 0; }
            Changed?.Invoke();
            Kick();
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend) PowerDown("suspend");
            else if (e.Mode == PowerModes.Resume)
            {
                Log.Write("resume");
                poweringDown = false;
                startupPending = lightDirty = true;
                Kick();
            }
        };
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
        : $"{CurrentRpm} rpm";

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
        try { temps = new Temps(); var (c, g) = temps.Read(); Log.Write($"sensors: cpu={c?.ToString("0.0") ?? "none"} gpu={g?.ToString("0.0") ?? "none"}"); }
        catch (Exception e) { Log.Write("temperature sensors unavailable: " + e.Message); }
        Dev.StartWatching(() => { if (!Dev.Connected) Kick(); });  // TryConnect decides whether to act
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
        (Cpu, Gpu) = temps.Read();
        rawTemp = Cpu is null && Gpu is null ? null : Math.Max(Cpu ?? 0, Gpu ?? 0);
        if (rawTemp is not double r) return;
        // laptop CPUs spike for a second or two all the time: use the lower of the last two samples (a lone
        // spike never counts), then follow heat up in ~12 s, down in ~25 s (2 s ticks). The old instant
        // snap at 88 C let single package spikes pin the fan near 2900 rpm at 6% load.
        double t = Math.Min(r, prevRaw ?? r);
        prevRaw = r;
        if (smoothTemp is not double s || t >= 95) smoothTemp = t;  // first sample / sustained and too hot to wait
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
                lastSentRpm = lastTempBand = -1;
                lightDirty = true;
                if (startupPending)
                {
                    startupPending = false;
                    if (S.OnStartup != StartupMode.Keep) { S.SoftOff = S.OnStartup == StartupMode.Off; S.Save(); }
                    if (S.SoftOff || !S.Light.On) bootAnimPending = false;  // after the rule: it may turn us on
                    Log.Write($"startup rule {S.OnStartup} => {(S.SoftOff ? "off" : "on")}");
                }
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
                // quiet while something plays: read the curve 8°C lower
                want = S.CurveRpm((smoothTemp ?? 60) - (Quiet ? 8 : 0));
                int delta = want - lastSentRpm;
                // fewer, bigger steps: an audible change at most every 20 s, unless heat jumps
                due = lastSentRpm < 0 || delta >= 300
                      || (Math.Abs(delta) >= 150 && DateTime.Now - lastSpeedChange > TimeSpan.FromSeconds(20));
            }
            want = (int)Math.Round(Math.Clamp(want, 1300, 3000) / 50.0) * 50;
            if (due && want != lastSentRpm)
            {
                if (await Dev.AckAsync(C.SetSpeed, (byte)want, (byte)(want >> 8)))
                {
                    lastSentRpm = want;
                    lastSpeedChange = DateTime.Now;
                    Log.Write($"fan -> {want} rpm (cpu {Cpu:0.0} gpu {Gpu:0.0} smooth {smoothTemp:0.0}{(Quiet ? " quiet" : "")}, now {CurrentRpm})");
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

    /// What the temperature wave should show now, or null when it shouldn't run.
    private (Lighting L, double hue)? TempWaveSource()
    {
        if (!Dev.Connected || DeviceOpen != true || S.SoftOff || poweringDown || UserDisconnected || bootAnimPending) return null;
        var L = S.Light;
        if (ReadFake() is var (_, fr))
            return (new Lighting { Mode = LightMode.TemperatureBreathe, Colors = L.Colors, Brightness = L.Brightness, Period = L.Period }, Lighting.RpmHue(fr));
        if (!L.On || L.Mode != LightMode.TemperatureBreathe || previewStamp != default) return null;
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

    private async Task UpdateMedia()
    {
        if (!S.QuietWhenMedia) { MediaPlaying = false; return; }
        try
        {
            media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            bool playing = media.GetSessions().Any(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            if (playing != MediaPlaying) Log.Write(playing ? "media playing: quiet" : "media stopped");
            MediaPlaying = playing;
        }
        catch (Exception e) { Log.Write("media status unavailable: " + e.Message); S.QuietWhenMedia = false; }
    }

    private async Task ApplyLights()
    {
        var L = S.Light;
        await RunDebugScript();
        if (bootAnimPending)
        {
            bootAnimPending = false;
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
            if (!L.On)
            {
                if (await Dev.AckAsync(C.RgbOnOff, 0)) lightDirty = false;
                return;
            }
            await Dev.SendAsync(C.RgbOnOff, 1);
            if (L.Mode is LightMode.Temperature or LightMode.TemperatureBreathe) { lastTempBand = -1; colorShown = false; }
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
        if (!S.OffOnShutdown || !Dev.Connected) return;
        poweringDown = true;
        // blocks the caller (Program holds a ShutdownBlockReason meanwhile, so Windows waits)
        Task.Run(async () =>
        {
            bool held = await tickGate.WaitAsync(700);
            if (!held) Log.Write("power down: tick busy, sending anyway");
            try
            {
                await OffAsync();  // straight off: Windows can take Bluetooth down 2-4 s into a shutdown
                Log.Write("power down done");
            }
            finally { if (held) tickGate.Release(); }
        }).Wait(9000);
        // S.SoftOff untouched: StartupMode.Keep restores the user's own choice
    }
}
