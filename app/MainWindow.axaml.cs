using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using FluentIcons.Common;

namespace Bs1Cooler.App;

public partial class MainWindow : Window
{
    private const string TaskName = "BS1 Cooler";
    private readonly Controller ctl;
    private bool loading;

    // effect list order in the UI
    private static readonly LightMode[] Effects =
        { LightMode.TemperatureBreathe, LightMode.Static, LightMode.Breathe, LightMode.Wave, LightMode.Twinkle, LightMode.Fade };

    public MainWindow() : this(null!) { }  // previewer

    public MainWindow(Controller ctl)
    {
        this.ctl = ctl;
        InitializeComponent();
        if (ctl == null) return;
        Fallback.IsVisible = ActualTransparencyLevel == WindowTransparencyLevel.None;
        Closing += (_, e) => { e.Cancel = true; Hide(); };  // close = back to tray
        Deactivated += (_, _) => { if (!Busy) Hide(); };     // a tray flyout: click elsewhere closes it
        Wire();
        WireFirmware();
        WireLanguage();
        WireColors();
        WireTemps();
        Load();
        Refresh();
        ctl.Changed += () => Dispatcher.UIThread.Post(Refresh);
    }

    private Settings S => ctl.S;
    private Lighting L => S.Light;

    public void ShowNearTray()
    {
        Show();
        var area = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary!.WorkingArea;
        double scale = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
        Position = new PixelPoint(area.Right - (int)(Width * scale) - 12, area.Bottom - (int)(Height * scale) - 12);
        Activate();
    }

    private void Wire()
    {
        PowerSwitch.IsCheckedChanged += (_, _) => { if (!loading) ctl.SetOn(PowerSwitch.IsChecked == true); };
        // two toggles acting as one segmented control
        SmartRadio.Click += (_, _) => { SmartRadio.IsChecked = true; ManualRadio.IsChecked = false; FanModeChanged(); };
        ManualRadio.Click += (_, _) => { ManualRadio.IsChecked = true; SmartRadio.IsChecked = false; FanModeChanged(); };
        ManualSlider.ValueChanged += (_, _) =>
        {
            ManualText.Text = $"{ManualSlider.Value:0} {Tx.Tr("rpm")}";
            if (loading) return;
            S.FixedRpm = (int)ManualSlider.Value;
            ctl.Save();
        };
        LightSwitch.IsCheckedChanged += (_, _) => LightChanged();
        EffectBox.SelectionChanged += (_, _) => LightChanged();
        BrightSlider.ValueChanged += (_, _) => BrightText.Text = $"{BrightSlider.Value:0}%";
        // one send when the drag ends, not one per pixel
        BrightSlider.AddHandler(PointerReleasedEvent, (_, _) => CommitBrightness(), RoutingStrategies.Tunnel, handledEventsToo: true);
        BrightSlider.KeyUp += (_, _) => CommitBrightness();

        SettingsBtn.Click += async (_, _) =>
        {
            ShowPage(SettingsPage);  // at once; the logon task check runs in the background
            bool on = await Task.Run(TaskExists);
            loading = true;
            StartWithWindows.IsChecked = on;
            loading = false;
        };
        BackBtn.Click += (_, _) => ShowPage(Home);
        foreach (var t in new[] { OffOnShutdown, QuietWhenMedia, QuietOnCalls, QuietOnDnd, QuietOnBattery, QuietAtNight, LightsOffWhenLocked, AutoConnect })
            t.IsCheckedChanged += (_, _) => Behaviour();
        foreach (var box in new[] { NightFrom, NightTo }) box.LostFocus += (_, _) => Behaviour();
        OnStartup.SelectionChanged += (_, _) => Behaviour();
        StartWithWindows.IsCheckedChanged += async (_, _) =>
        {
            if (loading) return;
            try { await SetStartWithWindows(StartWithWindows.IsChecked == true); }
            catch (Exception e) { Log.Write("start at login: " + e.Message); StartWithWindows.IsEnabled = true; }  // async void
        };
        // the report goes on the clipboard: paste it into the new issue
        ReportBtn.Click += async (_, _) => { await CopyReport(); Open("https://github.com/phoseinq/flydigi-bs1-controller/issues/new"); };
        CopyReportBtn.Click += async (_, _) => await CopyReport();
        OpenLogBtn.Click += (_, _) => Open(System.IO.Path.Combine(Settings.Dir, "bs1cooler.log"));
        DumpSensorsBtn.Click += (_, _) => Log.Write("all temps: " + ctl.SensorDump());
        EditionBox.SelectionChanged += (_, _) =>
        {
            if (loading || EditionBox.SelectedIndex < 0) return;
            S.Edition = (Edition)EditionBox.SelectedIndex;
            ctl.Save();
            DebugTools.IsVisible = S.Edition == Edition.Beta;
            RefreshFirmware();
        };
    }

