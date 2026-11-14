using System.Globalization;
using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>The caller's own folders, read from the caller's token (never from the request).</summary>
/// <param name="UserProfile">The caller's <c>%USERPROFILE%</c>.</param>
/// <param name="AppData">The caller's <c>%APPDATA%</c>.</param>
/// <param name="LocalAppData">The caller's <c>%LOCALAPPDATA%</c>.</param>
/// <param name="Temp">The caller's temp folder, from the caller's environment block; Monado names its pipe from it.</param>
public sealed record UserFolders(string UserProfile, string AppData, string LocalAppData, string Temp)
{
    /// <summary>
    /// The folders from the caller's environment block (<c>CreateEnvironmentBlock</c> with the caller's token), the
    /// block Mirador itself starts Monado from. The temp folder is the one <see cref="MonadoPipeName.TempPathFrom"/>
    /// picks from that block (<c>TMP</c>, <c>TEMP</c>, <c>USERPROFILE</c>), so the elevated Monado's pipe is the name
    /// Mirador and the user's games look for. Never the helper's own temp: a service's environment is SYSTEM's, and
    /// impersonating the caller doesn't change it.
    /// </summary>
    public static UserFolders FromBlock(IReadOnlyDictionary<string, string> block)
    {
        var profile = Get(block, "USERPROFILE") ?? "";
        var temp = Get(block, "TMP") ?? Get(block, "TEMP") ?? profile;
        return new UserFolders(
            profile,
            Get(block, "APPDATA") ?? Path.Combine(profile, "AppData", "Roaming"),
            Get(block, "LOCALAPPDATA") ?? Path.Combine(profile, "AppData", "Local"),
            temp);
    }

    private static string? Get(IReadOnlyDictionary<string, string> block, string name)
    {
        foreach (var (key, value) in block)
        {
            if (value.Length > 0 && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// Builds the environment the elevated <c>monado-service</c> is started with (Mirador.md section 4.10 item 46). The
/// helper builds it from scratch: Windows' machine-wide variables (from the helper's own SYSTEM environment), the
/// caller's own folders and temp, a <c>PATH</c> of System32, Windows and the server folder only, <c>STEAMVR_PATH</c>,
/// <c>IPC_RELAXED_CONNECTION_SECURITY</c> (only when needed: <see cref="ElevatedStart.ElevationFix"/>), the
/// range-checked settings and the module's fixed table. Nothing from the request becomes a path, and no <c>VK_*</c>
/// (beyond the module's <c>VK_LOADER_LAYERS_ALLOW</c>) or <c>XR_*</c> variable, nor the profile's own variables, ever
/// reaches it.
/// </summary>
public static class ElevatedEnvironment
{
    /// <summary>The default when Windows hasn't set <c>PATHEXT</c> (a bare SYSTEM environment can lack it).</summary>
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.WSF;.MSC";

    /// <param name="administratorProtection">Whether Windows' Administrator protection is on (<see cref="AdministratorProtection.IsOn"/>).</param>
    /// <exception cref="ArgumentException">A setting is out of range, or the module name is unknown.</exception>
    public static IReadOnlyDictionary<string, string> Build(
        ElevatedStart start, UserFolders folders, string serverDir, string steamVrPath, bool administratorProtection = false)
    {
        if (MonadoSettingsEnvironment.OutOfRange(start.LighthouseWaitMs, start.RenderScalePercent, start.CompositorMinTimeMs) is { } bad)
        {
            throw new ArgumentException($"The Monado setting {bad} is out of range.", nameof(start));
        }

        var moduleFixed = ModuleFixed(start.Module);
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Windows' own machine-wide variables, from the helper's (SYSTEM) environment, never from the caller.
        var system32 = Environment.SystemDirectory;
        var windows = Path.GetDirectoryName(system32) ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var wbem = Path.Combine(system32, "wbem");
        Set(env, "SystemRoot", windows);
        Set(env, "windir", windows);
        Set(env, "SystemDrive", (Path.GetPathRoot(windows) ?? @"C:\").TrimEnd('\\'));
        Set(env, "ComSpec", Path.Combine(system32, "cmd.exe"));
        Set(env, "PROCESSOR_ARCHITECTURE", Machine("PROCESSOR_ARCHITECTURE") ?? "AMD64");
        Set(env, "NUMBER_OF_PROCESSORS", Machine("NUMBER_OF_PROCESSORS") ?? Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        Set(env, "OS", Machine("OS") ?? "Windows_NT");
        Set(env, "PATHEXT", Machine("PATHEXT") ?? DefaultPathExt);
        Set(env, "ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Set(env, "ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Set(env, "ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Set(env, "CommonProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));

        // The caller's own folders and temp (from the caller's token). Monado names its pipe from TEMP/TMP, so games find it.
        Set(env, "USERPROFILE", folders.UserProfile);
        Set(env, "APPDATA", folders.AppData);
        Set(env, "LOCALAPPDATA", folders.LocalAppData);
        Set(env, "TEMP", folders.Temp);
        Set(env, "TMP", folders.Temp);

        // System32, Windows and the server folder only: never a folder the user can write, where a DLL could be planted.
        Set(env, "PATH", $"{system32};{windows};{wbem};{serverDir}");

        Set(env, "STEAMVR_PATH", steamVrPath);
        // Without the elevation fix, apps running as the user can't open an elevated Monado's pipe unless any signed-in
        // user's may; with it, they can, except under Administrator protection.
        if (!start.ElevationFix || administratorProtection)
        {
            Set(env, "IPC_RELAXED_CONNECTION_SECURITY", "1");
        }

        MonadoSettingsEnvironment.Write(
            (name, value) => Set(env, name, value),
            start.LighthouseWaitMs, start.RenderScalePercent, start.CompositorMinTimeMs, start.ExitOnDisconnect, start.StandbyOnExit);

        foreach (var (name, value) in moduleFixed)
        {
            Set(env, name, value);
        }

        // Verify's run: no headset and no display, fixed values only.
        if (start.Headless)
        {
            if (start.Module.Length > 0)
            {
                throw new ArgumentException("A headless Monado runs without a headset module.", nameof(start));
            }

            Set(env, "XRT_COMPOSITOR_NULL", "1");
            Set(env, "SIMULATED_ENABLE", "1");
        }

        return env;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ModuleFixed(string module) => module switch
    {
        "" => [],
        PimaxEnvironment.ModuleId => DreamAirEnvironment.FixedEnvironment,
        _ => throw new ArgumentException($"Unknown headset module \"{module}\".", nameof(module)),
    };

    private static string? Machine(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Sets a variable, its new spelling replacing any earlier one that differs only in case.</summary>
    private static void Set(Dictionary<string, string> env, string name, string value)
    {
        env.Remove(name);
        env[name] = value;
    }
}
