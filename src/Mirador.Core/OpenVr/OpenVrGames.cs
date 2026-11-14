using Mirador.Core.Runtime;

namespace Mirador.Core.OpenVr;

/// <summary>What Home shows for OpenVR games.</summary>
/// <param name="Runtime">The active OpenVR runtime, in plain words.</param>
/// <param name="OffersOpenComposite">Whether Home offers "Set OpenLamina".</param>
/// <param name="Warning">Something to know: the OpenXR runtime isn't Monado while OpenLamina is in front.</param>
/// <param name="Problem">The paths file can't be read, or OpenLamina was uninstalled while OpenVR games still go to it.</param>
public sealed record OpenVrView(string Runtime, bool OffersOpenComposite, string? Warning, string? Problem);

/// <summary>
/// Home's "OpenVR runtime" row: OpenLamina as its installer put it, or whatever the paths file names. One way, like
/// the OpenXR runtime: "Set OpenLamina" puts it first, and nothing in Mirador switches
/// it back or forth by itself. SteamVR puts itself first again whenever it starts.
/// </summary>
public sealed class OpenVrGames(OpenVrPaths paths, OpenCompositeInstall install)
{
    /// <summary>Home's button for <see cref="UseMonado"/>.</summary>
    public const string SetOpenCompositeLabel = "Set OpenLamina";

    /// <summary>Whether OpenLamina is installed. Without it, Home offers "Get OpenLamina".</summary>
    public bool Available => install.IsInstalled;

    /// <summary>Home's "OpenVR runtime" row, from the paths file as it is now.</summary>
    /// <param name="openXrIsMonado">Whether the profile's Monado is the active OpenXR runtime.</param>
    /// <param name="activeOpenXr">The active OpenXR runtime's manifest; null when none is set.</param>
    public OpenVrView View(bool openXrIsMonado, string? activeOpenXr)
    {
        var state = paths.Read();
        if (state.Problem is not null)
        {
            return new OpenVrView("Unknown", false, null, state.Problem);
        }

        if (state.Target == OpenVrTarget.Monado)
        {
            if (!install.IsInstalled)
            {
                return new OpenVrView(
                    "OpenLamina",
                    false,
                    null,
                    "OpenLamina isn't installed any more, so OpenVR games can't start. Press \"Get OpenLamina\" to install it again.");
            }

            return new OpenVrView(
                "OpenLamina",
                false,
                openXrIsMonado ? null
                : activeOpenXr is null ? "No OpenXR runtime is set, so OpenVR games won't start: set Monado as OpenXR runtime too."
                : $"OpenVR games will go to {RuntimeLine.ManifestName(activeOpenXr)}, not Monado: set Monado as OpenXR runtime too.",
                null);
        }

        // The OpenComposite before OpenLamina is another app; OpenVR games going to it get what they got before, so say it
        if (state.Target == OpenVrTarget.Other && state.First is { } first && OpenVrPaths.SamePath(first, OpenCompositeInstall.LegacyFolder))
        {
            return new OpenVrView(
                "OpenComposite (before OpenLamina)",
                true,
                "OpenVR games still go to the old OpenComposite. Press \"" + SetOpenCompositeLabel + "\" to use OpenLamina instead; Windows' Installed apps removes the old one.",
                null);
        }

        var runtime = state.Target switch
        {
            OpenVrTarget.SteamVr => "SteamVR",
            OpenVrTarget.Other => state.First!,
            _ => "None set",
        };
        return new OpenVrView(runtime, true, null, null);
    }

    /// <summary>
    /// "Set OpenLamina": puts the installed OpenLamina first in the paths file, which never names a DLL that
    /// isn't there.
    /// </summary>
    /// <returns>Null when done, else why nothing changed.</returns>
    public string? UseMonado() =>
        install.IsInstalled ? paths.UseMonado() : "OpenLamina isn't installed yet: press \"Get OpenLamina\" first.";
}
