using Mirador.Core;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.DreamAir;

/// <summary>
/// A Dream Air session while Monado runs: the fixed environment, Pimax's service to start again afterwards, the
/// status lines read from Monado's output, and a watch for programs that would take the headset or the receiver back.
/// </summary>
public sealed class DreamAirSession : ModuleSession, IDisposable
{
    public const string RecoveryKey = "pimaxWasRunning";

    /// <summary>How often the session looks for Pimax's software and SteamVR.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    internal const string PimaxStarted =
        "Pimax's software started while Monado runs and may take the headset back. Close Pimax Play.";

    internal const string SteamVrStarted =
        "SteamVR started while Monado runs. Close it: it competes with Monado for the Lighthouse receiver.";

    private readonly ModuleContext ctx;
    private readonly bool pimaxWasRunning;
    private readonly DreamAirLogParser parser;
    private readonly object gate = new();
    private readonly ITimer timer;
    private readonly Action? afterMonado;
    private List<SessionWarning>? pending;
    private bool pimaxSeen;
    private bool steamVrSeen;
    private bool ended;

    /// <param name="ctx">The module's context: the helper, the process list and the clock.</param>
    /// <param name="environment">The variables Monado gets for the Dream Air.</param>
    /// <param name="pimaxWasRunning">Whether Pimax's service ran before the session stopped it, so it is started again at the end.</param>
    /// <param name="upload">How the eye geometry upload went, the first state of the eye tracker's line.</param>
    /// <param name="afterMonado">Runs first in <see cref="EndAsync"/>, once Monado has stopped (the calibrations' copies).</param>
    /// <param name="warnings">Warnings from getting ready, raised at the first look (Mirador listens only once it has the session).</param>
    public DreamAirSession(
        ModuleContext ctx, IReadOnlyList<EnvVar> environment, bool pimaxWasRunning, GeometryResult? upload, Action? afterMonado = null,
        IReadOnlyList<SessionWarning>? warnings = null)
    {
        this.ctx = ctx;
        this.afterMonado = afterMonado;
        pending = warnings is { Count: > 0 } ? [.. warnings] : null;
        this.pimaxWasRunning = pimaxWasRunning;
        Environment = environment;
        RecoveryData = Recovery(pimaxWasRunning);
        parser = new DreamAirLogParser(upload);
        timer = ctx.Time.CreateTimer(_ => Poll(), null, PollInterval, Timeout.InfiniteTimeSpan);
    }

    public override IReadOnlyList<EnvVar> Environment { get; }

    public override IReadOnlyDictionary<string, string> RecoveryData { get; }

    public override IReadOnlyList<StatusLine> Status => parser.Lines;

    /// <summary>The recovery data for whether Pimax's service ran.</summary>
    public static IReadOnlyDictionary<string, string> Recovery(bool pimaxWasRunning) =>
        new Dictionary<string, string> { [RecoveryKey] = pimaxWasRunning ? "true" : "false" };

    public override void OnServiceLine(string line)
    {
        if (parser.Feed(line))
        {
            RaiseStatusChanged();
        }
    }

    /// <summary>
    /// Stops the watch and starts Pimax's service again if it ran. A failure is thrown, so that Mirador shows it as a
    /// warning and keeps <c>session.json</c>: recovery then tries again at the next launch or Start.
    /// </summary>
    /// <exception cref="MiradorException">Pimax's service couldn't be started again.</exception>
    public override async Task EndAsync(CancellationToken ct)
    {
        StopWatching();
        afterMonado?.Invoke();
        if (!pimaxWasRunning)
        {
            return;
        }

        var reply = await ctx.Helper.SendAsync(HelperCommand.StartPimax, ct).ConfigureAwait(false);
        if (!reply.Ok)
        {
            throw new MiradorException($"Mirador couldn't start Pimax's service again: {reply.Error} Mirador tries again the next time it starts.");
        }
    }

    public void Dispose() => StopWatching();

    private void StopWatching()
    {
        lock (gate)
        {
            ended = true;
        }

        timer.Dispose();
    }

    /// <summary>One look at the running programs; warns once each time Pimax's software or SteamVR appears.</summary>
    private void Poll()
    {
        IReadOnlyList<ProcInfo> running;
        try
        {
            running = ctx.Processes.Snapshot();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            running = [];
        }

        var pimax = running.Any(p =>
            string.Equals(p.Name, PimaxNames.Service, StringComparison.OrdinalIgnoreCase)
            || PimaxNames.Processes.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        var steamVr = LighthouseConflicts.Find(running).Count > 0;

        var warnings = new List<SessionWarning>();
        lock (gate)
        {
            if (ended)
            {
                return;
            }

            if (pending is not null)
            {
                warnings.AddRange(pending);
                pending = null;
            }

            if (pimax && !pimaxSeen)
            {
                warnings.Add(new SessionWarning("pimax", PimaxStarted));
            }

            if (steamVr && !steamVrSeen)
            {
                warnings.Add(new SessionWarning("lighthouse", SteamVrStarted));
            }

            pimaxSeen = pimax;
            steamVrSeen = steamVr;
            timer.Change(PollInterval, Timeout.InfiniteTimeSpan);
        }

        foreach (var warning in warnings)
        {
            RaiseWarning(warning);
        }
    }
}
