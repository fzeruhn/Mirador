namespace Mirador.Core.BaseStations;

/// <summary>Whether this PC can talk to Bluetooth LE devices right now.</summary>
public enum BluetoothState
{
    /// <summary>Bluetooth is on (or has not been looked at yet).</summary>
    Ready,

    /// <summary>The PC has Bluetooth LE but it is switched off.</summary>
    Off,

    /// <summary>The PC has no Bluetooth adapter that can do Bluetooth LE.</summary>
    Missing,
}

/// <summary>One Bluetooth advertisement from a base station.</summary>
/// <param name="Address">The Bluetooth address.</param>
/// <param name="RandomAddress">True if the address is a random address, false if it is a public one.</param>
/// <param name="Name">The Bluetooth name the station advertises.</param>
/// <param name="ValveData">The manufacturer data of Valve's company id, or null if the advertisement has none.</param>
public sealed record Advert(ulong Address, bool RandomAddress, string Name, byte[]? ValveData);

/// <summary>An open Bluetooth connection to one base station. Disposing it closes the connection.</summary>
public interface IStationLink : IAsyncDisposable
{
    /// <summary>Writes the value to the characteristic, waiting for the station to accept it. Throws <see cref="StationException"/> on any failure.</summary>
    Task WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct);
}

/// <summary>The PC's Bluetooth radio, as far as base stations need it.</summary>
public interface IStationRadio
{
    /// <summary>Whether Bluetooth can be used. <see cref="BluetoothState.Ready"/> until <see cref="StartAsync"/> has run.</summary>
    BluetoothState State { get; }

    /// <summary>Raised, on any thread, whenever <see cref="State"/> changes.</summary>
    event Action? StateChanged;

    /// <summary>Looks at the Bluetooth adapter once, at Mirador's start. Never throws for a PC without Bluetooth or with it off.</summary>
    Task StartAsync();

    /// <summary>
    /// Listens for base-station advertisements until the returned object is disposed. <paramref name="heard"/> is called on a
    /// background thread for each advertisement with a base station's name. A listener that stops because Bluetooth went
    /// off starts again when it comes back.
    /// </summary>
    IDisposable Watch(Action<Advert> heard);

    /// <summary>Connects to the station. Throws <see cref="StationException"/> if it can't be reached.</summary>
    Task<IStationLink> ConnectAsync(ulong address, bool randomAddress, CancellationToken ct);
}

/// <summary>A Bluetooth failure talking to a base station (not reachable, no such service, a write refused). The message is plain words.</summary>
public sealed class StationException(string message, Exception? inner = null) : Exception(message, inner);
