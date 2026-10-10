using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace Bs1Cooler;

public partial class MainWindow : Window
{
    private const string TaskName = "BS1 Cooler";
    private readonly Controller ctl;
    private bool loading;

    public MainWindow(Controller ctl)
    {
        this.ctl = ctl;
        InitializeComponent();
        LoadSettings();
        Refresh();
        ctl.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Closing += (_, e) => { e.Cancel = true; Hide(); };  // close = back to tray
    }

    private Settings S => ctl.S;
    private Lighting L => S.Light;

    private void LoadSettings()
    {
        loading = true;
        (S.FanMode == FanMode.Smart ? SmartRadio : FixedRadio).IsChecked = true;
        FixedSlider.Value = S.FixedRpm;
        LoadLighting();
        OffOnShutdown.IsChecked = S.OffOnShutdown;
        QuietWhenMedia.IsChecked = S.QuietWhenMedia;
        AutoConnect.IsChecked = S.AutoConnect;
        OnStartup.SelectedIndex = (int)S.OnStartup;
        StartWithWindows.IsChecked = TaskExists();
        loading = false;
    }

    private void LoadLighting()
    {
        bool was = loading;
        loading = true;
        LightOn.IsChecked = L.On;
        LightMode.SelectedIndex = (int)L.Mode;
        PerZone.IsChecked = L.ZoneColors != null;
        Period.Value = L.Period;
        Bright.Value = L.Brightness;
        BuildSwatches();
        UpdateVisibility();
        loading = was;
    }

    private void Refresh()
    {
        StatusText.Text = ctl.Status;
        TempText.Text = (ctl.Dev.Connected && !S.SoftOff ? $"target {ctl.TargetRpm}{(ctl.Quiet && S.FanMode == FanMode.Smart ? " (quiet)" : "")}   ·   " : "")
                        + $"CPU {(ctl.Cpu is null && ctl.Gpu is not null ? "n/a" : Fmt(ctl.Cpu))}   GPU {Fmt(ctl.Gpu)}";
        bool usable = ctl.Dev.Connected && ctl.DeviceOpen == true;
        PowerBtn.IsEnabled = usable;
        PowerBtn.IsChecked = usable && !S.SoftOff;
        PowerBtn.Content = PowerBtn.IsChecked == true ? "On" : "Off";
        LinkBtn.Content = ctl.Dev.Connected ? "Disconnect" : "Connect";
        static string Fmt(double? t) => t is null ? "–" : $"{t:0}°";
    }

    private void UpdateVisibility()
    {
        FixedRow.Visibility = S.FanMode == FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        LightPanel.IsEnabled = L.On;
        var mode = L.Mode;
        ColorRow.Visibility = mode is Bs1Cooler.LightMode.Temperature or Bs1Cooler.LightMode.TemperatureBreathe ? Visibility.Collapsed : Visibility.Visible;
        SpeedRow.Visibility = mode is Bs1Cooler.LightMode.Static or Bs1Cooler.LightMode.Temperature ? Visibility.Collapsed : Visibility.Visible;
        PerZone.Visibility = mode == Bs1Cooler.LightMode.Static ? Visibility.Visible : Visibility.Collapsed;
        AddColor.Visibility = mode == Bs1Cooler.LightMode.Static || L.Colors.Count >= 5 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- power / fan ----

    private void Power_Click(object sender, RoutedEventArgs e) => ctl.SetOn(PowerBtn.IsChecked == true);

    private void Fan_Click(object sender, RoutedEventArgs e)
    {
        S.FanMode = SmartRadio.IsChecked == true ? FanMode.Smart : FanMode.Fixed;
        // switching fan mode picks the matching effect; edits after that stick until the next switch
        L.Mode = S.FanMode == FanMode.Smart ? Bs1Cooler.LightMode.TemperatureBreathe : Bs1Cooler.LightMode.Static;
        LoadLighting();
        ctl.Save(lightsChanged: true);
    }

    private void FixedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        FixedText.Text = $"{FixedSlider.Value:0} rpm";
        if (loading) return;
        S.FixedRpm = (int)FixedSlider.Value;
        ctl.Save();
    }

    // ---- lighting ----