    private void Load()
    {
        loading = true;
        SmartRadio.IsChecked = S.FanMode == FanMode.Smart;
        ManualRadio.IsChecked = S.FanMode != FanMode.Smart;
        ManualSlider.Value = S.FixedRpm;
        LightSwitch.IsChecked = L.On;
        EffectBox.SelectedIndex = Math.Max(0, Array.IndexOf(Effects, L.Mode));
        BrightSlider.Value = L.Brightness;
        OffOnShutdown.IsChecked = S.OffOnShutdown;
        QuietWhenMedia.IsChecked = S.QuietWhenMedia;
        QuietOnCalls.IsChecked = S.QuietOnCalls;
        QuietOnDnd.IsChecked = S.QuietOnDnd;
        QuietOnBattery.IsChecked = S.QuietOnBattery;
        QuietAtNight.IsChecked = S.QuietAtNight;
        LightsOffWhenLocked.IsChecked = S.LightsOffWhenLocked;
        NightFrom.Text = S.NightFrom;
        NightTo.Text = S.NightTo;
        NightHours.IsVisible = S.QuietAtNight;
        AutoConnect.IsChecked = S.AutoConnect;
        OnStartup.SelectedIndex = (int)S.OnStartup;
        EditionBox.SelectedIndex = (int)S.Edition;
        if (!OperatingSystem.IsWindows()) StartTitle.Text = Tx.Tr("Start at login");
        DebugTools.IsVisible = S.Edition == Edition.Beta;
        AboutText.Text = $"BS1 Cooler {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        UpdateVisibility();
        loading = false;
        ShowColor();
    }

    private void Refresh()
    {
        bool usable = ctl.Dev.Connected && ctl.DeviceOpen == true;
        loading = true;
        PowerSwitch.IsEnabled = usable;
        PowerSwitch.IsChecked = usable && !S.SoftOff;
        loading = false;
        StatusText.Text = Tx.Tr(ctl.Status);
        QuietBadge.IsVisible = QuietPill.IsVisible = ctl.Quiet;
        HeavyPill.IsVisible = ctl.Heavy;
        HeavyText.Text = $"{Tx.Tr("Heavy load")} {ctl.Load:0}%";
        QuietText.Text = ctl.QuietReason is { } why ? $"{Tx.Tr("Quiet")} · {Tx.Tr(why)}" : Tx.Tr("Quiet");
        LinkIcon.Symbol = ctl.Dev.OverUsb ? Symbol.UsbPlug : Symbol.BluetoothConnected;
        LinkIcon.IsVisible = ctl.Dev.Connected;
        RpmText.Text = ctl.Dev.Connected ? $"{ctl.CurrentRpm}" : "–";
        CpuText.Text = Fmt(ctl.Cpu);
        GpuText.Text = Fmt(ctl.Gpu);
        RefreshFirmware();
        RefreshTemps();
        static string Fmt(double? t) => t is null ? "–" : $"{t:0}";
    }

    public event Action? LanguageChanged;

    private void WireLanguage()
    {
        string lang = S.Language;
        if (Array.IndexOf(Tx.Codes, lang) < 0)  // first run: follow Windows
        {
            var ui = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            lang = ui is "fa" or "zh" ? ui : "en";
        }
        LanguageBox.SelectedIndex = SetupLanguage.SelectedIndex = Array.IndexOf(Tx.Codes, lang);
        SetLanguage(lang);
        LanguageBox.SelectionChanged += (_, _) => { if (!loading) SetLanguage(Tx.Codes[LanguageBox.SelectedIndex], save: true); };
        SetupLanguage.SelectionChanged += (_, _) => { if (!loading) SetLanguage(Tx.Codes[SetupLanguage.SelectedIndex], save: true); };
    }

