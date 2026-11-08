using Microsoft.Win32;

namespace Mirador.Core.SignIn;

/// <summary>The current user's "run at sign-in" registry values (64-bit view), behind an interface so tests touch no registry.</summary>
public interface IRunKeys
{
    /// <summary>The command under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, or null.</summary>
    string? GetRun(string name);

    void SetRun(string name, string command);

    void DeleteRun(string name);

    /// <summary>Task Manager's Startup switch for <paramref name="name"/> (<c>…\Explorer\StartupApproved\Run</c>), or null when it never set one.</summary>
    byte[]? GetApproved(string name);

    void DeleteApproved(string name);
}

/// <summary>The real registry.</summary>
public sealed class RegistryRunKeys : IRunKeys
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? GetRun(string name) => Read(RunPath, key => key.GetValue(name) as string);

    public void SetRun(string name, string command) =>
        Write(RunPath, create: true, key => key.SetValue(name, command, RegistryValueKind.String));

    public void DeleteRun(string name) => Write(RunPath, create: false, key => key.DeleteValue(name, throwOnMissingValue: false));

    public byte[]? GetApproved(string name) => Read(ApprovedPath, key => key.GetValue(name) as byte[]);

    public void DeleteApproved(string name) => Write(ApprovedPath, create: false, key => key.DeleteValue(name, throwOnMissingValue: false));

    private static T? Read<T>(string path, Func<RegistryKey, T?> read)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(path);
        return key is null ? default : read(key);
    }

    private static void Write(string path, bool create, Action<RegistryKey> write)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = create ? user.CreateSubKey(path, writable: true) : user.OpenSubKey(path, writable: true);
        if (key is not null)
        {
            write(key);
        }
    }
}
