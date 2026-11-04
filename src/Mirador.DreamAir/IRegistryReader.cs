using System.Security;
using Microsoft.Win32;

namespace Mirador.DreamAir;

/// <summary>
/// Reads the registry, through its 64-bit view. Keys are written with their hive first, as in
/// <c>HKLM\SYSTEM\CurrentControlSet\Services\PiServiceLauncher</c>. The seam that lets finding Pimax be tested
/// without the registry.
/// </summary>
public interface IRegistryReader
{
    /// <summary>The value <paramref name="name"/> of the key as a string, or null when the key or value is missing or isn't a string.</summary>
    string? GetString(string hiveAndKey, string name);

    /// <summary>The names of the key's subkeys; empty when the key is missing.</summary>
    IReadOnlyList<string> SubKeyNames(string hiveAndKey);
}

/// <summary>The real registry. Only reads; never throws for a key it can't open, which reads as missing.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public string? GetString(string hiveAndKey, string name) =>
        Read(hiveAndKey, key => key.GetValue(name) as string);

    public IReadOnlyList<string> SubKeyNames(string hiveAndKey) =>
        Read<IReadOnlyList<string>>(hiveAndKey, key => key.GetSubKeyNames()) ?? [];

    private static T? Read<T>(string hiveAndKey, Func<RegistryKey, T?> read)
        where T : class
    {
        var (hive, path) = Split(hiveAndKey);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key is null ? null : read(key);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The hive and the path below it; only the two hives Mirador reads are known.</summary>
    private static (RegistryHive Hive, string Path) Split(string hiveAndKey)
    {
        var slash = hiveAndKey.IndexOf('\\');
        var hiveName = slash < 0 ? hiveAndKey : hiveAndKey[..slash];
        var path = slash < 0 ? "" : hiveAndKey[(slash + 1)..];
        var hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => throw new ArgumentException($"Unknown registry hive in {hiveAndKey}.", nameof(hiveAndKey)),
        };
        return (hive, path);
    }
}
