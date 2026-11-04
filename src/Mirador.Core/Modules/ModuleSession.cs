using Mirador.Core.Profiles;

namespace Mirador.Core.Modules;

/// <summary>
/// What a headset module did before Monado started, for as long as Monado runs: the variables Monado needs, what to
/// undo if Mirador goes away mid-session, the module's status lines and warnings, and the steps after Monado stopped.
/// </summary>
public abstract class ModuleSession
{
    /// <summary>The module's fixed variables for <c>monado-service</c>; a profile's own variables still win over them.</summary>
    public abstract IReadOnlyList<EnvVar> Environment { get; }

    /// <summary>
    /// What <see cref="IHeadsetModule.RecoverAsync"/> needs to undo this session's steps when Mirador ended without
    /// <see cref="EndAsync"/> (it was killed or crashed). Kept in <c>session.json</c> while Monado runs.
    /// </summary>
    public abstract IReadOnlyDictionary<string, string> RecoveryData { get; }

    /// <summary>The module's status lines, shown after Mirador's own. Read from any thread.</summary>
    public virtual IReadOnlyList<StatusLine> Status => [];

    /// <summary>Raised, on any thread, when <see cref="Status"/> is different.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised, on any thread, when the user should know something while Monado runs.</summary>
    public event Action<SessionWarning>? Warning;

    /// <summary>
    /// Takes the next line of Monado's output. Called on a background thread, one line at a time, while Monado's
    /// output waits: it must not block, and must not wait for another thread (the UI's above all).
    /// </summary>
    public virtual void OnServiceLine(string line)
    {
    }

    /// <summary>
    /// Undoes this session's steps once Monado has stopped (or failed to start). A failure should become a
    /// <see cref="Warning"/>; one that is thrown is shown too, and <c>session.json</c> is then kept for the next launch.
    /// </summary>
    public abstract Task EndAsync(CancellationToken ct);

    protected void RaiseStatusChanged() => StatusChanged?.Invoke();

    protected void RaiseWarning(SessionWarning w) => Warning?.Invoke(w);
}
