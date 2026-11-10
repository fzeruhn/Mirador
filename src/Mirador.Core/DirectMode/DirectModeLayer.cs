using Mirador.Core;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.DirectMode;

/// <summary>The direct-mode Vulkan layer's files Mirador uses.</summary>
/// <param name="ManifestDir">The folder holding <c>dreamair_vk_layer.json</c> and its DLL, for <c>VK_ADD_IMPLICIT_LAYER_PATH</c>.</param>
/// <param name="CheckExe">The full path of <c>dreamair-display-check.exe</c>, which tells whether direct mode works.</param>
public sealed record LayerFiles(string ManifestDir, string CheckExe);

/// <summary>Finds the direct-mode Vulkan layer: the one that comes with Mirador, or one a profile names.</summary>
public static class DirectModeLayer
{
    /// <summary>The key in <c>Profile.ModuleOptions</c> naming a layer folder to use instead of Mirador's own.</summary>
    public const string OptionKey = "layerFolder";

    public const string ManifestName = "dreamair_vk_layer.json";
    public const string DllName = "dreamair_vk_layer.dll";
    public const string CheckExeName = "dreamair-display-check.exe";

    /// <summary>The profile's layer folder (its <see cref="OptionKey"/> option), or null for the one that comes with Mirador.</summary>
    public static string? FolderOf(Profile profile) =>
        profile.ModuleOptions.GetValueOrDefault(OptionKey) is { } folder && !string.IsNullOrWhiteSpace(folder) ? folder : null;

    /// <summary>
    /// The variables that turn the layer on for Monado (<see cref="DirectModeEnvironment"/>), the ones it takes out of
    /// what Monado inherits (an empty value removes one), and, for a profile with a layer folder,
    /// <c>VK_ADD_IMPLICIT_LAYER_PATH</c> to it.
    /// </summary>
    /// <exception cref="MiradorException">The layer isn't there (<see cref="Locate"/>).</exception>
    public static IReadOnlyList<EnvVar> Environment(Profile profile, string bundledDir)
    {
        var folder = FolderOf(profile);
        var layer = Locate(folder, bundledDir);
        IEnumerable<EnvVar> variables =
        [
            .. DirectModeEnvironment.Variables.Select(v => new EnvVar(v.Key, v.Value)),
            .. DirectModeEnvironment.Cleared.Select(name => new EnvVar(name, "")),
        ];
        return folder is null ? [.. variables] : [.. variables, new EnvVar("VK_ADD_IMPLICIT_LAYER_PATH", layer.ManifestDir)];
    }

    private static readonly string[] LayerFileNames = [ManifestName, DllName];

    /// <summary>
    /// Finds the layer in <paramref name="folder"/>, or in <paramref name="bundledDir"/> when no folder is given, the way
    /// the layer's <c>install.ps1 -Source</c> does: the manifest and DLL side by side in the folder or in its
    /// <c>layer</c> folder, the check tool in the folder or in its <c>check</c> folder. A release folder has everything
    /// side by side; a build folder (<c>dreamair-vk-layer\build</c>) has the two folders.
    /// </summary>
    /// <param name="folder">The profile's layer folder (<see cref="OptionKey"/>); null or blank for Mirador's own.</param>
    /// <param name="bundledDir">The layer that comes with Mirador (<c>{app}\layer</c>).</param>
    /// <exception cref="MiradorException">The folder or a file is missing; the message names it and says what to do.</exception>
    public static LayerFiles Locate(string? folder, string bundledDir)
    {
        var bundled = string.IsNullOrWhiteSpace(folder);
        if (bundled && string.IsNullOrWhiteSpace(bundledDir))
        {
            throw new MiradorException("This Mirador doesn't know where the direct-mode layer that comes with it is. Reinstall Mirador, or pick a layer folder in the profile.");
        }

        var root = bundled ? Path.GetFullPath(bundledDir) : UserFolder(folder!);
        root = Path.TrimEndingDirectorySeparator(root);

        if (!Directory.Exists(root))
        {
            throw new MiradorException(bundled
                ? $"The direct-mode layer that comes with Mirador is missing ({root}). Reinstall Mirador, or pick a layer folder in the profile."
                : $"The direct-mode layer folder {root} doesn't exist. Pick another folder in the profile, or clear it to use the layer that comes with Mirador.");
        }

        string[] manifestDirs = [root, Path.Combine(root, "layer")];
        var manifestDir = manifestDirs.FirstOrDefault(dir => LayerFileNames.All(name => File.Exists(Path.Combine(dir, name))))
            ?? throw (bundled ? BundledIncomplete(root, LayerFileNames) : NotALayerFolder(root, LayerFilesMissing(manifestDirs), "layer"));

        string[] checkExes = [Path.Combine(root, CheckExeName), Path.Combine(root, "check", CheckExeName)];
        var checkExe = checkExes.FirstOrDefault(File.Exists)
            ?? throw (bundled ? BundledIncomplete(root, [CheckExeName]) : NotALayerFolder(root, CheckExeName, "check"));

        return new LayerFiles(manifestDir, checkExe);
    }

    private static string UserFolder(string folder)
    {
        var trimmed = folder.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new MiradorException($"The direct-mode layer folder \"{trimmed}\" isn't a full path. Pick the folder again in the profile.");
        }

        return Path.GetFullPath(trimmed);
    }

    /// <summary>
    /// The layer files neither folder has, such as <c>dreamair_vk_layer.json or dreamair_vk_layer.dll</c>; when each
    /// is in one of them, that they aren't side by side.
    /// </summary>
    private static string LayerFilesMissing(string[] dirs)
    {
        var missing = LayerFileNames.Where(name => !dirs.Any(dir => File.Exists(Path.Combine(dir, name)))).ToList();
        return missing.Count > 0 ? string.Join(" or ", missing) : $"{ManifestName} and {DllName} side by side";
    }

    private static MiradorException NotALayerFolder(string root, string what, string subfolder) =>
        new($"The folder {root} has no {what}, in it or in its {subfolder} folder: is it the direct-mode layer's build or release folder? Pick another folder in the profile.");

    /// <summary>Mirador's own layer, which is flat, lacks some of <paramref name="names"/>: names the ones its folder lacks.</summary>
    private static MiradorException BundledIncomplete(string root, string[] names)
    {
        var missing = names.Where(name => !File.Exists(Path.Combine(root, name))).DefaultIfEmpty(names[0]).ToList();
        return new(
            $"The direct-mode layer that comes with Mirador is incomplete: {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} missing from {root}. Reinstall Mirador, or pick a layer folder in the profile.");
    }
}
