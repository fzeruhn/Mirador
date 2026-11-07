using System.Globalization;

namespace Mirador.Core.BaseStations;

/// <summary>The two generations of SteamVR base station, which speak different Bluetooth protocols.</summary>
public enum StationVersion
{
    /// <summary>The first generation (HTC Vive base stations, "HTC BS ...").</summary>
    V1,

    /// <summary>The second generation (SteamVR Base Station 2.0, "LHB-...").</summary>
    V2,
}

/// <summary>What a base station can be told to do.</summary>
public enum StationCommand
{
    /// <summary>Spin up and track.</summary>
    On,

    /// <summary>Stop tracking but stay awake, ready to start again quickly.</summary>
    Standby,

    /// <summary>Stop tracking and sleep.</summary>
    Sleep,
}

/// <summary>What a base station's Bluetooth advertisement says it is doing.</summary>
public enum StationPower
{
    /// <summary>The advertisement doesn't say, or says something unknown.</summary>
    Unknown,

    /// <summary>Asleep.</summary>
    Asleep,

    /// <summary>Starting up or shutting down.</summary>
    Waking,

    /// <summary>On and tracking.</summary>
    On,
}

/// <summary>
/// The bytes and identifiers for talking to SteamVR base stations over Bluetooth LE. No Bluetooth calls happen here.
/// </summary>
public static class StationProtocol
{
    /// <summary>The service of a second-generation station.</summary>
    public static readonly Guid V2Service = Guid.Parse("00001523-1212-efde-1523-785feabcd124");

    /// <summary>The second-generation characteristic that switches the station on, to standby or to sleep.</summary>
    public static readonly Guid V2PowerCharacteristic = Guid.Parse("00001525-1212-efde-1523-785feabcd124");

    /// <summary>The second-generation characteristic that makes the station blink its light.</summary>
    public static readonly Guid V2IdentifyCharacteristic = Guid.Parse("00008421-1212-efde-1523-785feabcd124");

    /// <summary>The service of a first-generation station.</summary>
    public static readonly Guid V1Service = Guid.Parse("0000cb00-0000-1000-8000-00805f9b34fb");

    /// <summary>The first-generation characteristic that takes the power commands.</summary>
    public static readonly Guid V1PowerCharacteristic = Guid.Parse("0000cb01-0000-1000-8000-00805f9b34fb");

    /// <summary>The Bluetooth company id of Valve, in whose manufacturer data a second-generation station advertises its state.</summary>
    public const ushort ValveCompany = 0x055D;

    private const int V1Length = 20;

    /// <summary>The value written to <see cref="V2IdentifyCharacteristic"/> to make the station blink. A new array on every call.</summary>
    public static byte[] V2IdentifyValue => [0x01];

    /// <summary>The generation of the station with this Bluetooth name, or null if the name isn't a base station's.</summary>
    public static StationVersion? VersionOf(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (name.StartsWith("LHB-", StringComparison.Ordinal))
        {
            return StationVersion.V2;
        }

        return name.StartsWith("HTC BS", StringComparison.Ordinal) ? StationVersion.V1 : null;
    }

    /// <summary>The values to write, in order, to <see cref="V2PowerCharacteristic"/> for the command. New arrays on every call.</summary>
    public static IReadOnlyList<byte[]> V2Writes(StationCommand command) =>
        command switch
        {
            StationCommand.On => [[0x01]],
            StationCommand.Standby => [[0x02]],
            StationCommand.Sleep => [[0x00]],
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    /// <summary>
    /// The state byte a waking second-generation station shows first (about 3 s after On, then 02 1.5 s later); one
    /// that stays at it has stalled.
    /// </summary>
    public const byte StartingState = 0x01;

    /// <summary>
    /// The power state in a second-generation station's Valve manufacturer data (without the company id): byte 4
    /// is 0 asleep, 1 or 2 waking, 3 on. Anything else, or fewer than 5 bytes, is <see cref="StationPower.Unknown"/>.
    /// </summary>
    public static StationPower AdvertState(ReadOnlySpan<byte> valveData) =>
        AdvertStateByte(valveData) switch
        {
            0x00 => StationPower.Asleep,
            StartingState or 0x02 => StationPower.Waking,
            0x03 => StationPower.On,
            _ => StationPower.Unknown,
        };

    /// <summary>The state byte itself (byte 4 of the Valve manufacturer data, see <see cref="AdvertState"/>); null with fewer than 5 bytes.</summary>
    public static byte? AdvertStateByte(ReadOnlySpan<byte> valveData) => valveData.Length < 5 ? null : valveData[4];

    /// <summary>
    /// The 20 bytes to write to <see cref="V1PowerCharacteristic"/>. On ignores <paramref name="id"/>. Standby and
    /// Sleep are the same for a first-generation station: with its ID the station sleeps after one second, without
    /// it after a minute.
    /// </summary>
    public static byte[] V1Write(StationCommand command, uint? id)
    {
        var bytes = new byte[V1Length];
        switch (command)
        {
            case StationCommand.On:
                bytes[0] = 0x12;
                bytes.AsSpan(4, 4).Fill(0xFF);
                break;
            case StationCommand.Standby:
            case StationCommand.Sleep:
                bytes[0] = 0x12;
                if (id is { } value)
                {
                    bytes[1] = 0x02;
                    bytes[3] = 0x01;
                    BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), value);
                    if (!BitConverter.IsLittleEndian)
                    {
                        bytes.AsSpan(4, 4).Reverse();
                    }
                }
                else
                {
                    bytes[1] = 0x01;
                    bytes[3] = 0x3C;
                    bytes.AsSpan(4, 4).Fill(0xFF);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        return bytes;
    }

    /// <summary>Reads a first-generation station's ID: exactly 8 hex digits in either case, spaces around them allowed.</summary>
    public static bool TryParseV1Id(string? text, out uint id)
    {
        id = 0;
        var trimmed = text?.Trim();
        return trimmed is { Length: 8 }
            && trimmed.All(Uri.IsHexDigit)
            && uint.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>The Bluetooth address as six upper-case hex pairs separated by colons, e.g. <c>CE:36:CF:38:E9:CB</c>.</summary>
    public static string FormatAddress(ulong address)
    {
        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            parts[i] = ((byte)(address >> (8 * (5 - i)))).ToString("X2", CultureInfo.InvariantCulture);
        }

        return string.Join(':', parts);
    }

    /// <summary>Reads an address written like <see cref="FormatAddress"/> does, in either case.</summary>
    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        var parts = text?.Split(':');
        if (parts is not { Length: 6 })
        {
            return false;
        }

        ulong result = 0;
        foreach (var part in parts)
        {
            if (part.Length != 2 || !part.All(Uri.IsHexDigit))
            {
                return false;
            }

            result = (result << 8) | byte.Parse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        address = result;
        return true;
    }
}
