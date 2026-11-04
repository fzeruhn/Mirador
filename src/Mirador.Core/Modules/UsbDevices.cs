using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Mirador.Core.Modules;

/// <summary>A USB device's vendor and product id, as in <c>USB\VID_28DE&amp;PID_2300</c>.</summary>
public readonly record struct UsbId(ushort Vid, ushort Pid);

/// <summary>Lists the USB devices that are plugged in. The seam that lets headset detection be tested without the hardware.</summary>
public interface IUsbDevices
{
    /// <summary>The ids of the USB devices Windows knows to be present now, each once.</summary>
    /// <exception cref="MiradorException">Windows couldn't list them; the message says so.</exception>
    IReadOnlySet<UsbId> Present();
}

/// <summary>The USB devices Windows lists as Plug and Play devices (<c>Win32_PnPEntity</c>), read through WMI each time.</summary>
public sealed partial class WmiUsbDevices : IUsbDevices
{
    /// <summary>How long WMI may take to answer; its default is to wait for ever.</summary>
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlySet<UsbId> Present()
    {
        try
        {
            return Parse(QueryDeviceIds());
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.Timedout)
        {
            throw new MiradorException(
                $"Windows didn't list the USB devices within {WmiTimeout.TotalSeconds:0} seconds, so Mirador can't tell which headset is plugged in. Try again.");
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            throw new MiradorException(
                $"Windows couldn't list the USB devices ({e.Message.Trim().TrimEnd('.')}), so Mirador can't tell which headset is plugged in. Try again.");
        }
    }

    /// <summary>The vendor and product ids in device ids such as <c>USB\VID_28DE&amp;PID_2300\7&amp;…</c>; other ids are skipped.</summary>
    internal static IReadOnlySet<UsbId> Parse(IEnumerable<string?> deviceIds)
    {
        var found = new HashSet<UsbId>();
        foreach (var id in deviceIds)
        {
            if (id is not null && UsbDeviceId().Match(id) is { Success: true } match)
            {
                found.Add(new UsbId(Hex(match.Groups["vid"].Value), Hex(match.Groups["pid"].Value)));
            }
        }

        return found;
    }

    private static ushort Hex(string digits) => ushort.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private static List<string?> QueryDeviceIds()
    {
        // In WQL a backslash in a string is written twice; '_' and '%' are LIKE's wildcards.
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            @"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\VID_%'",
            new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true });
        using var results = searcher.Get();
        var ids = new List<string?>();
        foreach (var item in results)
        {
            using (item)
            {
                ids.Add(item["DeviceID"] as string);
            }
        }

        return ids;
    }

    [GeneratedRegex(@"^USB\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbDeviceId();
}
