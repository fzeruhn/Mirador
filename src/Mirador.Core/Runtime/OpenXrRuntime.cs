using Microsoft.Win32;

namespace Mirador.Core.Runtime;

/// <summary>Reads the machine-wide OpenXR runtime setting. A seam so the rest can be tested without the registry.</summary>
public interface IRuntimeRegistryReader
{
    /// <summary>The manifest path stored as the active runtime, exactly as stored; null when none is set.</summary>
    string? ActiveRuntime();
}

/// <summary>
/// Reads <c>ActiveRuntime</c> from the 64-bit <c>HKLM\SOFTWARE\Khronos\OpenXR\1</c>, the value every OpenXR app's loader
/// uses to pick its runtime. Only reads.
/// </summary>
public sealed class RegistryRuntimeReader : IRuntimeRegistryReader
{
    private const string KeyPath = @"SOFTWARE\Khronos\OpenXR\1";

    public string? ActiveRuntime()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath);
        return key?.GetValue("ActiveRuntime") as string;
    }
}

/// <summary>Which OpenXR runtime is the active one.</summary>
public sealed class OpenXrRuntime(IRuntimeRegistryReader reader)
{
    /// <summary>The manifest path of the active runtime as stored; null when none is set.</summary>
    public string? Active()
    {
        var stored = reader.ActiveRuntime();
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    /// <summary>
    /// Whether <paramref name="manifest"/> is the active runtime. Both are compared as full paths, ignoring case, spaces
    /// and surrounding quotes, and with <c>/</c> and <c>\</c> alike. A value that is not a full path is never active.
    /// </summary>
    public bool IsActive(string manifest)
    {
        var active = FullPathOf(reader.ActiveRuntime());
        var wanted = FullPathOf(manifest);
        return active is not null && wanted is not null && string.Equals(active, wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The value as a normalised full path, or null when it is not a full path (never throws).</summary>
    private static string? FullPathOf(string? value)
    {
        var path = value?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            // GetFullPath turns / into \ and resolves . and .. segments.
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