    private void SetLanguage(string lang, bool save = false)
    {
        Tx.Lang = lang;
        if (save) { S.Language = lang; ctl.Save(); }
        FlowDirection = lang == "fa" ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        loading = true;
        foreach (var o in this.GetLogicalDescendants())
        {
            switch (o)
            {
                case TextBlock t when t.Text != null: t.Text = Tx.Retranslate(t.Text); break;
                case Avalonia.Controls.Primitives.HeaderedContentControl h when h.Header is string hs: h.Header = Tx.Retranslate(hs); break;
                case ContentControl c when c.Content is string cs: c.Content = Tx.Retranslate(cs); break;
                case FluentAvalonia.UI.Controls.FAInfoBar b:
                    if (b.Title != null) b.Title = Tx.Retranslate(b.Title);
                    if (b.Message != null) b.Message = Tx.Retranslate(b.Message);
                    break;
                case FluentIcons.Avalonia.SymbolIcon { Classes: var cl } ic when cl.Contains("back"):
                    ic.Symbol = lang == "fa" ? Symbol.ArrowRight : Symbol.ArrowLeft;
                    break;
                case ComboBox box:  // the closed box shows a copy of the item: reselect to refresh it
                    Dispatcher.UIThread.Post(() =>  // read the index now: Load() may set it after this pass
                    {
                        int i = box.SelectedIndex;
                        if (i < 0) return;
                        loading = true; box.SelectedIndex = -1; box.SelectedIndex = i; loading = false;
                    });
                    break;
            }
        }
        LanguageBox.SelectedIndex = SetupLanguage.SelectedIndex = Array.IndexOf(Tx.Codes, lang);
        loading = false;
        LanguageChanged?.Invoke();
        if (ctl.Dev != null) Refresh();
    }

    // bright enough to read on the LEDs; the first is the default
    private static readonly string[] Palette = ["#00FFBB", "#00A2FF", "#7B5CFF", "#FF3CAC", "#FF3B30", "#FF9500", "#FFD60A"];

