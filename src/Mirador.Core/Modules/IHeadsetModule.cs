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

    /// <summary>
    /// Whether this profile points the module at a folder of its own (such as a Dream Air layer folder): that keeps
    /// Monado running as the user, since the elevated environment the helper builds carries no such path. The core
    /// asks this without naming the layer.
    /// </summary>
    bool UsesExtraFolder(Profile profile) => false;

    /// <summary>
    /// Whether this headset's display goes through NVIDIA direct mode (Mirador.md §4.11 item 52): the session then turns
    /// the direct-mode layer on for Monado, and the display check is offered.
    /// </summary>
    bool UsesNvidiaDirectMode => false;

    /// <summary>Ids the module had before (a renamed module): profiles and session files under them are its own.</summary>
    IReadOnlyList<string> FormerIds => [];

    /// <summary>
    /// The table the helper writes for this module when Monado runs as administrator (<c>ElevatedStart.Module</c>), for
    /// the headset as it is now; null when the helper has none for it, and Monado then runs as the user.
    /// </summary>
    string? ElevatedModuleId => Id;

    /// <summary>Whether this headset is among the plugged-in <paramref name="devices"/>.</summary>
    bool IsPresent(IReadOnlySet<UsbId> devices);

    /// <summary>Checks what this headset needs on the computer, for the first-run checks and Home.</summary>
    Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct);

    /// <summary>
    /// Gets the headset ready for Monado, which starts right after. <paramref name="progress"/> takes short phrases
    /// ("Uploading the eye geometry…") shown on the Monado status line while a step runs.
    /// </summary>
    /// <param name="saveRecovery">
    /// Call it with what <see cref="RecoverAsync"/> would need, right after each step that changes the computer (such as
    /// stopping the headset's own software), before any slow step: it goes into <c>session.json</c> at once, so that a
    /// Mirador killed or crashed during the later steps still undoes the change at its next launch. Each call replaces
    /// the last. It throws when the file can't be written; the module then undoes its steps and lets the exception out.
    /// Once this method has returned, the session's <see cref="ModuleSession.RecoveryData"/> is what is kept. When the
    /// method throws instead, <c>session.json</c> stays as long as the last save wasn't empty, and its data goes to
    /// <see cref="RecoverAsync"/> at the next launch or Start: so a module that has undone its steps before refusing
    /// saves an empty set last, and one whose undoing failed leaves the data that undoes the rest.
    /// </param>
    /// <exception cref="MiradorException">Monado must not start; the message says why and what to do. The module has
    /// undone its own steps before throwing.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (Stop was pressed while starting).
    /// As with a refusal, the module undoes its own steps before letting it out: Mirador ends only a session it was given.</exception>
    Task<ModuleSession> BeforeStartAsync(
        ModuleContext ctx, Profile profile, IProgress<string> progress, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct);

    /// <summary>
    /// Undoes the steps of a session that never ended (Mirador was killed or crashed while Monado ran or while it was
    /// starting), from that session's <see cref="ModuleSession.RecoveryData"/> or what <see cref="BeforeStartAsync"/>
    /// last saved. Runs when Mirador starts, and before a Start; it must be safe on steps that are partly undone already.
    /// </summary>
    Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct);

    /// <summary>Adds what this headset contributes to the diagnostics zip.</summary>
    void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink);
}
