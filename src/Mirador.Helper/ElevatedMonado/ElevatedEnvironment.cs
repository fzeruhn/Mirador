using System.Globalization;
using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>The caller's own folders, read from the caller's token (never from the request).</summary>
/// <param name="UserProfile">The caller's <c>%USERPROFILE%</c>.</param>
/// <param name="AppData">The caller's <c>%APPDATA%</c>.</param>
/// <param name="LocalAppData">The caller's <c>%LOCALAPPDATA%</c>.</param>
/// <param name="Temp">The caller's temp folder (<c>GetTempPath2</c> under the caller's token); Monado names its pipe from it.</param>
public sealed record UserFolders(string UserProfile, string AppData, string LocalAppData, string Temp);

/// <summary>
/// Builds the environment the elevated <c>monado-service</c> is started with (Mirador.md section 4.10 item 46). The
/// helper builds it from scratch: Windows' machine-wide variables (from the helper's own SYSTEM environment), the
/// caller's own folders and temp, a <c>PATH</c> of System32, Windows and the server folder only, <c>STEAMVR_PATH</c>,
/// <c>IPC_RELAXED_CONNECTION_SECURITY</c>, the range-checked settings and the module's fixed table, then any test
/// lines. Nothing from the request becomes a path, and no <c>VK_*</c> (beyond the module's <c>VK_LOADER_LAYERS_ALLOW</c>)
/// or <c>XR_*</c> variable, nor the profile's own variables, ever reaches it.
/// </summary>
public static class ElevatedEnvironment
{
    /// <summary>The default when Windows hasn't set <c>PATHEXT</c> (a bare SYSTEM environment can lack it).</summary>
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.WSF;.MSC";

    /// <exception cref="ArgumentException">A setting is out of range, or the module name is unknown.</exception>
    public static IReadOnlyDictionary<string, string> Build(
        ElevatedStart start, UserFolders folders, string serverDir, string steamVrPath, IReadOnlyList<string> testLines)
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
        Set(env, "IPC_RELAXED_CONNECTION_SECURITY", "1");

        MonadoSettingsEnvironment.Write(
            (name, value) => Set(env, name, value),
            start.LighthouseWaitMs, start.RenderScalePercent, start.CompositorMinTimeMs, start.ExitOnDisconnect);

        foreach (var (name, value) in moduleFixed)
        {
            Set(env, name, value);
        }

        foreach (var line in testLines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                Set(env, line[..eq], line[(eq + 1)..]);
            }
        }

        return env;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ModuleFixed(string module) => module switch
    {
        "" => [],
        DreamAirEnvironment.ModuleId => DreamAirEnvironment.FixedEnvironment,
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
