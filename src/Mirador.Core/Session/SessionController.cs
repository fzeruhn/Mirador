using Mirador.Core.DirectMode;
using System.Globalization;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Session;

/// <summary>What the session is doing.</summary>
public enum SessionState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>Who asked for a Start. It does not change what Start does.</summary>
public enum StartReason
{
    Button,
    Tray,
    Game,
    Check,
}

/// <summary>How a Start ended.</summary>
/// <param name="Ok">True when Monado is running.</param>
/// <param name="Message">What happened, in plain words; for a refusal or a failure, why and what to do.</param>
public sealed record StartOutcome(bool Ok, string Message);

/// <summary>What a session runs with.</summary>
/// <param name="Profile">The profile, as it was read at the session's Start.</param>
/// <param name="Module">Its headset module; null for a profile without one.</param>
public sealed record SessionProfile(Profile Profile, IHeadsetModule? Module);

/// <summary>
/// Starts and stops Monado's service for the selected profile, with its headset module's steps before and after, and
/// follows Monado's output for the log and the status lines.
/// </summary>
/// <remarks>
/// <para>
/// Everything here may be called from any thread. The events are raised on whichever thread made the change, mostly
/// background ones, and <see cref="LogLine"/> and <see cref="StatusChanged"/> often while Monado's output waits for
/// them: a subscriber must return quickly and hand any UI work to its UI thread asynchronously
/// (<c>DispatcherQueue.TryEnqueue</c>), never wait for that thread. An exception thrown by a subscriber is dropped, so
/// that it can't leave a Start or a Stop half done.
/// </para>
/// </remarks>
public sealed class SessionController(SessionDeps deps)
{
    /// <summary>How long Start waits for Monado to be ready for apps once its service runs.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    private const string ServiceName = "monado-service";
    private const string NotActiveNotice = "Monado (this profile) isn't the active OpenXR runtime, so games won't find it.";
    private const int KeptLogs = 10;
    private const int LastLinesShown = 5;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a module's <see cref="ModuleSession.EndAsync"/> may take before Mirador stops waiting for it.</summary>
    private static readonly TimeSpan EndLimit = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyDictionary<string, string> Nothing = new Dictionary<string, string>();

    private readonly SessionDeps deps = deps;
    private readonly SessionFile sessionFile = new(deps.Context.Paths.SessionFile);
    private readonly Func<string, bool> isStandIn = StandIn.IsStandIn;

    private readonly object gate = new();
    private SessionState state = SessionState.Stopped;
    private Run? current;
    private Task<StartOutcome>? starting;
    private CancellationTokenSource? startCancel;
    private Task? stopping;
    private Task recovering = Task.CompletedTask;
    private MonadoInstall? currentInstall;
    private IReadOnlyDictionary<string, string>? lastMiradorEnvironment;
    private TextLog? lastLog;

    /// <summary>For tests: <paramref name="isStandIn"/> stands in for <see cref="StandIn.IsStandIn"/>.</summary>
    internal SessionController(SessionDeps deps, Func<string, bool> isStandIn) : this(deps) => this.isStandIn = isStandIn;

    /// <summary>Raised when <see cref="State"/>, <see cref="CurrentInstall"/>, <see cref="RuntimeNotice"/> or <see cref="LastMiradorEnvironment"/> may be different.</summary>
    public event Action? Changed;

    /// <summary>Raised when <see cref="Status"/> may be different.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised with each line of Monado's output, in order, while Monado's output waits.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised when the user should know something during a session: from the headset module, or about Monado's log.</summary>
    public event Action<SessionWarning>? Warning;

    /// <summary>
    /// Raised once a running session has ended, after <see cref="State"/> is <see cref="SessionState.Stopped"/>, with
    /// what happened ("Stopped.", or why Monado stopped by itself). A Start that fails says so through its outcome instead.
    /// </summary>
    public event Action<string>? Ended;

    public SessionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>
    /// The status lines: <c>monado</c>; while there is a session also <c>apps</c>, <c>tracking</c> (when the profile
    /// uses the Lighthouse receiver), <c>standin</c> (when games can't start Monado from the build), then the module's.
    /// </summary>
    public IReadOnlyList<StatusLine> Status
    {
        get
        {
            List<StatusLine> lines;
            ModuleSession? session = null;
            lock (gate)
            {
                lines = [MonadoLine()];
                if (current is { } run)
                {
                    lines.Add(AppsLine(run.Clients.ConnectedApps));
                    if (run.UsesLighthouse)
                    {
                        lines.Add(TrackingLine(run.Lighthouse));
                    }

                    if (run.StandIn is { } standIn)
                    {
                        lines.Add(standIn);
                    }

                    session = run.Session;
                }
            }

            // The module's own lines are read outside the lock: that is the module's code.
            if (session is not null)
            {
                lines.AddRange(session.Status);
            }

            return lines;
        }
    }

    /// <summary>
    /// The controllers and trackers Monado has found in the current session, in the order found; null unless a session
    /// whose profile uses the Lighthouse receiver is starting or running. A snapshot, safe to read from any thread.
    /// </summary>
    public IReadOnlyList<TrackedDevice>? Devices
    {
        get
        {
            lock (gate)
            {
                return current is { UsesLighthouse: true } run ? run.Lighthouse.Devices : null;
            }
        }
    }

