using Mirador.Core.Runtime;

namespace Mirador.Core.OpenVr;

/// <summary>What Home shows for OpenVR games.</summary>
/// <param name="Line">Where OpenVR games go now, in plain words.</param>
/// <param name="OffersMonado">The button is "Use Monado for OpenVR games" (else "Use SteamVR for OpenVR games").</param>
/// <param name="Warning">Something to know: the OpenXR runtime isn't Monado, or another program switched OpenVR games back.</param>
/// <param name="Problem">The paths file can't be read.</param>
public sealed record OpenVrView(string Line, bool OffersMonado, string? Warning, string? Problem);

/// <summary>
/// The "OpenVR games" switch on Home (Mirador.md §4.6 items 20–22): Monado through Mirador's OpenComposite copy, or
/// SteamVR. It is set and left: nothing else in Mirador touches the paths file, and Mirador never switches it back by
/// itself.
/// </summary>
public sealed class OpenVrGames(OpenVrPaths paths, OpenCompositeCopy copy, AppSettingsStore settings)
{
    public const string UseMonadoLabel = "Use Monado for OpenVR games";

    public const string UseSteamVrLabel = "Use SteamVR for OpenVR games";

    /// <summary>Whether this Mirador has OpenComposite to offer (not in a dev run without the bundle).</summary>
    public bool Available => copy.HasBundle;

    /// <summary>What Home shows now.</summary>
    /// <param name="openXrIsMonado">Whether the profile's Monado is the active OpenXR runtime.</param>
    /// <param name="activeOpenXr">The active OpenXR runtime's manifest; null when none is set.</param>
    public OpenVrView View(bool openXrIsMonado, string? activeOpenXr)
    {
        var state = paths.Read();
        if (state.Problem is not null)
        {
            return new OpenVrView("OpenVR games: unknown, the paths file can't be read", true, null, state.Problem);
        }

        if (state.Target == OpenVrTarget.Monado && !copy.HasCopy)
        {
            return new OpenVrView(
                "OpenVR games: Monado (OpenComposite)",
                true,
                null,
                $"Mirador's copy of OpenComposite is missing, so OpenVR games can't start. Press \"{UseMonadoLabel}\" to copy it again.");
        }

        var line = state.Target switch
        {
            OpenVrTarget.Monado => "OpenVR games: Monado (OpenComposite)",
            OpenVrTarget.SteamVr => "OpenVR games: SteamVR",
            OpenVrTarget.Other => $"OpenVR games: another runtime ({state.First})",
            _ => "No OpenVR runtime is set",
        };

        string? warning = null;
        if (state.Target == OpenVrTarget.Monado && !openXrIsMonado)
        {
            warning = activeOpenXr is null
                ? "No OpenXR runtime is set, so OpenVR games won't start: set Monado as OpenXR runtime too."
                : $"OpenVR games will go to {RuntimeLine.ManifestName(activeOpenXr)}, not Monado: set Monado as OpenXR runtime too.";
        }
        else if (state.Target != OpenVrTarget.Monado && settings.Load().OpenVrOnMonado)
        {
            var who = state.Target switch
            {
                OpenVrTarget.SteamVr => "SteamVR",
                OpenVrTarget.Other => state.First,
                _ => "another runtime",
            };
            warning = $"Something put {who} back in front for OpenVR games (SteamVR does this when it starts or updates). Press \"{UseMonadoLabel}\" to switch back.";
        }

        return new OpenVrView(line, state.Target != OpenVrTarget.Monado, warning, null);
    }

    /// <summary>
    /// Copies OpenComposite where games can use it, then puts it first in the paths file. The file is written only once
    /// a copy is in place: it never names a DLL that isn't there.
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

        if (paths.UseMonado() is { } problem)
        {
            return problem;
        }

        settings.Save(settings.Load() with { OpenVrOnMonado = true });
        return null;
    }

    /// <summary>Puts SteamVR back in front for OpenVR games.</summary>
    /// <returns>Null when done, else why nothing changed.</returns>
    public string? UseSteamVr()
    {
        if (paths.UseSteamVr() is { } problem)
        {
            return problem;
        }

        settings.Save(settings.Load() with { OpenVrOnMonado = false });
        return null;
    }

    /// <summary>
    /// At Mirador's launch: brings an existing copy up to date with the bundle, and makes it again when the paths file
    /// names it but it is gone (a virus scanner, a reset of Mirador's folder): that keeps the user's own choice working.
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
