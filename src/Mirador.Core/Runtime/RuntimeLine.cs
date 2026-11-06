using Mirador.Core.Monado;

namespace Mirador.Core.Runtime;

/// <summary>Home's "OpenXR runtime" row: which OpenXR runtime games will use.</summary>
public static class RuntimeLine
{
    /// <summary>
    /// "Monado, this profile" when <paramref name="isActive"/>; otherwise the active manifest's file name, or
    /// "None set".
    /// </summary>
    /// <param name="activeManifest">The active runtime's manifest path, as the registry holds it; null when none is set.</param>
    /// <param name="install">The selected profile's Monado, when it could be found.</param>
    /// <param name="isActive">Whether <paramref name="install"/>'s manifest is the active runtime.</param>
    public static string Describe(string? activeManifest, MonadoInstall? install, bool isActive)
    {
        if (isActive && install is not null)
        {
            return "Monado, this profile";
        }

        if (string.IsNullOrWhiteSpace(activeManifest))
        {
            return "None set";
        }

        return ManifestName(activeManifest);
    }

    /// <summary>A runtime manifest's file name, as the runtime line shows it: <c>PiOpenXR_64.json</c>.</summary>
    public static string ManifestName(string manifest) =>
        // Split on both separators: the registry may hold either.
        manifest.Trim().Trim('"').Split('\\', '/').LastOrDefault(part => part.Length > 0) ?? manifest;
}
