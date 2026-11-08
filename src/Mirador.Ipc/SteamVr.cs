using System.Security;
using Microsoft.Win32;

namespace Mirador.Ipc;

/// <summary>Finds SteamVR on this PC the way Monado's <c>steamvr_lh</c> driver does, to get at SteamVR's Lighthouse driver.</summary>
public static class SteamVr
{
    /// <summary>SteamVR's app id on Steam.</summary>
    private const string SteamVrAppId = "250820";

    /// <summary>
    /// Finds SteamVR's folder. <c>STEAMVR_PATH</c> (<paramref name="steamVrPathEnv"/>), when set, is the folder as it is.
    /// Otherwise the first library in Steam's <c>libraryfolders.vdf</c> that lists SteamVR and has its folder wins.
    /// </summary>
    /// <param name="steamPath">Steam's folder, as <see cref="SteamPathFromRegistry"/> gives it (forward slashes are fine).</param>
    /// <param name="steamVrPathEnv">The value of the <c>STEAMVR_PATH</c> environment variable, or null.</param>
    /// <param name="readFile">Reads a text file; returns null when the file is not there.</param>
    /// <param name="dirExists">Whether a folder exists.</param>
    /// <returns>A full path with backslashes, or null when SteamVR is not found. Never throws for a file it cannot make sense of.</returns>
    public static string? FindInstall(
        string? steamPath, string? steamVrPathEnv, Func<string, string?> readFile, Func<string, bool> dirExists)
    {
        if (!string.IsNullOrWhiteSpace(steamVrPathEnv))
        {
            return Resolve(steamVrPathEnv);
        }

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return null;
        }

        var vdfPath = Resolve(steamPath, "steamapps", "libraryfolders.vdf");
        if (vdfPath is null || readFile(vdfPath) is not { } text)
        {
            return null;
        }

        var libraries = Vdf.Parse(text)?.Get("libraryfolders");
        if (libraries is null)
        {
            return null;
        }

        foreach (var (_, library) in libraries.Members)
        {
            if (library.Get("apps")?.Get(SteamVrAppId) is null
                || library.Get("path")?.Text is not { } libraryPath
                || string.IsNullOrWhiteSpace(libraryPath))
            {
                continue;
            }

            var candidate = Resolve(libraryPath, "steamapps", "common", "SteamVR");
            if (candidate is not null && dirExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Where SteamVR keeps its Lighthouse driver, under <paramref name="steamVrDir"/>. Whether it is there is for the caller to check.</summary>
    public static string DriverLighthouse(string steamVrDir) =>
        Path.Combine(steamVrDir, "drivers", "lighthouse", "bin", "win64", "driver_lighthouse.dll");

    /// <summary>The folder SteamVR's Lighthouse driver loads its DLLs from, under <paramref name="steamVrDir"/>.</summary>
    public static string DriverLighthouseBinDir(string steamVrDir) =>
        Path.Combine(steamVrDir, "drivers", "lighthouse", "bin", "win64");

    /// <summary>Steam's folder from the registry (<c>HKCU\Software\Valve\Steam</c>, value <c>SteamPath</c>), or null when Steam has not written it.</summary>
    public static string? SteamPathFromRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }

    /// <summary>The parts joined into a full path with backslashes, or null when Windows can't make a path of them.</summary>
    private static string? Resolve(params string[] parts)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(parts));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }
}
