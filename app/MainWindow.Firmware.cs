using System;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentIcons.Common;

namespace Bs1Cooler.App;

/// First-run setup and the firmware update wizard.
public partial class MainWindow
{
    private Control? beforeUpdate;   // the page to go back to
    private CancellationTokenSource? flashCts;
    private bool flashing;
    private static readonly int CustomVersion = Flasher.CustomVersion();

    /// A tray flyout hides when it loses focus, but never in the middle of setup or an update.
    private bool Busy => SetupPage.IsVisible || UpdatePage.IsVisible;

    private void ShowPage(Control page)
    {
        foreach (var p in new Control[] { Home, SettingsPage, TempsPage, SetupPage, UpdatePage }) p.IsVisible = p == page;
    }

    private void WireFirmware()
    {
        SetupSkip.Click += (_, _) => FinishSetup();
        SetupNext.Click += (_, _) => { if (NeedsUpdate) OpenUpdate(); else FinishSetup(); };
        UpdateBarBtn.Click += (_, _) => OpenUpdate();
        UpdateBtn.Click += (_, _) => OpenUpdate();
        UpdateBack.Click += (_, _) => CloseUpdate();
        UpdateCancel.Click += (_, _) => CloseUpdate();
        UpdateGo.Click += (_, _) => UpdateNext();
        Consent.IsCheckedChanged += (_, _) => UpdateGo.IsEnabled = Consent.IsChecked == true;
        DriverBtn.Click += async (_, _) =>
        {
            DriverBtn.IsEnabled = false;
            bool ok = await Task.Run(Flasher.InstallDriver);
            DriverBar.Title = Tx.Tr(ok ? "Driver installed" : "Driver install failed");
            DriverBar.Message = Tx.Tr(ok ? "Unplug the cooler and do the steps again." : "See the log, or install WCH's CH372 driver by hand.");
            DriverBar.Severity = ok ? FluentAvalonia.UI.Controls.FAInfoBarSeverity.Success : FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error;
            DriverBtn.IsEnabled = true;
        };
        if (!S.SetupDone) ShowPage(SetupPage);
    }

    /// Official firmware older than 0.0.6.02, or our firmware older than the one in this app.
    /// Official edition: only an old official firmware, or ours to take back. Custom/Beta: anything that isn't ours or is older.
    private bool NeedsUpdate => OperatingSystem.IsWindows() && ctl.Dev.Connected && (S.Edition == Edition.Official
        ? ctl.FirmwareOld || ctl.FirmwarePatch > 0
        : ctl.FirmwarePatch < CustomVersion);  // ours is the default: official firmware gets it offered too