    /// <summary>The last lines of Monado's output in the current or last session (up to 200), oldest first.</summary>
    public IReadOnlyList<string> LogTail
    {
        get
        {
            TextLog? log;
            lock (gate)
            {
                log = lastLog;
            }

            return log?.Tail ?? [];
        }
    }

    /// <summary>
    /// While there is a session: a note when its Monado isn't the active OpenXR runtime, since games then don't find
    /// it; else null. Read fresh from the registry each time, so it goes once the runtime is set.
    /// </summary>
    public string? RuntimeNotice =>
        CurrentInstall is { } install && !deps.Runtime.IsActive(install.Manifest) ? NotActiveNotice : null;

    /// <summary>
    /// The profile and headset module of the session that is starting (once the Start has read the profile), running or
    /// stopping, until it has ended (after <see cref="ISessionHook.Ended"/>); null otherwise.
    /// </summary>
    public SessionProfile? CurrentProfile
    {
        get
        {
            lock (gate)
            {
                return current is { Profile: { } profile } run ? new SessionProfile(profile, run.Module) : null;
            }
        }
    }

    /// <summary>The Monado build of the session that is starting or running; null when stopped.</summary>
    public MonadoInstall? CurrentInstall
    {
        get
        {
            lock (gate)
            {
                return currentInstall;
            }
        }
    }

    /// <summary>
    /// The variables Mirador itself set for Monado at the last Start (the settings, the module's and the profile's),
    /// without the ones inherited from Windows, so with no personal paths; null before the first Start.
    /// </summary>
    public IReadOnlyDictionary<string, string>? LastMiradorEnvironment
    {
        get
        {
            lock (gate)
            {
                return lastMiradorEnvironment;
            }
        }
    }

    /// <summary>
    /// Starts Monado with the selected profile: refuses while another Monado runs (a <c>monado-service</c> that isn't
    /// Mirador's stand-in), or while a program holds the Lighthouse receiver the profile needs; runs the headset module's steps; starts the service and waits
    /// until it is ready for apps (at most <see cref="StartTimeout"/>). Callers during a Start share its outcome; a Start
    /// while Monado runs succeeds at once; a Start while stopping waits for the stop, then starts.
    /// </summary>
    /// <param name="reason">Who asked.</param>
    /// <param name="ct">Stops this caller's waiting only: a Start under way serves every caller, and only
    /// <see cref="StopAsync"/> breaks it off.</param>
    public Task<StartOutcome> StartAsync(StartReason reason, CancellationToken ct) => StartAsync(reason, null, ct);

