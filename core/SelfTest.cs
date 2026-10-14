using System;

namespace Bs1Cooler;

public static class SelfTest
{
    /// Checks the byte-level bits against frames captured from Space Station.
    public static int Run()
    {
        static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
        try
        {
            Check(Convert.ToHexString(CoolerDevice.Frame(70, new byte[] { 0 })) == "5AA546030049", "frame checksum");
            var n0 = new TimeOnly(23, 30); var n1 = new TimeOnly(6, 59); var n2 = new TimeOnly(7, 0); var n3 = new TimeOnly(12, 0);
            Check(Settings.InRange(n0, "23:00", "07:00") && Settings.InRange(n1, "23:00", "07:00") && !Settings.InRange(n2, "23:00", "07:00")
                  && !Settings.InRange(n3, "23:00", "07:00") && Settings.InRange(n3, "09:00", "17:00") && !Settings.InRange(n3, "x", "07:00"), "night hours, across midnight");
            var call = new MediaGate(TimeSpan.Zero); var c0 = new DateTime(2026, 1, 1);
            Check(call.Update(true, c0) && call.Update(false, c0.AddSeconds(29)) && !call.Update(false, c0.AddSeconds(31)), "calls: quiet at once, 30 s hold");
            {
                var q = new Settings(); var noon = new TimeOnly(12, 0); var late = new TimeOnly(23, 30);
                Signals Sig(bool mic = false, bool bat = false, bool dnd = false) => new(mic, bat, dnd, false);
                Check(Controller.QuietReasonFor(q, false, false, false, Sig(), noon) == null, "quiet: nothing going on");
                Check(Controller.QuietReasonFor(q, false, true, true, Sig(), late) == "Call", "quiet: a call names itself first");
                Check(Controller.QuietReasonFor(q, false, false, true, Sig(), noon) == "Media", "quiet: media");
                Check(Controller.QuietReasonFor(q, false, false, false, Sig(dnd: true), noon) == "Do Not Disturb", "quiet: DND / presenting");
                Check(Controller.QuietReasonFor(q, false, false, false, Sig(), late) == "Night", "quiet: night");
                Check(Controller.QuietReasonFor(q, false, false, false, Sig(bat: true), noon) == "Battery", "quiet: battery");
                Check(Controller.QuietReasonFor(q, false, false, false, null, late) == "Night", "quiet: night needs no OS signals");
                Check(Controller.QuietReasonFor(q, true, true, true, Sig(true, true, true), late) == null, "quiet: heavy load beats every reason");
                q.QuietAtNight = q.QuietOnBattery = q.QuietOnDnd = false;
                Check(Controller.QuietReasonFor(q, false, false, false, Sig(true, true, true), late) == null, "quiet: switched-off reasons don't count");
            }
            {
                var f = new Settings(); var min = TimeSpan.FromMinutes(5);
                Check(Controller.FanTarget(f, 45, 45, 0, 5, false, 1300, min) == (1300, false), "fan: cool and idle stays put");
                Check(Controller.FanTarget(f, 45, 45, 0, 5, true, 1300, TimeSpan.Zero) == (Controller.SafeRpm, true), "fan: lost sensors -> safe speed at once");
                Check(Controller.FanTarget(f, 70, 92, 0, 5, false, 2250, TimeSpan.Zero).due, "fan: critical heat skips the step limits");
                Check(Controller.FanTarget(f, 55, 55, 0, 5, false, 1500, TimeSpan.FromSeconds(30)) == (1550, false), "fan: a 50 rpm gap waits");
                Check(Controller.FanTarget(f, 55, 55, 0, 5, false, 1500, TimeSpan.FromSeconds(61)).due, "fan: ... and settles after a minute");
                Check(Controller.FanTarget(f, 45, 45, 0, 40, false, 1300, TimeSpan.Zero) == (1900, true), "fan: heavy load floor jumps at once");
                Check(Controller.QuietReasonFor(f, false, true, true, null, new TimeOnly(23, 30), sensorsLost: true) == null, "quiet: never while sensors are lost");
            }
            var ls = new Settings();
            Check(ls.LoadRpm(5) == 1300 && ls.LoadRpm(40) == 1900 && ls.LoadRpm(55) == 2150 && ls.LoadRpm(100) == 2800, "load floor");
            var cs = new Settings();
            Check(cs.CurveRpm(40) == 1300 && cs.CurveRpm(50) == 1300 && cs.CurveRpm(55) == 1550 && cs.CurveRpm(70) == 2300, "curve: lowest speed while cool");
            {
                var g = new MediaGate(); var t0 = new DateTime(2026, 1, 1);
                bool At(double sec, bool sound) => g.Update(sound, t0.AddSeconds(sec));
                bool on = false;
                for (int i = 0; i < 10; i++) on |= At(i, true);
                Check(!on, "quiet: not before 10 s of sound");
                Check(At(10, true), "quiet: on after 10 s of steady sound");
                Check(At(25, false) && At(39, false) && !At(40, false), "quiet: holds through 30 s of silence, then off");
                var b = new MediaGate(); bool any = false;
                for (int i = 0; i <= 60; i++) any |= b.Update(i % 3 == 0, t0.AddSeconds(i));  // a notification every 3 s
                Check(!any, "quiet: separate notifications never add up");
                var m = new MediaGate(); bool late = false;
                for (int i = 0; i <= 12; i++) late |= m.Update(i % 2 == 0, t0.AddSeconds(i));  // music, a missed sample now and then
                Check(late, "quiet: one missed sample doesn't break a song");
            }
            var teal = new Lighting { Mode = LightMode.Static, Colors = new() { "#00FFBB" }, Brightness = 20 }.Config();
            Check(teal.Length == 96, "config length");
            Check(Convert.ToHexString(teal, 0, 10) == "000200000F1400FFBB00", "static config head (Space Station chunk 0)");
            Check(Convert.ToHexString(teal, 36, 3) == "00FFBB" && Convert.ToHexString(teal, 66, 3) == "00FFBB", "all 3 zones");
            var zoned = new Lighting { Mode = LightMode.Static, ZoneColors = new() { "#FF0000", "#00FF00", "#0000FF" } }.Config();
            Check(Convert.ToHexString(zoned, 6, 3) + Convert.ToHexString(zoned, 36, 3) + Convert.ToHexString(zoned, 66, 3) == "FF000000FF000000FF", "per-zone");
            var breathe = new Lighting { Mode = LightMode.Breathe, Colors = new() { "#FF0000", "#0000FF" }, Period = 30 }.Config();
            Check(breathe[3] == 3 && breathe[4] == 30, "breathe loop end / period");
            Check(Convert.ToHexString(breathe, 6, 15) == "000000FF0000000000" + "0000FF" + "000000", "breathe slots: gap,c1,gap,c2,gap");
            Check(Lighting.TemperatureColor(30) == (44, "#0055FF") && Lighting.TemperatureColor(95) == (0, "#FF0000"), "temperature color ends");
            Check(Lighting.TemperatureColor(72.5).color == "#00FF00" || Lighting.TemperatureColor(72.5).step == 22, "temperature color middle ~green");
            var tb = new Lighting { Mode = LightMode.TemperatureBreathe, Period = 19 }.Config("#FF0000");
            Check(tb[3] == 0 && Convert.ToHexString(tb, 6, 3) + Convert.ToHexString(tb, 36, 3) + Convert.ToHexString(tb, 66, 3) == "FF0000FF0000FF0000", "temperature: steady color, all zones");
            var wave = new Lighting { Mode = LightMode.Wave, Colors = new() { "#FF0000", "#00FF00", "#0000FF" }, Period = 3 }.Config();
            Check(wave[3] == 8 && wave[4] == 3, "wave loop: 3 colors x 3 = 9 slots");
            // zone 0 starts red, zone 1 one third later (green), zone 2 two thirds later (blue)
            Check(Convert.ToHexString(wave, 6, 3) + Convert.ToHexString(wave, 36, 3) + Convert.ToHexString(wave, 66, 3) == "FF000000FF000000FF", "wave phase shift");
            var water = new Lighting { Mode = LightMode.Wave, Colors = new() { "#0060FF" }, Period = 10 }.Config();
            Check(water[3] == 1, "water: breathe's 2-slot loop");
            Check(Convert.ToHexString(water, 6, 6) + Convert.ToHexString(water, 36, 6) + Convert.ToHexString(water, 66, 6)
                  == "0000000060FF" + "000000003EA6" + "000000002666", "water: off together, zones 100/65/40%");
            Check(Lighting.RpmHue(1500) == 220 && Lighting.RpmHue(1800) is > 170 and < 180 && Lighting.RpmHue(3000) == 0 && Lighting.RpmHue(1000) == 220, "wave color by rpm");
            var ramp = new Lighting().Ramp(new double[] { 0, 2, 4, 6, 8, 120, 120, 120, 120, 240 }, 5);
            Check(ramp[3] == 9 && ramp[4] == 5 && Convert.ToHexString(ramp, 6, 3) == "FF0000" && Convert.ToHexString(ramp, 36 + 15, 3) == "00FF00"
                  && Convert.ToHexString(ramp, 66 + 27, 3) == "0000FF", "ramp: one hue per slot, all zones");
            var boot = new Lighting().BootAnimation();
            Check(Convert.ToHexString(boot, 6, 3) + Convert.ToHexString(boot, 36, 3) + Convert.ToHexString(boot, 66, 3) == "000000000000000000", "boot: starts dark");
            Check(boot[6 + 3] < 0x80 && boot[36 + 6] < 0x80 && boot[66 + 9] == 0xFF && boot[6 + 9] == 0, "boot: dim pulse runs 1 -> 3, lands full");
            Check(boot[66 + 12] == 0xFF && boot[6 + 12] < 0x80 && boot[36 + 15] == 0xFF && boot[6 + 18] == 0xFF && boot[4] == 7, "boot: zone 3 stays, next lands at 2, then 1; slower");
            Check(boot[6 + 24] == 0 && boot[66 + 27] == 0 && boot[6 + 21] == 0xFF, "boot: glow, then 2 dark slots for the handover");
            Check(new Lighting().Mode == LightMode.TemperatureBreathe && new Lighting().Brightness == 22, "defaults = temperature wave");
            var s = new Settings();
            Check(s.CurveRpm(0) == 1300 && s.CurveRpm(55) == 1550 && s.CurveRpm(95) == 3000, "curve");
            // WCH ISP (wchisp's encoding): identify is cmd, len 0x12, ids, magic; key from this cooler's UID
            Check(Convert.ToHexString(WchIsp.Identify()) == "A112000000" + "4D4355204953502026205743482E434E", "isp identify");
            var key = WchIsp.Key(Convert.FromHexString("EFCE9A10535CDC3B"));
            Check(key[0] == 0x2D && key[6] == 0x2D && key[7] == 0x2D + 0x71, "isp key");
            Console.WriteLine("selftest ok");
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e.Message); return 1; }
    }
}
