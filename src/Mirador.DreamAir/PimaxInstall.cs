using Mirador.Ipc;

namespace Mirador.DreamAir;

/// <summary>
/// Where Pimax Play is installed: its <c>Runtime</c> folder, which holds Tobii's eye tracking runtime and the eye
/// geometry files Mirador uploads to the tracker. Mirador only reads from it.
/// </summary>
/// <param name="RuntimeDir">Pimax's <c>Runtime</c> folder, such as <c>C:\Program Files\Pimax\Runtime</c>.</param>
/// <param name="Version">Pimax Play's version, such as <c>2.0.4.329</c>; null when its uninstall entry doesn't say.</param>
public sealed record PimaxInstall(string RuntimeDir, string? Version)
{
    private const string ServiceKey = @"HKLM\SYSTEM\CurrentControlSet\Services\" + PimaxNames.Service;
    private const string DisplayName = "PimaxPlay";

    /// <summary>Where Windows lists installed programs: the 64-bit list, then the 32-bit one.</summary>
    private static readonly string[] UninstallKeys =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private string PlatformRuntimeDir => Path.Combine(RuntimeDir, "EyeTrackingServer", "platform_runtime");

    /// <summary>Tobii's eye tracking runtime, the program that uploads the eye geometry to the tracker.</summary>
    public string TobiiRuntimeExe => Path.Combine(PlatformRuntimeDir, PimaxNames.EyeRuntimeProcess + ".exe");

    /// <summary>The eye geometry file for <paramref name="variant"/>, one of <see cref="EyeBlobVariants.All"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="variant"/> isn't a known variant.</exception>
    public string BlobPath(string variant) => Path.Combine(PlatformRuntimeDir, "config", EyeBlobVariants.FileName(variant));

    /// <summary>
    /// Finds Pimax Play: the folder of Pimax's service program (<c>PiServiceLauncher</c>'s <c>ImagePath</c>), else
    /// <c>Runtime</c> under the <c>InstallLocation</c> of the uninstall entry named <c>PimaxPlay</c>. The version comes
    /// from that entry's <c>DisplayVersion</c> either way. Whether the files are there is for the caller to check.
    /// </summary>
    /// <returns>Null when neither the service nor the entry gives a folder. Never throws.</returns>
    public static PimaxInstall? Locate(IRegistryReader reg)
    {
        var entry = FindUninstallEntry(reg);
        var version = entry is null ? null : NullIfBlank(reg.GetString(entry, "DisplayVersion"));

        var runtimeDir = FullFolder(ServiceImage.ProgramOf(reg.GetString(ServiceKey, "ImagePath")) is { } exe ? Path.GetDirectoryName(exe) : null)
            ?? (entry is not null && NullIfBlank(reg.GetString(entry, "InstallLocation")) is { } location
                ? FullFolder(Path.Combine(location.Trim().Trim('"'), "Runtime"))
                : null);

        return runtimeDir is null ? null : new PimaxInstall(runtimeDir, version);
    }

    /// <summary>The key of the uninstall entry whose <c>DisplayName</c> is Pimax Play's, or null.</summary>
    private static string? FindUninstallEntry(IRegistryReader reg)
    {
        foreach (var root in UninstallKeys)
        {
            foreach (var name in reg.SubKeyNames(root))
            {
                var key = $@"{root}\{name}";
                if (string.Equals(reg.GetString(key, "DisplayName")?.Trim(), DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
        }

        return null;
    }

    /// <summary><paramref name="folder"/> as a full path without a trailing separator; null when it isn't a full path.</summary>
    private static string? FullFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