    private void RefreshFirmware()
    {
        UpdateBar.IsOpen = NeedsUpdate && !UpdatePage.IsVisible;
        (UpdateBar.Title, UpdateBar.Message) =
            S.Edition == Edition.Official && ctl.FirmwarePatch > 0 ? (Tx.Tr("Firmware"), Tx.Tr("You chose the official firmware; the cooler runs the BS1 Cooler firmware."))
            : ctl.FirmwarePatch > 0 ? (Tx.Tr("Firmware update"), Tx.Tr("Version {0} of the BS1 Cooler firmware is ready.", CustomVersion))
            : !ctl.FirmwareOld ? (Tx.Tr("Firmware"), Tx.Tr("The BS1 Cooler firmware is recommended: smooth colors and a power-on animation. You can go back to the official one in Settings."))
            : (Tx.Tr("Firmware update"), Tx.Tr("Your cooler's firmware is old: it can't start by itself after a power cut."));
        UpdateBtn.IsEnabled = OperatingSystem.IsWindows() && !flashing;  // flashing needs WCH's Windows driver
        FirmwareDesc.Text = !ctl.Dev.Connected ? Tx.Tr("Cooler not connected")
            : ctl.FirmwarePatch > 0 ? $"BS1 Cooler {ctl.FirmwarePatch} · Flydigi {ctl.FirmwareVersion}"
            : Tx.Tr("Official Flydigi {0}", ctl.FirmwareVersion);
        bool known = ctl.Dev.Connected && ctl.FirmwareVersion != null;
        FirmwareState.IsVisible = known;
        FirmwareState.Text = Tx.Tr(NeedsUpdate ? "Update available" : "Up to date");
        FirmwareState.Foreground = (Avalonia.Media.IBrush?)this.FindResource(NeedsUpdate ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");

        if (!SetupPage.IsVisible) return;
        bool found = ctl.Dev.Connected && ctl.FirmwareVersion != null;
        SetupRing.IsVisible = !found;
        SetupOk.IsVisible = found;
        SetupNext.IsEnabled = found;
        if (!found) return;
        SetupStatus.Text = Tx.Tr("Found your cooler ({0})", ctl.Dev.OverUsb ? "USB" : "Bluetooth");
        SetupHint.Text = NeedsUpdate
            ? Tx.Tr("Its firmware ({0}) is out of date. Next: update it, about a minute.", FirmwareName())
            : Tx.Tr("Firmware {0}: up to date.", FirmwareName());
        SetupNext.Content = Tx.Tr(NeedsUpdate ? "Update firmware" : "Finish");
    }

    /// Setup done (or skipped): start with Windows is on by default.
    private async void CompleteSetup()
    {
        S.SetupDone = true;
        ctl.Save();
        try { if (!await Task.Run(TaskExists)) await SetStartWithWindows(true); }
        catch (Exception e) { Log.Write("start at login: " + e.Message); }  // async void: never let it reach the app
    }

    private string FirmwareName() => ctl.FirmwarePatch > 0 ? $"BS1 Cooler {ctl.FirmwarePatch}" : $"Flydigi {ctl.FirmwareVersion}";

    private void FinishSetup()
    {
        CompleteSetup();
        ctl.Save();
        ShowPage(Home);
    }

    private void OpenUpdate()
    {
        beforeUpdate = SetupPage.IsVisible ? Home : SettingsPage.IsVisible ? SettingsPage : Home;
        if (SetupPage.IsVisible) CompleteSetup();
        CurrentFw.Text = ctl.Dev.Connected ? Tx.Tr("Now on the cooler: {0}.", FirmwareName()) : Tx.Tr("The cooler isn't connected; you can still update it.");
        Consent.IsChecked = false;
        PickOfficial.IsChecked = S.Edition == Edition.Official;
        PickCustom.IsChecked = S.Edition != Edition.Official;
        UpdateGo.IsEnabled = false;
        UpdateGo.Content = Tx.Tr("Next");
        ShowStep(StepChoose);
        ShowPage(UpdatePage);
    }

    private void CloseUpdate()
    {
        flashCts?.Cancel();
        if (flashing) return;  // can't stop a write half way; it finishes in seconds
        ShowPage(beforeUpdate ?? Home);
        RefreshFirmware();
    }

    private void ShowStep(Control step)
    {
        foreach (var s in new Control[] { StepChoose, StepPlug, StepFlash, StepDone }) s.IsVisible = s == step;
        UpdateGo.IsVisible = step == StepChoose || step == StepDone;
        UpdateCancel.IsVisible = step != StepDone;
        UpdateBack.IsEnabled = step != StepFlash;
    }

    private async void UpdateNext()
    {
        if (StepDone.IsVisible) { CloseUpdate(); return; }
        var edition = PickOfficial.IsChecked == true ? Flasher.Edition.Official : Flasher.Edition.Custom;
        DriverBar.IsOpen = HelpBar.IsOpen = false;
        ShowStep(StepPlug);
        flashCts = new CancellationTokenSource();
        var guide = Guide(flashCts.Token);
        try
        {
            await Flasher.Run(edition, (what, p) => Dispatcher.UIThread.Post(() =>
            {
                if (!flashing) { flashing = true; flashCts.Cancel(); ShowStep(StepFlash); }
                FlashStep.Text = Tx.Tr(what switch { "erase" => "Erasing…", "write" => "Writing…", "verify" => "Checking…", "restart" => "Restarting the cooler…", _ => "Connecting…" });
                FlashBar.Value = p;
            }), () => Dispatcher.UIThread.Post(() => DriverBar.IsOpen = true), flashCts.Token);
            Done(true, Tx.Tr(edition == Flasher.Edition.Custom
                ? "The cooler restarted with the BS1 Cooler firmware. It reconnects by itself in a few seconds."
                : "The cooler restarted with the official Flydigi firmware. It reconnects by itself in a few seconds."));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Write("flash failed: " + e);
            Done(false, $"{e.Message}\n{Tx.Tr("Unplug the cooler and try again: update mode always works, nothing is lost.")}");
        }
        finally { flashing = false; }
    }

    /// Ticks the steps off by itself: step 1 is done when the cooler's normal USB device disappears;
    /// 25 s without the bootloader shows the troubleshooting tips.
    private async Task Guide(CancellationToken ct)
    {
        static bool Plugged() => HidSharp.DeviceList.Local.GetHidDevices(0x04A0, 0x2501).Any();
        int phase = Plugged() ? 1 : 2;
        var since = DateTime.Now;
        SetPhase(phase);
        try
        {
            while (true)
            {
                await Task.Delay(250, ct);
                if (phase == 1 && !Plugged()) { phase = 2; since = DateTime.Now; SetPhase(2); }
                if (phase == 2 && DateTime.Now - since > TimeSpan.FromSeconds(25)) HelpBar.IsOpen = true;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetPhase(int phase)
    {
        void Dot(Border d, string state, string text)
        {
            d.Classes.Set("active", state == "active");
            d.Classes.Set("done", state == "done");
            ((TextBlock)d.Child!).Text = text;
        }
        Dot(Dot1, phase == 1 ? "active" : "done", phase == 1 ? "1" : "✓");
        Dot(Dot2, phase == 1 ? "" : "active", "2");
        Dot(Dot3, phase == 1 ? "" : "active", "3");
        Art.Classes.Set("unplug", phase == 1);
        Art.Classes.Set("plug", phase == 2);
    }

    /// Dev (--smoke): visit every page and tutorial state so a bad style or binding throws now, not for a user.
    public void SmokeTest(string? show = null, string? lang = null)
    {
        // every XAML string must have a translation: list the ones that don't
        SetLanguage("fa");
        foreach (var o in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(this))
        {
            string? s = o switch { TextBlock t => t.Text, Avalonia.Controls.Primitives.HeaderedContentControl h => h.Header as string, ContentControl c => c.Content as string, _ => null };
            if (s != null && System.Text.RegularExpressions.Regex.IsMatch(s, "[a-z]{3}") && Tx.Retranslate(s) == s && !Tx.Known(s) && !s.StartsWith("BS1"))
                Console.WriteLine("untranslated: " + s);
        }
        SetLanguage(lang ?? "en");
        foreach (var page in new Control[] { SettingsPage, TempsPage, SetupPage, UpdatePage, Home }) ShowPage(page);
        SampleTemps();
        ShowPage(UpdatePage);
        foreach (var step in new Control[] { StepChoose, StepPlug, StepFlash, StepDone }) ShowStep(step);
        SetPhase(1);
        SetPhase(2);
        ShowPage(show switch { "settings" or "quiet" => SettingsPage, "temps" => TempsPage, "setup" => SetupPage, "update" => UpdatePage, _ => Home });
        if (show == "update") { ShowStep(StepPlug); SetPhase(2); }
        QuietBadge.IsVisible = QuietPill.IsVisible = true;  // preview quiet mode
        ColorRow.IsVisible = true;  // preview the color row (no settings change: smoke must never save)
        if (show == "quiet") Avalonia.Threading.Dispatcher.UIThread.Post(() => QuietOnCalls.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Snapshot(string path)
    {
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#202020"));  // Mica isn't part of the render
        Measure(new Avalonia.Size(Width, Height)); Arrange(new Avalonia.Rect(0, 0, Width, Height));  // also when never shown (--hidden)
        var size = new Avalonia.PixelSize((int)Width, (int)Height);
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        bmp.Render(this);
        bmp.Save(path);
    }

    private void Done(bool ok, string text)
    {
        DoneIcon.Symbol = ok ? Symbol.ShieldCheckmark : Symbol.Warning;
        DoneIcon.Foreground = (Avalonia.Media.IBrush?)this.FindResource(ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        DoneTitle.Text = Tx.Tr(ok ? "Updated" : "That didn't work");
        DoneText.Text = text;
        UpdateGo.Content = Tx.Tr(ok ? "Done" : "Close");
        ShowStep(StepDone);
    }
}