    private void Light_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        L.On = LightOn.IsChecked == true;
        L.Mode = (LightMode)Math.Max(0, LightMode.SelectedIndex);
        UpdateVisibility();
        BuildSwatches();
        ctl.Save(lightsChanged: true);
    }

    private void Bright_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => BrightText.Text = $"{Bright.Value:0}%";
    private void Period_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => PeriodText.Text = $"{Period.Value:0}";

    // sliders send on release, not on every pixel of the drag (each send is ~12 BLE writes)
    private void Light_Commit(object sender, EventArgs e)
    {
        if (loading || ((int)Bright.Value == L.Brightness && (int)Period.Value == L.Period)) return;
        L.Brightness = (int)Bright.Value;
        L.Period = (int)Period.Value;
        ctl.Save(lightsChanged: true);
    }

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        bool zones = Zoned;
        var colors = zones ? L.ZoneColors! : L.Mode == Bs1Cooler.LightMode.Static ? L.Colors.GetRange(0, Math.Min(1, L.Colors.Count)) : L.Colors;
        for (int i = 0; i < colors.Count; i++)
        {
            int idx = i;
            var b = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 0),
                Background = (Brush)new BrushConverter().ConvertFromString(colors[i])!,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                ToolTip = zones ? $"Zone {i + 1}" : "Click: change · Right-click: remove",
            };
            b.MouseLeftButtonUp += (_, _) => PickColor(idx);
            b.MouseRightButtonUp += (_, _) =>
            {
                if (zones || L.Colors.Count <= 1) return;
                L.Colors.RemoveAt(idx);
                Light_Changed(this, new RoutedEventArgs());
            };
            Swatches.Children.Add(b);
        }
    }

    private bool Zoned => L.Mode == Bs1Cooler.LightMode.Static && L.ZoneColors is { Count: 3 };

    private void PerZone_Click(object sender, RoutedEventArgs e)
    {
        L.ZoneColors = PerZone.IsChecked == true ? new() { L.Colors[0], L.Colors[0], L.Colors[0] } : null;
        Light_Changed(this, new RoutedEventArgs());
    }

    private void AddColor_Click(object sender, RoutedEventArgs e) => PickColor(L.Colors.Count);

    private void PickColor(int idx)
    {
        using var dlg = new WinForms.ColorDialog { FullOpen = true };
        var list = Zoned ? L.ZoneColors! : L.Colors;
        if (idx < list.Count) dlg.Color = System.Drawing.ColorTranslator.FromHtml(list[idx]);
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        string hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        var target = Zoned ? L.ZoneColors! : L.Colors;
        if (idx < target.Count) target[idx] = hex; else target.Add(hex);
        Light_Changed(this, new RoutedEventArgs());
    }

    // ---- behaviour ----

    private void Behaviour_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        S.OffOnShutdown = OffOnShutdown.IsChecked == true;
        S.QuietWhenMedia = QuietWhenMedia.IsChecked == true;
        S.AutoConnect = AutoConnect.IsChecked == true;
        S.OnStartup = (StartupMode)Math.Max(0, OnStartup.SelectedIndex);
        ctl.Save();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (ctl.Dev.Connected) ctl.Disconnect(); else ctl.Connect();
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartWithWindows.IsChecked == true;
        // logon task with highest privileges: CPU temperature needs admin, and no UAC prompt at boot
        string ps = want
            ? $"Register-ScheduledTask -TaskName '{TaskName}' -Force -RunLevel Highest " +
              $"-Action (New-ScheduledTaskAction -Execute '{Environment.ProcessPath}' -Argument '--tray') " +
              "-Trigger (New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME) " +
              "-Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0)"
            : $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false";
        RunPowerShell(ps);
        StartWithWindows.IsChecked = TaskExists();
        Log.Write($"start with windows: {StartWithWindows.IsChecked}");
    }

    public static bool TaskExists() => RunPowerShell($"if (Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue) {{ exit 0 }} else {{ exit 1 }}") == 0;

    private static int RunPowerShell(string script)
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"")
        {
            CreateNoWindow = true, UseShellExecute = false,
        })!;
        p.WaitForExit(15000);
        return p.ExitCode;
    }
}