    /// <inheritdoc cref="StartAsync(StartReason, CancellationToken)"/>
    /// <param name="gameStandIn">For a game's Start: the stand-in its Monado runtime DLL started
    /// (<see cref="StandIn.ProgramOf"/>). Only that Monado may start for it, since the game's DLL then talks to the
    /// service: a Start for another build than the selected profile's is refused before any step, and one that joins or
    /// finds Monado of another build is refused too (that Monado stays).</param>
    public async Task<StartOutcome> StartAsync(StartReason reason, string? gameStandIn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        while (true)
        {
            Task? stopFirst = null;
            Task<StartOutcome>? shared = null;
            var joined = false;
            lock (gate)
            {
                switch (state)
                {
                    case SessionState.Running:
                        joined = true;
                        shared = Task.FromResult(new StartOutcome(true, "Monado is already running."));
                        break;
                    case SessionState.Starting:
                        joined = true;
                        shared = starting;
                        break;
                    case SessionState.Stopping:
                        stopFirst = stopping;
                        break;
                    default:
                        shared = BeginStart(reason, gameStandIn);
                        break;
                }
            }

            if (stopFirst is not null)
            {
                await stopFirst.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var outcome = await shared!.WaitAsync(ct).ConfigureAwait(false);
            if (joined && outcome.Ok && gameStandIn is not null)
            {
                MonadoInstall? install;
                Profile? profile;
                lock (gate)
                {
                    install = currentInstall;
                    profile = current?.Profile;
                }

                if (install is not null && !StandIn.BelongsTo(gameStandIn, install))
                {
                    return Fail(OtherMonado(gameStandIn, install, profile));
                }
            }

            return outcome;
        }
    }

    /// <summary>
    /// Puts the stand-in below the selected profile's build folder now, as every Start does too, so that a game can
    /// start Monado from that build before Mirador has started it once. Call at launch and when the profiles change.
    /// Does nothing for the bundled Monado (its stand-in is the installer's) or a build that can't be found (its Start
    /// says why).
    /// </summary>
    public void PlaceStandIn() => PlaceStandIn(deps.Profiles.Load().Selected);

    /// <summary>Puts the stand-in below <paramref name="profile"/>'s build folder (Verify's game start needs it there).</summary>
    public void PlaceStandIn(Profile profile)
    {
        try
        {
            if (profile.Monado.Kind is SourceKind.Folder or SourceKind.Branch)
            {
                var install = deps.Locator.Locate(profile);
                StandInCopy.Place(deps.StandInExe, install.StandInDir, install.ServiceExe);
            }
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Why a game whose runtime is another Monado than <paramref name="install"/> doesn't get it.</summary>
    private static string OtherMonado(string gameStandIn, MonadoInstall install, Profile? profile)
    {
        var games = Path.GetDirectoryName(Path.GetDirectoryName(gameStandIn)) ?? gameStandIn;
        var ours = Path.GetDirectoryName(install.RuntimeDll) ?? install.Manifest;
        var whose = profile is null ? "Mirador's profile" : $"the profile \"{profile.Name}\"";
        return $"The game uses the Monado in {games}, but {whose} uses the one in {ours}. Pick the profile with the game's Monado in Mirador, or set this profile's Monado as the OpenXR runtime.";
    }

    /// <summary>
    /// Ends Monado and the headset module's session, and deletes <c>session.json</c>. During a Start, breaks it off.
    /// Does nothing when stopped.
    /// </summary>
    /// <param name="ct">Stops this caller's waiting only: a stop, once begun, runs to its end, since a half-done one
    /// would leave the headset's software stopped. The module's part is given at most 60 s; after that the session is
    /// over anyway, <c>session.json</c> stays and the module's steps are undone before Monado next starts.</param>
    public async Task StopAsync(CancellationToken ct)
    {
        while (true)
        {
            Task? stop = null;
            Task<StartOutcome>? start = null;
            CancellationTokenSource? breakOff = null;
            lock (gate)
            {
                switch (state)
                {
                    case SessionState.Stopped:
                        return;
                    case SessionState.Starting:
                        start = starting;
                        breakOff = startCancel;
                        break;
                    case SessionState.Stopping:
                        stop = stopping;
                        break;
                    default:
                        stop = BeginStop(current!, "Stopped.");
                        break;
                }
            }

            if (stop is not null)
            {
                await stop.WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            // Outside the lock: cancelling runs the Start's cancellation callbacks on this thread.
            breakOff?.Cancel();
            if (!(await start!.WaitAsync(ct).ConfigureAwait(false)).Ok)
            {
                return;
            }

            // Monado came up before the Start saw the request: stop it now.
        }
    }

    /// <summary>
    /// When Mirador went away mid-session (killed or crashed), <c>session.json</c> was left behind: once no Monado
    /// runs (stand-ins don't count), hands its data to the module's <see cref="IHeadsetModule.RecoverAsync"/>, then deletes
    /// it. Meant for Mirador's start; a Start that comes in meanwhile waits for it. A failure becomes a
    /// <see cref="Warning"/> and the file stays. Every Start also undoes a session left behind before its own steps
    /// (and refuses when that fails), so the file is never replaced while it still holds something to undo.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        Task? already = null;
        TaskCompletionSource? done = null;
        lock (gate)
        {
            if (state != SessionState.Stopped)
            {
                return; // session.json belongs to the session that runs now
            }

            if (!recovering.IsCompleted)
            {
                already = recovering;
            }
            else
            {
                done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                recovering = done.Task;
            }
        }

        if (already is not null)
        {
            await already.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await RecoverCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            done!.SetResult();
        }
    }

    /// <summary>
    /// Runs a headset module's own work while Monado is stopped, such as the Dream Air's display check, the way recovery
    /// runs: a Start (from any caller) waits until it is done, and what the work saves through its first argument goes
    /// into <c>session.json</c> at once (an empty set deletes the file), so that recovery undoes the work's steps if
    /// Mirador goes away meanwhile. A session left behind earlier is undone first. Works run one after another.
    /// </summary>
    /// <exception cref="MiradorException">Monado isn't stopped, or what an earlier session left behind couldn't be undone.</exception>
    public async Task<T> RunWhileStoppedAsync<T>(
        string moduleId, Func<Action<IReadOnlyDictionary<string, string>>, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        Task before;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (state != SessionState.Stopped)
            {
                throw new MiradorException("Stop Monado first.");
            }

            before = recovering;
            recovering = done.Task;
        }

        try
        {
            await before.WaitAsync(ct).ConfigureAwait(false);
            if (sessionFile.Read() is { } left)
            {
                if (FindService(deps.Context.Processes.Snapshot()) is { } service)
                {
                    throw new MiradorException(
                        $"Monado is running outside Mirador ({ServiceName}, process {service.Id}). Close it, then try again.");
                }

                await UndoLeftSessionAsync(left.ModuleId, left.Data, ct).ConfigureAwait(false);
            }

            var profileId = deps.Profiles.Load().SelectedId;
            void Save(IReadOnlyDictionary<string, string> data)
            {
                if (data.Count == 0)
                {
                    sessionFile.Delete();
                }
                else
                {
                    sessionFile.Write(profileId, moduleId, data);
                }
            }

            return await work(Save, ct).ConfigureAwait(false);
        }
        finally
        {
            done.SetResult();
        }
    }

    /// <summary>Completes when no recovery and no module work (<see cref="RunWhileStoppedAsync"/>) is under way.</summary>
    public Task WhenSettledAsync()
    {
        lock (gate)
        {
            return recovering;
        }
    }

    private async Task RecoverCoreAsync(CancellationToken ct)
    {
        try
        {
            if (sessionFile.Read() is not { } saved)
            {
                return;
            }

            // A service that still runs may be the one this session started: its steps must not be undone under it.
            if (FindService(deps.Context.Processes.Snapshot()) is not null)
            {
                return;
            }

            await UndoLeftSessionAsync(saved.ModuleId, saved.Data, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            RaiseWarning(new SessionWarning("headset", LeftSessionProblem(e)));
        }
    }

    /// <summary>
    /// Hands what a session left in <c>session.json</c> to its module's <see cref="IHeadsetModule.RecoverAsync"/>, then
    /// deletes the file. When the module fails, its exception comes out and the file stays.
    /// </summary>
    private async Task UndoLeftSessionAsync(string? moduleId, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        if (FindModule(moduleId) is { } module)
        {
            await module.RecoverAsync(deps.Context, data, ct).ConfigureAwait(false);
        }

        sessionFile.Delete();
    }

    private static string LeftSessionProblem(Exception e) =>
        e is MiradorException
            ? e.Message
            : $"Mirador couldn't undo what its last session left behind ({Trimmed(e.Message)}). It tries again the next time Mirador starts.";

    /// <summary>Moves to Starting and sets the Start off. Called under the lock.</summary>
    private Task<StartOutcome> BeginStart(StartReason reason, string? gameStandIn)
    {
        var run = new Run { ForGame = reason == StartReason.Game, GameStandIn = gameStandIn };
        var cancel = new CancellationTokenSource(); // not disposed: it has no timer, and a late Stop may still cancel it
        var done = new TaskCompletionSource<StartOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        state = SessionState.Starting;
        current = run;
        startCancel = cancel;
        starting = done.Task;
        var recovery = recovering;
        _ = Task.Run(() => RunStartAsync(run, recovery, done, cancel.Token), CancellationToken.None);
        return done.Task;
    }

    /// <summary>Moves a running session to Stopping and sets its end off. Called under the lock.</summary>
    private Task BeginStop(Run run, string endedMessage)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state = SessionState.Stopping;
        stopping = done.Task;
        _ = Task.Run(() => FinishAsync(run, done, endedMessage), CancellationToken.None);
        return done.Task;
    }

    private async Task RunStartAsync(Run run, Task recovery, TaskCompletionSource<StartOutcome> done, CancellationToken ct)
    {
        var outcome = Fail("Mirador couldn't start Monado.");
        try
        {
            RaiseChanged();
            RaiseStatusChanged();
            try
            {
                await recovery.WaitAsync(ct).ConfigureAwait(false);
                outcome = await StartCoreAsync(run, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = Fail("Monado was stopped while it was starting.");
            }
            catch (MiradorException e)
            {
                outcome = Fail(e.Message);
            }
            catch (Exception e)
            {
                outcome = Fail($"Mirador couldn't start Monado: {e.Message}");
            }

            if (!outcome.Ok)
            {
                await EndRunAsync(run).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever happened above, the state moves on and every caller gets an outcome.
            lock (gate)
            {
                state = outcome.Ok ? SessionState.Running : SessionState.Stopped;
                starting = null;
                startCancel = null;
                if (!outcome.Ok)
                {
                    current = null;
                    currentInstall = null;
                }
            }

            RaiseChanged();
            RaiseStatusChanged();
            done.SetResult(outcome);
            if (outcome.Ok)
            {
                _ = WatchAsync(run);
            }
        }
    }

    /// <summary>The steps of a Start, in order. A refusal is returned; whatever was set up is ended by the caller.</summary>
    private async Task<StartOutcome> StartCoreAsync(Run run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var running = deps.Context.Processes.Snapshot();
        if (FindService(running) is { } other)
        {
            return Fail($"Monado is already running outside Mirador ({ServiceName}, process {Number(other.Id)}). Close it, then press Start.");
        }

        // A session that wasn't undone (Mirador went away, or its module failed to end) is undone first: the new
        // session's recovery data would otherwise replace what that one needs.
        if (sessionFile.Read() is { } left)
        {
            try
            {
                await UndoLeftSessionAsync(left.ModuleId, left.Data, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return Fail(LeftSessionProblem(e));
            }
        }

        var profile = deps.Profiles.Load().Selected;
        IHeadsetModule? module = null;
        if (profile.ModuleId is { } moduleId && (module = FindModule(moduleId)) is null)
        {
            return Fail($"This profile uses a headset Mirador doesn't know ({moduleId}).");
        }

        var install = deps.Locator.Locate(profile);
        if (run.GameStandIn is { } gameStandIn && !StandIn.BelongsTo(gameStandIn, install))
        {
            return Fail(OtherMonado(gameStandIn, install, profile));
        }

        // A game that only asks whether a headset is there starts Monado (OpenComposite does, Mirador.md §4.6 item 25):
        // with the headset unplugged, refuse before any step, so the headset's own software is never touched and the game goes on without VR.
        if (run.ForGame && module is not null && !module.IsPresent(deps.Context.Usb.Present()))
        {
            return Fail($"Monado wasn't started: the {module.DisplayName} isn't connected.");
        }

        var usesLighthouse = LighthouseConflicts.UsesLighthouse(profile, module?.UsesSteamVrLighthouse ?? false);
        lock (gate)
        {
            run.Profile = profile;
            run.Module = module;
            run.UsesLighthouse = usesLighthouse;
            currentInstall = install;
        }

        RaiseChanged();
        RaiseStatusChanged();

        if (usesLighthouse && LighthouseConflicts.Find(running) is { Count: > 0 } conflicts)
        {
            return Fail(LighthouseConflicts.Message(conflicts));
        }

        // A headset that isn't plugged in skips the hook, whatever started the session; the steps below go on as before.
        if (module is null || module.IsPresent(deps.Context.Usb.Present()))
        {
            run.HookStarted = true;
            CallHook(profile, module, started: true);
        }

        run.Settings = deps.MonadoSettings.Load();
        if (module is not null)
        {
            var recovery = new RecoveryWriter(run, data => sessionFile.Write(profile.Id, module.Id, data));
            ModuleSession session;
            try
            {
                session = await module.BeforeStartAsync(
                    deps.Context, profile, new Reporter(text => ShowProgress(run, text)), recovery.Save, ct).ConfigureAwait(false);
            }
            catch when (recovery.LeftSomethingToUndo)
            {
                // The module refused (or was stopped) without saving that it had undone everything: keep session.json,
                // so that its RecoverAsync, which is safe to repeat, finishes the job at the next launch or Start.
                run.WroteSessionFile = false;
                throw;
            }
            finally
            {
                recovery.Close();
            }

            Attach(run, session);
            ShowProgress(run, null);
            ct.ThrowIfCancellationRequested();
        }

        sessionFile.Write(profile.Id, module?.Id, run.Session?.RecoveryData ?? Nothing);
        run.WroteSessionFile = true;

        var moduleEnvironment = run.Session?.Environment ?? [];
        if (module?.UsesNvidiaDirectMode == true)
        {
            // The layer, turned on by the core for a headset that uses NVIDIA direct mode (Mirador.md §4.11 item 52);
            // the module's own variables come after it.
            moduleEnvironment = [.. DirectModeLayer.Environment(deps.BundledLayerDir ?? ""), .. moduleEnvironment];
        }

        var environment = MonadoEnvironment.Build(deps.InheritedEnvironment, run.Settings, profile, moduleEnvironment);
        var mine = MonadoEnvironment.Build(Nothing, run.Settings, profile, moduleEnvironment);
        var standIn = install.IsBundled
            ? null
            : StandInCopy.Place(deps.StandInExe, install.StandInDir, install.ServiceExe) ?? ServiceLaunch.Check(Path.GetDirectoryName(install.Manifest)!);

        var logPath = deps.Context.Paths.NewMonadoLogFile(deps.Context.Time.GetLocalNow().DateTime);
        MonadoLogs.Prune(deps.Context.Paths.LogsDir, logPath, KeptLogs);
        run.Log = new TextLog(logPath);
        lock (gate)
        {
            run.StandIn = standIn;
            lastMiradorEnvironment = mine;
            lastLog = run.Log;
        }

        RaiseChanged();
        RaiseStatusChanged();
        ct.ThrowIfCancellationRequested();

        var launcher = ChooseLauncher(install, profile, module, run.Settings);
        var process = launcher.Start(new ProcessSpec(
            install.ServiceExe, "", Path.GetDirectoryName(install.ServiceExe)!, environment));
        run.Process = process;
        Listen(run, process);

        return await WaitForPipeAsync(run, process, deps.Pipe.NameFor(environment), logPath, ct).ConfigureAwait(false);
    }

    /// <summary>Looks for Monado's pipe every 100 ms until it is there, the service ends, or <see cref="StartTimeout"/> has passed.</summary>
    private async Task<StartOutcome> WaitForPipeAsync(Run run, IRunningProcess process, string pipeName, string logPath, CancellationToken ct)
    {
        var time = deps.Context.Time;
        using var timeout = new CancellationTokenSource(StartTimeout, time);
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (true)
            {
                if (process.Exited.IsCompleted)
                {
                    var code = await process.Exited.ConfigureAwait(false);
                    return Fail(StoppedWhileStarting(code, run.Log!.Tail));
                }

                if (deps.Pipe.Exists(pipeName))
                {
                    return new StartOutcome(true, "Monado is running.");
                }

                if (timeout.IsCancellationRequested)
                {
                    return Fail($"Monado didn't come up within {Number((int)StartTimeout.TotalSeconds)} s. Its log: {logPath}");
                }

                ct.ThrowIfCancellationRequested();

                // A cancelled delay ends the wait too; the checks above then say why.
                await Task.WhenAny(process.Exited, Task.Delay(PollInterval, time, wake.Token)).ConfigureAwait(false);
            }
        }
        finally
        {
            wake.Cancel(); // drops the pending delay's timer
        }
    }

    private static string StoppedWhileStarting(int exitCode, IReadOnlyList<string> tail)
    {
        var text = $"Monado stopped while starting (exit code {Number(exitCode)}).";
        var last = tail.TakeLast(LastLinesShown).ToArray();
        return last.Length == 0
            ? text
            : text + " Last lines:" + Environment.NewLine + string.Join(Environment.NewLine, last);
    }

    /// <summary>Waits for a running session's service to end by itself, then ends the session and says why.</summary>
    private async Task WatchAsync(Run run)
    {
        string message;
        try
        {
            var code = await run.Process!.Exited.ConfigureAwait(false);
            message = run.Settings.StopWhenLastAppCloses && code == 0
                ? "Monado stopped after the last app closed."
                : $"Monado stopped by itself (exit code {Number(code)}).";
        }
        catch (Exception)
        {
            message = "Monado stopped by itself.";
        }

        lock (gate)
        {
            if (current == run && state == SessionState.Running) // else a Stop got there first
            {
                _ = BeginStop(run, message);
            }
        }
    }

    /// <summary>Ends a running session (state Stopping), then moves to Stopped and raises <see cref="Ended"/>.</summary>
    private async Task FinishAsync(Run run, TaskCompletionSource done, string endedMessage)
    {
        try
        {
            RaiseChanged();
            RaiseStatusChanged();
            await EndRunAsync(run).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened above, the state moves on and every caller's wait ends.
            lock (gate)
            {
                state = SessionState.Stopped;
                current = null;
                currentInstall = null;
                stopping = null;
            }

            RaiseChanged();
            RaiseStatusChanged();
            Raise(Ended, endedMessage);
            done.SetResult();
        }
    }

    /// <summary>
    /// Ends what a run set up, in order: Monado's service (killed, then its exit awaited), its log, the module's session,
    /// <c>session.json</c>. Never throws: a failure becomes a <see cref="Warning"/>.
    /// </summary>
    private async Task EndRunAsync(Run run)
    {
        if (run.Process is { } process)
        {
            try
            {
                process.Kill();
            }
            catch (Exception e)
            {
                RaiseWarning(new SessionWarning("monado", $"Mirador couldn't end Monado ({Trimmed(e.Message)}). It ends when Mirador quits."));
            }

            await process.DisposeAsync().ConfigureAwait(false); // waits for the exit, at most a few seconds
            process.Line -= run.OnLine;
            process.ListenerFailed -= run.OnListenerFailed;
        }

        run.Log?.Dispose();

        var keepSessionFile = false;
        if (run.Session is { } session)
        {
            // The module gets a token that is cancelled at the limit, and Mirador stops waiting then either way.
            using var limit = new CancellationTokenSource(EndLimit, deps.Context.Time);
            try
            {
                await session.EndAsync(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                keepSessionFile = true; // undone through RecoverAsync before Monado next starts
                RaiseWarning(new SessionWarning(
                    "headset",
                    $"{run.Module!.DisplayName} didn't finish stopping within {Number((int)EndLimit.TotalSeconds)} s; Mirador tries again the next time it starts."));
            }
            catch (Exception e)
            {
                keepSessionFile = true;
                RaiseWarning(new SessionWarning(
                    "headset",
                    e is MiradorException
                        ? e.Message
                        : $"Mirador couldn't put things back for the {run.Module!.DisplayName} ({Trimmed(e.Message)}). It tries again the next time Mirador starts."));
            }

            session.StatusChanged -= run.OnModuleStatus;
            session.Warning -= run.OnModuleWarning;
        }

        if (run.WroteSessionFile && !keepSessionFile)
        {
            try
            {
                sessionFile.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RaiseWarning(new SessionWarning(
                    "session",
                    $"Mirador couldn't delete {deps.Context.Paths.SessionFile} ({Trimmed(e.Message)}). It tries again the next time Mirador starts."));
            }
        }

        if (run.HookStarted)
        {
            CallHook(run.Profile!, run.Module, started: false);
        }
    }

    /// <summary>Tells the hook (<paramref name="started"/>, else ended). It returns at once and never throws; if it does anyway, that is a warning.</summary>
    private void CallHook(Profile profile, IHeadsetModule? module, bool started)
    {
        if (deps.Hook is not { } hook)
        {
            return;
        }

        try
        {
            if (started)
            {
                hook.Started(profile, module);
            }
            else
            {
                hook.Ended(profile, module);
            }
        }
        catch (Exception e)
        {
            RaiseWarning(new SessionWarning("basestations", $"Mirador couldn't switch the base stations: {e.Message}"));
        }
    }

    /// <summary>Follows the module session's status and warnings until <see cref="EndRunAsync"/>.</summary>
    private void Attach(Run run, ModuleSession session)
    {
        run.OnModuleStatus = RaiseStatusChanged;
        run.OnModuleWarning = RaiseWarning;
        session.StatusChanged += run.OnModuleStatus;
        session.Warning += run.OnModuleWarning;
        lock (gate)
        {
            run.Session = session;
        }

        RaiseStatusChanged();
    }

    private void ShowProgress(Run run, string? text)
    {
        lock (gate)
        {
            run.Progress = text;
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// Follows the service's output. Failures are listened for first, so that a failure on a line replayed to the
    /// first line handler is heard too.
    /// </summary>
    private void Listen(Run run, IRunningProcess process)
    {
        run.OnListenerFailed = failure => ReportListenerFailure(run, failure);
        run.OnLine = line => OnServiceLine(run, line);
        process.ListenerFailed += run.OnListenerFailed;
        process.Line += run.OnLine;
    }

    /// <summary>
    /// One line of Monado's output: into the log, the parsers, <see cref="LogLine"/> and the module's session. A log that
    /// can't be written or a module that throws doesn't keep the line from the rest; the failures are thrown at the end,
    /// for <see cref="IRunningProcess.ListenerFailed"/>.
    /// </summary>
    private void OnServiceLine(Run run, string line)
    {
        List<Exception>? failures = null;
        try
        {
            run.Log!.Write(line);
        }
        catch (Exception e)
        {
            (failures ??= []).Add(new LogWriteException(e));
        }

        var appsChanged = run.Clients.Feed(line);
        var trackingChanged = run.Lighthouse.Feed(line) && run.UsesLighthouse;
        Raise(LogLine, line);
        if (appsChanged || trackingChanged)
        {
            RaiseStatusChanged();
        }

        try
        {
            run.Session?.OnServiceLine(line);
        }
        catch (Exception e)
        {
            (failures ??= []).Add(e);
        }

        if (failures is not null)
        {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    /// <summary>
    /// Warns once per session about each kind of failure while following the output, with the first one's message: the
    /// log that can't be written (key <c>log</c>), and the module that fails on a line (key <c>module-log</c>).
    /// </summary>
    private void ReportListenerFailure(Run run, Exception failure)
    {
        IEnumerable<Exception> each = failure is AggregateException all ? all.InnerExceptions : [failure];
        foreach (var e in each)
        {
            if (e is LogWriteException)
            {
                if (Interlocked.Exchange(ref run.LogFailureReported, 1) == 0)
                {
                    RaiseWarning(new SessionWarning("log", $"Mirador couldn't write Monado's log: {e.Message}"));
                }
            }
            else if (Interlocked.Exchange(ref run.ModuleFailureReported, 1) == 0)
            {
                RaiseWarning(new SessionWarning(
                    "module-log",
                    $"Mirador's {run.Module?.DisplayName ?? "headset"} support couldn't read Monado's log: {e.Message}"));
            }
        }
    }

    /// <summary>The <c>monado</c> line. Called under the lock.</summary>
    private StatusLine MonadoLine() => state switch
    {
        SessionState.Running => new StatusLine("monado", "Monado", StatusLevel.Ok, $"Running ({current?.Profile?.Name})"),
        SessionState.Starting => new StatusLine("monado", "Monado", StatusLevel.Info, current?.Progress ?? "Starting…"),
        SessionState.Stopping => new StatusLine("monado", "Monado", StatusLevel.Info, "Stopping…"),
        _ => new StatusLine("monado", "Monado", StatusLevel.Info, "Stopped"),
    };

    private static StatusLine AppsLine(IReadOnlyList<string> apps) =>
        apps.Count == 0
            ? new StatusLine("apps", "Apps", StatusLevel.Info, "No apps")
            : new StatusLine("apps", "Apps", StatusLevel.Ok, string.Join(", ", apps));

    private static StatusLine TrackingLine(LighthouseLogParser lighthouse)
    {
        if (lighthouse.HmdSerial is not { } hmd)
        {
            return new StatusLine("tracking", "Tracking", StatusLevel.Info, "Waiting for the headset");
        }

        return new StatusLine("tracking", "Tracking", StatusLevel.Ok, $"HMD {hmd}");
    }

    /// <summary>
    /// The first real Monado among <paramref name="running"/>: a <c>monado-service</c> whose program file isn't Mirador's
    /// stand-in, which runs under the same name while it waits for a Start (one per game). One whose file can't be read
    /// counts as a Monado (when Mirador can't tell, it doesn't start a second service) unless a fresh look shows it has
    /// ended: Windows can't read the file of a process that is gone, and a stand-in that just gave up is no Monado.
    /// </summary>
    private ProcInfo? FindService(IReadOnlyList<ProcInfo> running)
    {
        List<ProcInfo>? unreadable = null;
        foreach (var p in running.Where(p => string.Equals(p.Name, ServiceName, StringComparison.OrdinalIgnoreCase)))
        {
            if (deps.Context.Processes.ImagePathOf(p.Id) is not { } path)
            {
                (unreadable ??= []).Add(p);
            }
            else if (!isStandIn(path))
            {
                return p;
            }
        }

        if (unreadable is null)
        {
            return null;
        }

        var now = deps.Context.Processes.Snapshot();
        return unreadable.FirstOrDefault(p => now.Contains(p));
    }

    /// <summary>
    /// Which launcher starts this Start: the helper's elevated one when the user allowed it and
    /// <see cref="Elevation.ElevatedChoice.RunsAsYouReason"/> (shared with Home's line) finds no reason against it: this
    /// is the installed Mirador, the profile runs the bundled Monado, it sets no extra environment variables and its
    /// module points at no folder of its own; otherwise the job launcher, which runs Monado as the user. The elevated
    /// launcher itself runs the Start through the job launcher when the helper says SteamVR's Lighthouse driver changed
    /// since the consent (<see cref="HelperProcessLauncher"/>).
    /// Whatever the choice, the rest of the session is the same.
    /// </summary>
    private IProcessLauncher ChooseLauncher(MonadoInstall install, Profile profile, IHeadsetModule? module, MonadoSettings settings)
    {
        // install is in Mirador's admin-only folder exactly when the profile runs Mirador's (or the bundled) Monado
        // (MonadoLocator). A branch or build folder runs elevated only as its approved copy, which the helper checks.
        var approvedBuild = !install.IsBundled && profile.RunAsAdministrator && deps.ApprovedBuildCurrent?.Invoke(profile) == true;
        var eligible = deps.ElevatedLauncher is not null
            && deps.ElevatedConsented?.Invoke() == true
            && (install.IsBundled || approvedBuild)
            && Elevation.ElevatedChoice.RunsAsYouReason(profile, module, deps.ElevatedInstalledCopy?.Invoke() == true, approvedBuild) is null;

        if (!eligible)
        {
            return deps.Launcher;
        }

        var start = new ElevatedStart(
            settings.LighthouseWaitMs,
            settings.RenderScalePercent,
            settings.CompositorMinTimeMs,
            settings.StopWhenLastAppCloses,
            module?.ElevatedModuleId ?? "",
            approvedBuild ? profile.Id : "");
        return deps.ElevatedLauncher!(start);
    }

    private static StartOutcome Fail(string message) => new(false, message);

    /// <summary>The module with <paramref name="id"/>, or the one that had it before (<see cref="IHeadsetModule.FormerIds"/>).</summary>
    private IHeadsetModule? FindModule(string? id) =>
        id is null ? null
            : deps.Modules.TryGetValue(id, out var module) ? module
            : deps.Modules.Values.FirstOrDefault(m => m.FormerIds.Contains(id, StringComparer.Ordinal));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Trimmed(string message) => message.Trim().TrimEnd('.');

    private void RaiseChanged() => Raise(Changed);

    private void RaiseStatusChanged() => Raise(StatusChanged);

    private void RaiseWarning(SessionWarning warning) => Raise(Warning, warning);

    private static void Raise(Action? handlers)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // See the class remarks: a subscriber can't be allowed to stop a Start or a Stop half-way.
            }
        }
    }

    private static void Raise<T>(Action<T>? handlers, T value)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<T>)handler)(value);
            }
            catch (Exception)
            {
                // See the class remarks.
            }
        }
    }

    /// <summary>Hands progress on as it is reported (<see cref="Progress{T}"/> would post it to a synchronization context).</summary>
    private sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>The log file couldn't take a line.</summary>
    private sealed class LogWriteException(Exception inner) : Exception(inner.Message, inner);

    /// <summary>
    /// Writes the recovery data a module saves while it gets ready (<see cref="IHeadsetModule.BeforeStartAsync"/>), and
    /// nothing once that has ended: a late call must not bring back a <c>session.json</c> the session has deleted.
    /// </summary>
    private sealed class RecoveryWriter(Run run, Action<IReadOnlyDictionary<string, string>> write)
    {
        private readonly object gate = new();
        private bool closed;
        private bool somethingToUndo;

        /// <summary>Whether the last data saved wasn't empty: the module's steps may still need undoing.</summary>
        public bool LeftSomethingToUndo
        {
            get
            {
                lock (gate)
                {
                    return somethingToUndo;
                }
            }
        }

        public void Save(IReadOnlyDictionary<string, string> data)
        {
            lock (gate)
            {
                if (closed)
                {
                    return;
                }

                write(data);
                run.WroteSessionFile = true;
                somethingToUndo = data.Count > 0;
            }
        }

        public void Close()
        {
            lock (gate)
            {
                closed = true;
            }
        }
    }

    /// <summary>One Start and, if it succeeds, the session that follows it.</summary>
    private sealed class Run
    {
        /// <summary>A game asked for this Start (through its stand-in), whether or not the stand-in is known.</summary>
        public bool ForGame { get; init; }

        /// <summary>The stand-in of the game that asked for this Start; null when someone else did.</summary>
        public string? GameStandIn { get; init; }

        // Shown in the status: written and read under the controller's lock.
        public Profile? Profile { get; set; }

        public IHeadsetModule? Module { get; set; }

        public bool UsesLighthouse { get; set; }

        /// <summary>The hook was told this session started, so it is told it ended.</summary>
        public bool HookStarted { get; set; }

        public string? Progress { get; set; }

        public StatusLine? StandIn { get; set; }

        public ModuleSession? Session { get; set; }

        // Set while starting and read when the session ends, which comes after.
        public MonadoSettings Settings { get; set; } = new();

        public bool WroteSessionFile { get; set; }

        public TextLog? Log { get; set; }

        public IRunningProcess? Process { get; set; }

        public Action<string>? OnLine { get; set; }

        public Action<Exception>? OnListenerFailed { get; set; }

        public Action? OnModuleStatus { get; set; }

        public Action<SessionWarning>? OnModuleWarning { get; set; }

        // Safe to read from any thread; fed only from the output's one line at a time.
        public ClientLogParser Clients { get; } = new();

        public LighthouseLogParser Lighthouse { get; } = new();

        // 1 once that kind of failure while following the output has been reported (Interlocked: two output streams).
        public int LogFailureReported;

        public int ModuleFailureReported;
    }
}
