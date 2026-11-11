using Microsoft.Win32;

namespace Mirador.Core.Runtime;

/// <summary>Writes the machine-wide active OpenXR runtime. Needs an administrator: only the elevated <c>Mirador.exe --approve-build</c> step calls it, for the copy it just approved.</summary>
public static class ActiveRuntimeWriter
{
    /// <summary>Sets <c>ActiveRuntime</c> in the 64-bit <c>HKLM\SOFTWARE\Khronos\OpenXR\1</c> to <paramref name="manifest"/>.</summary>
    /// <exception cref="UnauthorizedAccessException">Not elevated.</exception>
    public static void Write(string manifest)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.CreateSubKey(@"SOFTWARE\Khronos\OpenXR\1", writable: true);
        key.SetValue("ActiveRuntime", manifest, RegistryValueKind.String);
    }
}
