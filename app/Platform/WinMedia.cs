using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Bs1Cooler;

/// "Is anything playing?" for quiet mode: any app making sound right now, per Windows' own audio meters
/// (browsers, Spotify, VLC, games, calls: no player list).
public static class WinMedia
{
    private static string? lastSource;

    /// Real sound only: players' "playing" status isn't trusted (Spotify and Telegram report it while silent).
    public static Task<bool> IsPlaying()
    {
        string? source = SoundSource();
        // which app it was, once per change: "quiet: Media" alone doesn't say what made the sound
        if (source != null && source != lastSource) { lastSource = source; Log.Write("sound from " + source); }
        return Task.FromResult(source != null);
    }

    /// Any app (not system sounds, not us) with audio above silence on any active output device.
    public static bool SoundPlaying() => SoundSource() != null;

    private static string? SoundSource() => AnySession(render: true) is uint pid ? "app " + ProcessName(pid) : null;

    private static string ProcessName(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return pid.ToString(); }
    }

    /// Any app recording from any microphone right now (a call, even while nobody talks).
    public static bool MicInUse() => AnySession(render: false) != null;

    private static readonly string[] Virtual = ["CABLE", "VB-Audio", "Voicemeeter", "Virtual", "Stereo Mix", "What U Hear"];

    private static string Name(IMMDevice dev)
    {
        try
        {
            dev.OpenPropertyStore(0 /* read */, out var props);
            var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };  // FriendlyName
            props.GetValue(ref key, out var v);
            return v.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.Ptr) ?? "" : "";
        }
        catch (COMException) { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Fmtid; public int Pid; }
    // PROPVARIANT: only the string case is read; the value is left for the process to reclaim (one per device, rare)
    [StructLayout(LayoutKind.Sequential)] private struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr Ptr; public IntPtr Ptr2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void _GetCount(); void _GetAt();
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    /// Dev (--signals): collect every active capture session's process instead of stopping at the first.
    public static System.Collections.Generic.List<uint>? Trace;

    /// The process of the first matching session, or null.
    private static uint? AnySession(bool render)
    {
        uint self = (uint)Environment.ProcessId;
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        en.EnumAudioEndpoints(render ? 0 : 1, 1 /* active */, out var devices);
        devices.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            devices.Item(i, out var dev);
            string name = Name(dev);
            // virtual cables and mixers (VB-Audio, Voicemeeter...) are held open by audio tools all day: not a mic
            if (!render && Virtual.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase))) continue;
            if (Trace != null && !render) Trace.Add(0);  // marker: this device is checked
            var iid = typeof(IAudioSessionManager2).GUID;
            dev.Activate(ref iid, 1 /* in-process */, IntPtr.Zero, out var o);
            ((IAudioSessionManager2)o).GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);
            for (int k = 0; k < count; k++)
            {
                try
                {
                    sessions.GetSession(k, out var s);
                    if (s.IsSystemSoundsSession() == 0) continue;  // S_OK = the system sounds session
                    s.GetProcessId(out uint pid);
                    if (pid == self) continue;
                    if (!render)
                    {
                        s.GetState(out int state);
                        if (state == 1 /* active */) { if (Trace != null) Trace.Add(pid); else return pid; }
                        continue;
                    }
                    ((IAudioMeterInformation)s).GetPeakValue(out float peak);
                    if (peak > 0.001f) return pid;
                }
                catch (COMException) { }  // a session that closed under us
            }
        }
        return null;
    }

    // Core Audio COM interfaces; unused methods are placeholders that keep the vtable order.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void OpenPropertyStore(int access, out IPropertyStore props);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void _GetAudioSessionControl();
        void _GetSimpleAudioVolume();
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        void GetState(out int state); void _GetDisplayName(); void _SetDisplayName(); void _GetIconPath(); void _SetIconPath();
        void _GetGroupingParam(); void _SetGroupingParam(); void _RegisterNotification(); void _UnregisterNotification();
        void _GetSessionIdentifier(); void _GetSessionInstanceIdentifier();
        void GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
