using System.Text.RegularExpressions;

namespace Mirador.Core.Lighthouse;

/// <summary>
/// Follows Monado's log to see which Lighthouse-tracked devices it has found: the headset, and how many others
/// (controllers, trackers). The headset is also reported as a new device, so it is left out of the count of others.
/// </summary>
public sealed partial class LighthouseLogParser
{
    private readonly HashSet<string> added = [];
    private volatile string? hmdSerial;
    private volatile int otherDevices;

    /// <summary>The headset's serial, once the log has named it. Safe to read from any thread.</summary>
    public string? HmdSerial => hmdSerial;

    /// <summary>How many devices besides the headset were found, each counted once. Safe to read from any thread.</summary>
    public int OtherDevices => otherDevices;

    /// <summary>
    /// Takes the next line of Monado's log. Not safe to call from several threads at once.
    /// </summary>
    /// <returns>True when <see cref="HmdSerial"/> or <see cref="OtherDevices"/> is different afterwards.</returns>
    public bool Feed(string line)
    {
        var hmd = hmdSerial;
        if (HmdLine().Match(line) is { Success: true } found)
        {
            hmd = found.Groups["serial"].Value;
        }
        else if (AddedLine().Match(line) is { Success: true } device)
        {
            added.Add(device.Groups["serial"].Value);
        }
        else
        {
            return false;
        }

        var others = added.Count(serial => serial != hmd);
        if (hmd == hmdSerial && others == otherDevices)
        {
            return false;
        }

        hmdSerial = hmd;
        otherDevices = others;
        return true;
    }

    [GeneratedRegex(@"Found lighthouse HMD: (?<serial>\S+)")]
    private static partial Regex HmdLine();

    [GeneratedRegex(@"New device added: (?<serial>\S+)")]
    private static partial Regex AddedLine();
}
