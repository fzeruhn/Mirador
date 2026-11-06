using Mirador.Core.Runtime;

namespace Mirador.Core.OpenVr;

/// <summary>What Home shows for OpenVR games.</summary>
/// <param name="Runtime">The active OpenVR runtime, in plain words.</param>
/// <param name="OffersOpenComposite">Whether Home offers "Set OpenComposite".</param>
/// <param name="Warning">Something to know: the OpenXR runtime isn't Monado while OpenComposite is in front.</param>
/// <param name="Problem">The paths file can't be read, or Mirador's copy is gone.</param>
public sealed record OpenVrView(string Runtime, bool OffersOpenComposite, string? Warning, string? Problem);

/// <summary>
/// Home's "OpenVR runtime" row (Mirador.md §4.6 items 20–22): OpenComposite, Mirador's copy, or whatever the paths file
/// names. One way, like the OpenXR runtime: "Set OpenComposite" puts the copy first, and nothing in Mirador switches it
/// back or forth by itself (Finn, 2026-11-06). SteamVR puts itself first again whenever it starts.
/// </summary>
public sealed class OpenVrGames(OpenVrPaths paths, OpenCompositeCopy copy)
{
    /// <summary>Home's button for <see cref="UseMonado"/>.</summary>
    public const string SetOpenCompositeLabel = "Set OpenComposite";

    /// <summary>Whether this Mirador has OpenComposite to offer (not in a dev run without the bundle).</summary>
    public bool Available => copy.HasBundle;

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
            if (!copy.HasCopy)
            {
                return new OpenVrView(
                    "OpenComposite",
                    true,
                    null,
                    $"Mirador's copy of OpenComposite is missing, so OpenVR games can't start. Press \"{SetOpenCompositeLabel}\" to copy it again.");
            }

            return new OpenVrView(
                "OpenComposite",
                false,
                openXrIsMonado ? null
                : activeOpenXr is null ? "No OpenXR runtime is set, so OpenVR games won't start: set Monado as OpenXR runtime too."
                : $"OpenVR games will go to {RuntimeLine.ManifestName(activeOpenXr)}, not Monado: set Monado as OpenXR runtime too.",
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
    /// "Set OpenComposite": copies OpenComposite where games can use it, then puts it first in the paths file. The
    /// file is written only once a copy is in place: it never names a DLL that isn't there.
    /// </summary>
    /// <returns>Null when done, else why nothing changed.</returns>
    public string? UseMonado()
    {
        CopyOutcome outcome;
        try
        {
            outcome = copy.Update();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return CouldNotCopy(e);
        }

        if (outcome == CopyOutcome.NoBundle)
        {
            return "OpenComposite isn't in this Mirador build.";
        }

        if (!copy.HasCopy)
        {
            return CouldNotCopy(null);
        }

        return paths.UseMonado();
    }

    /// <summary>
    /// At Mirador's launch: brings an existing copy up to date with the bundle, and makes it again when the paths file
    /// names it but it is gone (a virus scanner, a reset of Mirador's folder). It never switches OpenVR games.
    /// </summary>
    /// <returns>A notice for Home when that has to wait, else null.</returns>
    public string? UpdateAtLaunch()
    {
        if (!copy.HasCopy && paths.Read().Target != OpenVrTarget.Monado)
        {
            return null;
        }

        try
        {
            return copy.Update() == CopyOutcome.InUse ? "OpenComposite will be updated when no game uses it." : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Mirador couldn't update its copy of OpenComposite ({Reason(e)}); OpenVR games use the old one.";
        }
    }

    private static string CouldNotCopy(Exception? e) =>
        $"Mirador couldn't copy OpenComposite ({(e is null ? "a game may be using it" : Reason(e))}); OpenVR games are unchanged.";

    private static string Reason(Exception e) => e.Message.Trim().TrimEnd('.');
}