    private void WireColors()
    {
        foreach (var hex in Palette)
        {
            var b = new Button { Classes = { "swatch" }, Tag = hex, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)) };
            b.Click += (_, _) => SetColor(hex);
            Swatches.Children.Insert(Swatches.Children.Count - 1, b);
        }
        CustomColor.ColorChanged += (_, e) => { if (!loading) SetColor($"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}"); };
    }

    private void SetColor(string hex)
    {
        L.Colors = [hex];
        L.ZoneColors = null;  // one color for all zones
        ShowColor();
        ctl.Save(lightsChanged: true);
    }

    private void ShowColor()
    {
        string now = L.Colors.Count > 0 ? L.Colors[0] : Palette[0];
        foreach (var b in Swatches.Children.OfType<Button>()) b.Classes.Set("chosen", string.Equals(b.Tag as string, now, StringComparison.OrdinalIgnoreCase));
        loading = true;
        CustomColor.Color = Avalonia.Media.Color.Parse(now);
        loading = false;
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// Version, OS, cooler state and the end of the log, on the clipboard: paste it into a message or an issue.
    private async Task CopyReport()
    {
        string log = "";
        try
        {
            var lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(Settings.Dir, "bs1cooler.log"));
            log = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 150)));
        }
        catch { }
        string report = $"""
            BS1 Cooler {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} ({S.Edition}, {Tx.Lang})
            {Environment.OSVersion} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}
            cooler: {ctl.Status}; firmware {ctl.FirmwareVersion ?? "?"} custom {ctl.FirmwarePatch}; {(ctl.Dev.OverUsb ? "USB" : "Bluetooth")}
            temps: cpu {ctl.Cpu:0.0} gpu {ctl.Gpu:0.0}; load {ctl.Load:0}%; quiet {ctl.QuietReason ?? "off"}
            sensors: {ctl.SensorDump()}

            {log}
            """;
        if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, report);
    }

    private void UpdateVisibility()
    {
        bool smart = S.FanMode == FanMode.Smart;
        ManualRow.IsVisible = !smart;
        SmartHint.IsVisible = smart;
        LightPanel.IsEnabled = L.On;
        ColorRow.IsVisible = L.Mode is not (LightMode.Temperature or LightMode.TemperatureBreathe);
    }

    private void FanModeChanged()
    {
        if (loading) return;
        S.FanMode = SmartRadio.IsChecked == true ? FanMode.Smart : FanMode.Fixed;
        // switching fan mode picks the matching light: color by fan speed / the manual warm white
        L.Mode = S.FanMode == FanMode.Smart ? LightMode.TemperatureBreathe : LightMode.Static;
        L.ZoneColors = S.FanMode == FanMode.Fixed ? new(Lighting.ManualColors) : null;
        loading = true;
        EffectBox.SelectedIndex = Array.IndexOf(Effects, L.Mode);
        loading = false;
        UpdateVisibility();
        ctl.Save(lightsChanged: true);
    }

    private void LightChanged()
    {
        if (loading) return;
        L.On = LightSwitch.IsChecked == true;
        var mode = Effects[Math.Max(0, EffectBox.SelectedIndex)];
        if (mode != L.Mode) L.ZoneColors = null;  // a new effect shows the chosen color, not old zones
        L.Mode = mode;
        UpdateVisibility();
        ctl.Save(lightsChanged: true);
    }

    private void CommitBrightness()
    {
        if (loading || (int)BrightSlider.Value == L.Brightness) return;
        L.Brightness = (int)BrightSlider.Value;
        ctl.Save(lightsChanged: true);
    }

    private void Behaviour()
    {
        if (loading) return;
        S.OffOnShutdown = OffOnShutdown.IsChecked == true;
        S.QuietWhenMedia = QuietWhenMedia.IsChecked == true;
        S.QuietOnCalls = QuietOnCalls.IsChecked == true;
        S.QuietOnDnd = QuietOnDnd.IsChecked == true;
        S.QuietOnBattery = QuietOnBattery.IsChecked == true;
        S.QuietAtNight = QuietAtNight.IsChecked == true;
        S.LightsOffWhenLocked = LightsOffWhenLocked.IsChecked == true;
        NightHours.IsVisible = S.QuietAtNight;
        // keep a typed time only if it reads as one ("7:30", "23:00"); show it back normalized
        if (TimeOnly.TryParse(NightFrom.Text, out var f)) S.NightFrom = f.ToString("HH:mm");
        if (TimeOnly.TryParse(NightTo.Text, out var t)) S.NightTo = t.ToString("HH:mm");
        NightFrom.Text = S.NightFrom;
        NightTo.Text = S.NightTo;
        S.AutoConnect = AutoConnect.IsChecked == true;
        S.OnStartup = (StartupMode)Math.Max(0, OnStartup.SelectedIndex);
        ctl.Save();
    }

    // Windows: logon task with highest privileges (CPU temperature needs admin, and no UAC prompt at sign-in).
    // Linux: an XDG autostart entry. macOS: a LaunchAgent.
    private static readonly string LinuxEntry = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "bs1cooler.desktop");
    private static readonly string MacAgent = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.phoseinq.bs1cooler.plist");

    private async Task SetStartWithWindows(bool want)
    {
        StartWithWindows.IsEnabled = false;
        await Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                RunPowerShell(want
                    ? $"Register-ScheduledTask -TaskName '{TaskName}' -Force -RunLevel Highest " +
                      $"-Action (New-ScheduledTaskAction -Execute '{Environment.ProcessPath}' -Argument '--tray') " +
                      "-Trigger (New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME) " +
                      "-Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0)"
                    : $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false");
            else
            {
                string file = OperatingSystem.IsMacOS() ? MacAgent : LinuxEntry;
                if (!want) { System.IO.File.Delete(file); return; }
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                System.IO.File.WriteAllText(file, OperatingSystem.IsMacOS()
                    ? $"""
                      <?xml version="1.0" encoding="UTF-8"?>
                      <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                      <plist version="1.0"><dict>
                        <key>Label</key><string>com.phoseinq.bs1cooler</string>
                        <key>ProgramArguments</key><array><string>{Environment.ProcessPath}</string><string>--tray</string></array>
                        <key>RunAtLoad</key><true/>
                      </dict></plist>
                      """
                    : $"""
                      [Desktop Entry]
                      Type=Application
                      Name=BS1 Cooler
                      Exec="{Environment.ProcessPath}" --tray
                      X-GNOME-Autostart-enabled=true
                      """);
            }
        });
        bool on = await Task.Run(TaskExists);
        loading = true;
        StartWithWindows.IsChecked = on;
        loading = false;
        StartWithWindows.IsEnabled = true;
        Log.Write($"start with windows: {StartWithWindows.IsChecked}");
    }

    private static bool TaskExists()  // schtasks answers in ~50 ms; PowerShell takes a second
    {
        if (!OperatingSystem.IsWindows()) return System.IO.File.Exists(OperatingSystem.IsMacOS() ? MacAgent : LinuxEntry);
        var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        return Flasher.ExitCode(p, 5000) == 0;
    }

    private static int RunPowerShell(string script)
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"")
        {
            CreateNoWindow = true, UseShellExecute = false,
        })!;
        return Flasher.ExitCode(p, 15000);
    }
}
