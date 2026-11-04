using System.Management;
using System.Runtime.InteropServices;
using Mirador.Core;

namespace Mirador.DreamAir;

/// <summary>
/// The NVIDIA driver's version. Direct mode on the Dream Air goes through NVIDIA's private interface, whose ids can
/// move with any driver, so Mirador checks direct mode again when the driver changes.
/// </summary>
public static class NvidiaDriver
{
    /// <summary>How long WMI may take to answer; its default is to wait for ever.</summary>
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// NVIDIA's version from the one Windows shows: the last five digits of the last two parts, so
    /// <c>32.0.16.2101</c> is <c>621.01</c>. The last part counts as four digits even when Windows dropped its
    /// leading zeros. Null when <paramref name="windowsVersion"/> isn't four numbers.
    /// </summary>
    public static string? FromWindowsVersion(string windowsVersion)
    {
        var parts = windowsVersion.Trim().Split('.');
        if (parts.Length != 4 || !parts.All(IsDigits) || parts[3].Length > 4)
        {
            return null;
        }

        var digits = parts[2] + parts[3].PadLeft(4, '0');
        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    /// <summary>
    /// The Windows driver version of the first video controller whose name contains NVIDIA
    /// (<c>Win32_VideoController</c>, through WMI), such as <c>32.0.16.2101</c>; null when there is none.
    /// </summary>
    /// <exception cref="MiradorException">Windows couldn't list the graphics cards; the message says so.</exception>
    public static string? CurrentWindowsVersion()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT Name, DriverVersion FROM Win32_VideoController",
                new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true });
            using var results = searcher.Get();
            string? found = null;
            foreach (var item in results)
            {
                using (item)
                {
                    if (found is null
                        && item["Name"] is string name
                        && name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        && item["DriverVersion"] is string version
                        && !string.IsNullOrWhiteSpace(version))
                    {
                        found = version.Trim();
                    }
                }
            }

            return found;
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.Timedout)
        {
            throw new MiradorException(
                $"Windows didn't list the graphics cards within {WmiTimeout.TotalSeconds:0} seconds, so Mirador can't tell the NVIDIA driver's version. Try again.");
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            throw new MiradorException(
                $"Windows couldn't list the graphics cards ({e.Message.Trim().TrimEnd('.')}), so Mirador can't tell the NVIDIA driver's version. Try again.");
        }
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
