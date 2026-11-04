using Mirador.Core.Profiles;

namespace Mirador.Core.Modules;

/// <summary>
/// The steps one kind of headset needs around Monado: finding it, checking the computer, getting it ready before
/// Monado starts and putting things back afterwards. A profile names its module by <see cref="Id"/>; a profile
/// without one runs Monado with no headset steps.
/// </summary>
public interface IHeadsetModule
{
    /// <summary>The id a profile's <see cref="Profile.ModuleId"/> names, such as <c>dreamair</c>.</summary>
    string Id { get; }

    /// <summary>The headset's name for the user.</summary>
    string DisplayName { get; }

    /// <summary>Whether Monado tracks this headset through SteamVR's Lighthouse driver, which can't share the receiver with SteamVR.</summary>
    bool UsesSteamVrLighthouse { get; }

    /// <summary>Whether this headset is among the plugged-in <paramref name="devices"/>.</summary>
    bool IsPresent(IReadOnlySet<UsbId> devices);

    /// <summary>Checks what this headset needs on the computer, for the first-run checks and Home.</summary>
    Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct);

    /// <summary>
    /// Gets the headset ready for Monado, which starts right after. <paramref name="progress"/> takes short phrases
    /// ("Uploading the eye geometry…") shown on the Monado status line while a step runs.
    /// </summary>
    /// <exception cref="MiradorException">Monado must not start; the message says why and what to do. The module has
    /// undone its own steps before throwing.</exception>
    Task<ModuleSession> BeforeStartAsync(ModuleContext ctx, Profile profile, IProgress<string> progress, CancellationToken ct);

    /// <summary>
    /// Undoes the steps of a session that never ended (Mirador was killed or crashed while Monado ran), from that
    /// session's <see cref="ModuleSession.RecoveryData"/>. Runs when Mirador starts.
    /// </summary>
    Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct);

    /// <summary>Adds what this headset contributes to the diagnostics zip.</summary>
    void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink);
}
