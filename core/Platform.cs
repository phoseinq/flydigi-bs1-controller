using System.Collections.Generic;
using System;
using System.Threading.Tasks;

namespace Bs1Cooler;

/// The cooler's BLE side (WinRT on Windows; BlueZ / CoreBluetooth later). Frames go out as-is.
public interface IBleLink
{
    /// The cooler's address once seen (0 = not yet). Settings remember it.
    ulong Address { get; set; }
    bool Connected { get; }
    /// Fires on every advertisement of our cooler (it only advertises while not connected).
    void StartWatching(Action seen);
    Task<bool> ConnectAsync();
    Task<bool> WriteAsync(byte[] frame);
    void Drop();
    event Action<byte[]>? Frame;       // notifications
    event Action<bool>? LinkChanged;  // the OS reports the link up/down
}

/// CPU/GPU temperatures in °C (null = no sensor).
public interface ITemps
{
    (double? cpu, double? gpu) Read();
    /// Every sensor, for the debug log.
    string Dump();
    /// Each part with a temperature, from the last Read: cpu, gpu, disk, board.
    IReadOnlyList<Part> Parts => [];
    /// Busiest of CPU total and GPU core load in %, from the last Read (null = no load sensors).
    double? Load => null;
    /// Busiest single CPU core in %, from the last Read: one maxed core (a game's main thread) is work that
    /// CPU total hides on a 20-thread chip.
    double? BusiestCore => null;
}

public sealed record Part(string Kind, string Name, double Temp);

/// Mic in use (a call), unplugged, Do Not Disturb or presentation mode, screen locked.
public sealed record Signals(bool MicInUse, bool OnBattery, bool DoNotDisturb, bool Locked);

/// What the host OS provides besides BLE: temperatures and "is media playing" (for quiet mode).
public sealed class Platform
{
    public required IBleLink Ble { get; init; }
    public Func<ITemps>? Temps { get; init; }
    public Func<Task<bool>>? MediaPlaying { get; init; }
    /// The other quiet-mode inputs, polled every tick; null = none on this OS.
    public Func<Signals>? Signals { get; init; }
}
