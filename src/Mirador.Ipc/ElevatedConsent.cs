using System.Security.Principal;
using Microsoft.Win32;

namespace Mirador.Ipc;

/// <summary>
/// The consent values under <c>HKLM\SOFTWARE\Mirador\ElevatedMonado</c> and the approved SteamVR files under
/// <c>HKLM\SOFTWARE\Mirador\ElevatedMonadoFiles</c> (64-bit view), behind an interface so tests touch no registry. Each
/// value is named after a user's SID.
/// </summary>
public interface IConsentValues
{
    /// <summary>Whether a value of this name exists and is the DWORD 1.</summary>
    bool Has(string sid);

    /// <summary>Writes the DWORD 1 under this name, creating the key when it isn't there.</summary>
    void Set(string sid);

    /// <summary>Removes the value; one that isn't there is fine.</summary>
    void Delete(string sid);

    /// <summary>The approved-files lines (a REG_MULTI_SZ) of this name, or null when there is none.</summary>
    string[]? Files(string sid);

    /// <summary>Writes the approved-files lines under this name as a REG_MULTI_SZ, creating the key when it isn't there.</summary>
    void SetFiles(string sid, string[] lines);

    /// <summary>Removes the approved-files value; one that isn't there is fine.</summary>
    void DeleteFiles(string sid);
}

/// <summary>
/// Which users have allowed Monado to run as administrator (Mirador.md section 4.10 item 44), and for each the SteamVR
/// Lighthouse DLLs not signed by Valve that they allowed with it (item 46: name and SHA-256). Only an administrator can
/// write the keys, so a program running as the user can't give itself consent or approve a DLL: <see cref="Allow"/>
/// runs in the elevated <c>--allow-elevated-monado</c> step (or the installer), and the helper service only ever calls
/// <see cref="Revoke"/>, <see cref="IsAllowed"/> and <see cref="ApprovedFiles"/>.
/// </summary>
public sealed class ElevatedConsent(IConsentValues values)
{
    public bool IsAllowed(SecurityIdentifier sid) => values.Has(sid.Value);

    /// <summary>
    /// Records <paramref name="sid"/>'s consent and replaces its approved files with <paramref name="unsignedFiles"/>
    /// (file name to lower-case hex SHA-256; empty when SteamVR isn't installed or every DLL is Valve's). The list is
    /// written first, so a failure leaves the consent as it was.
    /// </summary>
    /// <exception cref="ArgumentException">A name isn't a plain file name, or a value isn't a lower-case hex SHA-256.</exception>
    public void Allow(SecurityIdentifier sid, IReadOnlyDictionary<string, string> unsignedFiles)
    {
        var lines = new List<string>(unsignedFiles.Count);
        foreach (var (name, sha256) in unsignedFiles.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsFileName(name))
            {
                throw new ArgumentException($"\"{name}\" isn't a file name.", nameof(unsignedFiles));
            }

            if (!IsSha256(sha256))
            {
                throw new ArgumentException($"The hash of {name} isn't a lower-case hex SHA-256.", nameof(unsignedFiles));
            }

            lines.Add(name + "=" + sha256);
        }

        if (lines.Count == 0)
        {
            values.DeleteFiles(sid.Value);
        }
        else
        {
            values.SetFiles(sid.Value, [.. lines]);
        }

        values.Set(sid.Value);
    }

    /// <summary>
    /// The DLLs <paramref name="sid"/> allowed: file name (compared as Windows does, ignoring case) to lower-case hex
    /// SHA-256. Empty when there are none; a line that isn't a plain file name and a SHA-256 approves nothing.
    /// </summary>
    public IReadOnlyDictionary<string, string> ApprovedFiles(SecurityIdentifier sid)
    {
        var approved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in values.Files(sid.Value) ?? [])
        {
            // The hash has no '=', a file name may: split at the last one.
            var eq = line.LastIndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = line[..eq];
            var sha256 = line[(eq + 1)..];
            if (IsFileName(name) && IsSha256(sha256))
            {
                approved.TryAdd(name, sha256);
            }
        }

        return approved;
    }

    /// <summary>Takes back <paramref name="sid"/>'s consent, then its approved files.</summary>
    public void Revoke(SecurityIdentifier sid)
    {
        values.Delete(sid.Value);
        values.DeleteFiles(sid.Value);
    }

    /// <summary>A name in a folder, no path: what <see cref="Path.GetFileName(string)"/> leaves alone.</summary>
    private static bool IsFileName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name is not "." and not "..";

    /// <summary>64 lower-case hex digits.</summary>
    private static bool IsSha256(string text) =>
        text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>The real registry.</summary>
public sealed class RegistryConsentValues : IConsentValues
{
    /// <summary>The consent key's path under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public const string KeyPath = @"SOFTWARE\Mirador\ElevatedMonado";

    /// <summary>The approved SteamVR files' key under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public const string FilesKeyPath = @"SOFTWARE\Mirador\ElevatedMonadoFiles";

    public bool Has(string sid)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath);
        return key?.GetValue(sid) is int value && value == 1;
    }

    public void Set(string sid)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(KeyPath, writable: true);
        key.SetValue(sid, 1, RegistryValueKind.DWord);
    }

    public void Delete(string sid) => DeleteValue(KeyPath, sid);

    public string[]? Files(string sid)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(FilesKeyPath);
        if (key is null || Array.IndexOf(key.GetValueNames(), sid) < 0 || key.GetValueKind(sid) != RegistryValueKind.MultiString)
        {
            return null;
        }

        return key.GetValue(sid) as string[];
    }

    public void SetFiles(string sid, string[] lines)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(FilesKeyPath, writable: true);
        key.SetValue(sid, lines, RegistryValueKind.MultiString);
    }

    public void DeleteFiles(string sid) => DeleteValue(FilesKeyPath, sid);

    private static void DeleteValue(string keyPath, string sid)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(sid, throwOnMissingValue: false);
    }
}
