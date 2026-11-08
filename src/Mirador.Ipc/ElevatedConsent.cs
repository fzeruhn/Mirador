using System.Security.Principal;
using Microsoft.Win32;

namespace Mirador.Ipc;

/// <summary>
/// The consent values under <c>HKLM\SOFTWARE\Mirador\ElevatedMonado</c> (64-bit view), behind an interface so tests
/// touch no registry. Each value is named after a user's SID.
/// </summary>
public interface IConsentValues
{
    /// <summary>Whether a value of this name exists and is the DWORD 1.</summary>
    bool Has(string sid);

    /// <summary>Writes the DWORD 1 under this name, creating the key when it isn't there.</summary>
    void Set(string sid);

    /// <summary>Removes the value; one that isn't there is fine.</summary>
    void Delete(string sid);
}

/// <summary>
/// Which users have allowed Monado to run as administrator (Mirador.md section 4.10 item 44). Only an administrator can
/// write the key, so a program running as the user can't give itself consent: <see cref="Allow"/> runs in the elevated
/// <c>--allow-elevated-monado</c> step (or the installer), and the helper service only ever calls <see cref="Revoke"/>
/// and <see cref="IsAllowed"/>.
/// </summary>
public sealed class ElevatedConsent(IConsentValues values)
{
    public bool IsAllowed(SecurityIdentifier sid) => values.Has(sid.Value);

    public void Allow(SecurityIdentifier sid) => values.Set(sid.Value);

    public void Revoke(SecurityIdentifier sid) => values.Delete(sid.Value);
}

/// <summary>The real registry.</summary>
public sealed class RegistryConsentValues : IConsentValues
{
    /// <summary>The key's path under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public const string KeyPath = @"SOFTWARE\Mirador\ElevatedMonado";

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

    public void Delete(string sid)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(sid, throwOnMissingValue: false);
    }
}
