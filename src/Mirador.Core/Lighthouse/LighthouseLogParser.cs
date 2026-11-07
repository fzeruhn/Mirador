using System.Text.RegularExpressions;

namespace Mirador.Core.Lighthouse;

/// <summary>What a Lighthouse-tracked device that isn't the headset is.</summary>
public enum TrackedKind
{
    /// <summary>A hand controller.</summary>
    Controller,

    /// <summary>A body or object tracker.</summary>
    Tracker,
}

/// <summary>One controller or tracker Monado found.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Serial">Its serial, as Monado's log names it.</param>
public sealed record TrackedDevice(TrackedKind Kind, string Serial);

/// <summary>
/// Follows Monado's log to see which Lighthouse-tracked devices it has found: the headset, and the controllers and
/// trackers.
/// </summary>
public sealed partial class LighthouseLogParser
{
    private readonly HashSet<string> seen = [];
    private readonly List<TrackedDevice> found = [];
    private volatile string? hmdSerial;
    private volatile IReadOnlyList<TrackedDevice> devices = [];

    /// <summary>The headset's serial, once the log has named it. Safe to read from any thread.</summary>
    public string? HmdSerial => hmdSerial;

    /// <summary>The controllers and trackers found, in the order found, each serial once. A snapshot; safe to read from any thread.</summary>
    public IReadOnlyList<TrackedDevice> Devices => devices;

    /// <summary>
    /// Takes the next line of Monado's log. Not safe to call from several threads at once.
    /// </summary>
    /// <returns>True when <see cref="HmdSerial"/> or <see cref="Devices"/> is different afterwards.</returns>
    public bool Feed(string line)
    {
        if (HmdLine().Match(line) is { Success: true } hmd)
        {
            var serial = hmd.Groups["serial"].Value;
            if (serial == hmdSerial)
            {
                return false;
            }

            hmdSerial = serial;
            return true;
        }

        if (DeviceLine().Match(line) is { Success: true } device)
        {
            var serial = device.Groups["serial"].Value;
            if (!seen.Add(serial))
            {
                return false;
            }

            var kind = device.Groups["kind"].Value == "controller" ? TrackedKind.Controller : TrackedKind.Tracker;
            found.Add(new TrackedDevice(kind, serial));
            devices = found.ToArray();
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"Found lighthouse HMD: (?<serial>\S+)")]
    private static partial Regex HmdLine();

    [GeneratedRegex(@"Found lighthouse (?<kind>controller|tracker): (?<serial>\S+)")]
    private static partial Regex DeviceLine();
}
