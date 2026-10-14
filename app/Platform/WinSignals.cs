using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Bs1Cooler;

/// Quiet-mode inputs on Windows, each a cheap poll.
public static class WinSignals
{
    public static Signals Read() => new(WinMedia.MicInUse() || MicInUse(), OnBattery(), DoNotDisturb(), Locked());

    /// Windows' privacy store marks an app that is using the mic right now with LastUsedTimeStop = 0.
    private static bool MicInUse()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        using var key = Registry.CurrentUser.OpenSubKey(root);
        return key != null && AnyActive(key);

        static bool AnyActive(RegistryKey k)
        {
            foreach (var name in k.GetSubKeyNames())
            {
                using var sub = k.OpenSubKey(name);
                if (sub == null) continue;
                if (name == "NonPackaged") { if (AnyActive(sub)) return true; continue; }
                if (sub.GetValue("LastUsedTimeStart") is long start && start > 0 && sub.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                    return true;
            }
            return false;
        }
    }

    private static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    /// 6 = quiet time (Do Not Disturb on), 4 = presentation mode. Registry flags lie on Windows 11: this doesn't.
    private static bool DoNotDisturb() => SHQueryUserNotificationState(out int state) == 0 && state is 4 or 6;

    /// The lock screen is LogonUI.exe; it only runs while locked (or at sign-in).
    private static bool Locked() => Process.GetProcessesByName("LogonUI").Length > 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
