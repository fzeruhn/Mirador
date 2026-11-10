using Mirador.Core;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.DirectMode;

/// <summary>The direct-mode Vulkan layer's files Mirador uses.</summary>
/// <param name="ManifestDir">The folder holding <c>mirador_direct_mode.json</c> and its DLL.</param>
/// <param name="CheckExe">The full path of <c>mirador-display-check.exe</c>, which tells whether direct mode works.</param>
public sealed record LayerFiles(string ManifestDir, string CheckExe);

/// <summary>Finds the direct-mode Vulkan layer that comes with Mirador (<c>{app}\layer</c>), which its installer registers.</summary>
public static class DirectModeLayer
{
    public const string ManifestName = "mirador_direct_mode.json";
    public const string DllName = "mirador_direct_mode.dll";
    public const string CheckExeName = "mirador-display-check.exe";

    /// <summary>
    /// The variables that turn the layer on for Monado (<see cref="DirectModeEnvironment"/>), and the ones it takes out
    /// of what Monado inherits (an empty value removes one).
    /// </summary>
    /// <exception cref="MiradorException">The layer isn't there (<see cref="Locate"/>).</exception>
    public static IReadOnlyList<EnvVar> Environment(string bundledDir)
    {
        Locate(bundledDir);
        return
        [
            .. DirectModeEnvironment.Variables.Select(v => new EnvVar(v.Key, v.Value)),
            .. DirectModeEnvironment.Cleared.Select(name => new EnvVar(name, "")),
        ];
    }

    private static readonly string[] LayerFileNames = [ManifestName, DllName, CheckExeName];

    /// <summary>Finds the layer's manifest, DLL and check tool side by side in <paramref name="bundledDir"/>.</summary>
    /// <param name="bundledDir">The layer that comes with Mirador (<c>{app}\layer</c>).</param>
    /// <exception cref="MiradorException">The folder or a file is missing; the message names it and says what to do.</exception>
    public static LayerFiles Locate(string bundledDir)
    {
        if (string.IsNullOrWhiteSpace(bundledDir))
        {
            throw new MiradorException("This Mirador doesn't know where the direct-mode layer that comes with it is. Reinstall Mirador.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundledDir));
        if (!Directory.Exists(root))
        {
            throw new MiradorException($"The direct-mode layer that comes with Mirador is missing ({root}). Reinstall Mirador.");
        }

        var missing = LayerFileNames.Where(name => !File.Exists(Path.Combine(root, name))).ToList();
        if (missing.Count > 0)
        {
            throw new MiradorException(
                $"The direct-mode layer that comes with Mirador is incomplete: {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} missing from {root}. Reinstall Mirador.");
        }

        return new LayerFiles(root, Path.Combine(root, CheckExeName));
    }
}
