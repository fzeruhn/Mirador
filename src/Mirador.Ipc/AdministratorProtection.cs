using Microsoft.Win32;

namespace Mirador.Ipc;

/// <summary>
/// Whether Windows' Administrator protection is on: elevation then runs as a separate, hidden administrator account
/// instead of the user's own, so an elevated Monado can't admit the user's apps as its own account's.
/// </summary>
public static class AdministratorProtection
{
    /// <summary>Where Windows keeps the setting (<c>TypeOfAdminApprovalMode</c>, 2 with Administrator protection).</summary>
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>The value that means Administrator protection.</summary>
    public const int On = 2;

    /// <summary>Whether it is on; false when the setting can't be read.</summary>
    public static bool IsOn()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(KeyPath);
            return IsOn(key?.GetValue("TypeOfAdminApprovalMode"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Whether the registry value <paramref name="value"/> means Administrator protection.</summary>
    public static bool IsOn(object? value) => value is int mode && mode == On;
}
