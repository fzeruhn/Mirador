using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.BaseStations;

// The queue of commands with its one worker, the listening a command does before and after it, the session's off, the
// session's hook, and Mirador's own start and quit.
public sealed partial class StationManager
{
    private const string Quitting = "Mirador is quitting";

    // Under _lock.
    private readonly List<Work> _queue = [];
    private bool _working;
    private TaskCompletionSource _idle = Idle();
    private Work? _current;
    private StationCommand? _running;
    private bool _commandRunning;
    private bool _settling;
    private long _settleSince;
    private ITimer? _settleTimer;
    private ITimer? _offTimer;
    private object? _offToken;

    // Under _lock: Windows' ending queued this session's off (EndingNow), so its Ended sends none and quitting sends no
    // second one; until a session starts again.
    private bool _offSentAtEnding;

    // Under _lock: Mirador is quitting (QuittingNow or FlushAsync ran), so Mirador's start sends no On any more.
    private bool _quitting;

    // Under _lock: the quit's off is decided: queued, or the session's off taken for it. Decided once; a session's end
    // after it sends no second off.
    private bool _quitOff;

    private enum WorkKind
    {
        Switch,
        Identify,
    }

    /// <summary>
    /// Whether the off that follows a session is still to come: waiting its <see cref="OffDelay"/>, queued, or being
    /// sent. An Off the user asked for doesn't count, nor Mirador's own as it quits (<see cref="OffAtQuitWanted"/>).
    /// </summary>
    public bool OffPending
    {
        get
        {
            lock (_lock)
            {
                return _offToken is not null || SessionOffOutLocked();
            }
        }
    }

    /// <summary>
    /// Whether quitting switches the base stations off (<see cref="QuittingNow"/>, <see cref="FlushAsync"/>): "Power off
    /// when Mirador quits" is on and a station is selected.
    /// </summary>
    public bool OffAtQuitWanted
    {
        get
        {
            if (!_settings().BaseStationsOffAtMiradorQuit)
            {
                return false;
            }

            EnsureLoaded();
            lock (_lock)
            {
                return AnySelectedLocked();
            }
        }
    }

    /// <summary>Whether the end of a session of this profile switches the base stations off: the profile has them, and "Power off when Monado stops" is on.</summary>
    public bool OffFollows(Profile profile, IHeadsetModule? module) =>
        profile.UsesBaseStations(module?.UsesSteamVrLighthouse ?? false) && _settings().BaseStationsOffAtStop;

    /// <summary>
    /// For Mirador's start, whatever started it (its window, the tray at login, a game's stand-in): a scan, as
    /// <see cref="ScanAsync"/>, then with "Power on when Mirador starts" an On to the selected stations, queued like the
    /// user's own (<see cref="SwitchSelectedAsync"/>); it completes without waiting for that On. No On once Mirador is
    /// quitting (<see cref="QuittingNow"/>): the queue refuses it. Never throws, also when cancelled through
    /// <paramref name="ct"/>, which cancels the On too.
    /// </summary>
    public async Task StartupAsync(CancellationToken ct)
    {
        try
        {
            // "Constant" tracking listens from Mirador's start on.
            SettingsChanged();
            await ScanAsync(ct).ConfigureAwait(false);
            if (!_settings().BaseStationsOnAtMiradorStart)
            {
                return;
            }

            Log("Base stations: switching on as Mirador starts");
            _ = Ask(new Work(WorkKind.Switch, StationCommand.On, null, ct, fromStartup: true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Mirador's start was called off; nothing to switch.
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't follow its own start ({Reason(e)})");
        }
    }

    /// <summary>
    /// Switches every selected station, after the commands asked for before it. It drops the session's off (the delayed
    /// one, or one still queued) and an Off for the selected stations that the user asked for and is still queued; one
    /// already sending goes on, and so does Mirador's own as it quits. Completes when the command has been sent, dropped
    /// or stopped (it never fails: a station
    /// that doesn't answer gets its <see cref="StationView.Problem"/>); cancelled only through <paramref name="ct"/>,
    /// which also stops it while it runs.
    /// </summary>
    public Task SwitchSelectedAsync(StationCommand command, CancellationToken ct) =>
        Ask(new Work(WorkKind.Switch, command, null, ct));

    /// <summary>
    /// Switches one station, selected or not (the user picked it), as <see cref="SwitchSelectedAsync"/> does; the Off of
    /// the user's it drops is one still queued for this station.
    /// </summary>
    public Task SwitchAsync(string address, StationCommand command, CancellationToken ct) =>
        Ask(new Work(WorkKind.Switch, command, Normalize(address) ?? address, ct));

    /// <summary>
    /// Makes one station blink, selected or not (the user picked it): only a 2.0 station that is on, else nothing is
    /// sent. It waits its turn like a command but drops nothing.
    /// </summary>
    public Task IdentifyAsync(string address, CancellationToken ct) =>
        Ask(new Work(WorkKind.Identify, StationCommand.On, Normalize(address) ?? address, ct));

    /// <inheritdoc />
    /// <remarks>
    /// For a profile with base stations: drops the session's off, delayed or queued (one already sending goes on), and
    /// the Ons still to be sent again, then, with "Power on when Monado starts", queues On.
    /// </remarks>
    public void Started(Profile profile, IHeadsetModule? module)
    {
        try
        {
            lock (_lock)
            {
                // A new session: Windows' ending, if it began, was cancelled.
                _offSentAtEnding = false;
            }

            if (!profile.UsesBaseStations(module?.UsesSteamVrLighthouse ?? false))
            {
                return;
            }

            var on = _settings().BaseStationsOnAtStart;
            List<Work> dropped;
            bool delayed, start = false;
            lock (_lock)
            {
                // Not the session's off that is the quit's too.
                dropped = TakeQueuedLocked(w => w.IsSessionOff && !w.AtQuit);
                dropped.AddRange(CancelWakesLocked());
                delayed = CancelDelayedOffLocked();
                if (on)
                {
                    start = EnqueueLocked(new Work(WorkKind.Switch, StationCommand.On, null, CancellationToken.None, fromSession: true));
                }
            }

            Dropped(dropped, delayed, "Monado started again");
            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't follow Monado's start ({Reason(e)})");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// For a profile with base stations and with "Power off when Monado stops": queues <see cref="OffCommand"/> after
    /// <see cref="OffDelay"/>, replacing an off still waiting; nothing when <see cref="EndingNow"/> sent this session's
    /// off already, or Mirador's quit sent its own (<see cref="QuittingNow"/>).
    /// </remarks>
    public void Ended(Profile profile, IHeadsetModule? module)
    {
        try
        {
            if (!OffFollows(profile, module))
            {
                return;
            }

            bool ending, quit;
            lock (_lock)
            {
                // Left set until a session starts: quitting after it sends no second off either.
                ending = _offSentAtEnding;
                quit = _quitOff;
                if (!ending && !quit)
                {
                    CancelDelayedOffLocked();
                    var token = new object();
                    _offToken = token;
                    _offTimer = _time.CreateTimer(OffDue, token, OffDelay, Timeout.InfiniteTimeSpan);
                }
            }

            if (ending)
            {
                Log("Base stations: the off after Monado stopped went out already (Windows is ending the session)");
            }
            else if (quit)
            {
                Log("Base stations: the off after Monado stopped isn't sent (Mirador's off as it quits went out already)");
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't follow Monado's stop ({Reason(e)})");
        }
    }

    /// <summary>
    /// For Windows ending the session (shutting down, restarting, signing out) while a Monado session hasn't ended: for
    /// a profile with base stations and with "Power off when Monado stops", queues the session's off at once, without
    /// waiting for the session's end or <see cref="OffDelay"/>, unless one is queued or being sent already, or Mirador's
    /// quit sent its own. It drops an off still waiting and the Ons still to be sent again, and the session's own
    /// <see cref="Ended"/> after it sends nothing. <see cref="QuittingNow"/> after it stops an On being sent, and
    /// <see cref="FlushAsync"/> waits for the off. Returns at once and never throws.
    /// </summary>
    public void EndingNow(Profile profile, IHeadsetModule? module)
    {
        try
        {
            if (!OffFollows(profile, module))
            {
                return;
            }

            var command = OffCommand();
            List<Work> dropped;
            var start = false;
            lock (_lock)
            {
                dropped = CancelWakesLocked();
                CancelDelayedOffLocked();
                _offSentAtEnding = true;
                if (!SessionOffOutLocked() && !_quitOff)
                {
                    start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
                }
            }

            Dropped(dropped, false, "Windows is ending the session");
            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't switch the base stations off as Windows ends the session ({Reason(e)})");
        }
    }

    /// <summary>
    /// For Mirador's quit, before Monado is stopped (a headset's end can take long, and the quit's off is sent at once):
    /// drops the queued Ons and those still to be sent again, stops one that is running (what it has sent stays sent),
    /// and queues the delayed off at once. With "Power off when Mirador quits" (<see cref="OffAtQuitWanted"/>) it also
    /// queues <see cref="OffCommand"/> for the selected stations at once, unless the session's off is queued, being sent,
    /// or went out as Windows ends the session (<see cref="EndingNow"/>), which is then the quit's off too: one off, never
    /// two. That is decided once; nothing drops the quit's off, and a session's end after it sends no second one. From
    /// now on Mirador's start sends no On. Returns at once and never throws.
    /// </summary>
    public void QuittingNow()
    {
        try
        {
            _ = Quit();
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't switch the base stations for its quit ({Reason(e)})");
        }
    }

    /// <summary>
    /// For quitting, after Monado was stopped: does what <see cref="QuittingNow"/> does (again, for what came since:
    /// the delayed off of a session that ended meanwhile is queued at once), and returns when the queue is empty or after
    /// <paramref name="limit"/>, whichever comes first. Never throws.
    /// </summary>
    public async Task FlushAsync(TimeSpan limit)
    {
        try
        {
            await Quit().WaitAsync(limit, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log($"Base stations: Mirador stopped waiting for the base stations after {limit.TotalSeconds:0} s");
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't finish with the base stations ({Reason(e)})");
        }
    }

    /// <summary>The quit's step (<see cref="QuittingNow"/>); returns what completes once the queue is empty.</summary>
    private Task Quit()
    {
        var command = OffCommand();
        var quitOff = _settings().BaseStationsOffAtMiradorQuit;
        EnsureLoaded();
        List<Work> dropped;
        Work? stopping;
        var start = false;
        string? quit = null;
        Task idle;
        lock (_lock)
        {
            _quitting = true;
            dropped = CancelWakesLocked();
            dropped.AddRange(TakeQueuedLocked(w => w.IsOn));
            stopping = _current is { IsOn: true } running ? running : null;
            if (CancelDelayedOffLocked())
            {
                start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
            }

            if (quitOff && !_quitOff && AnySelectedLocked())
            {
                _quitOff = true;
                if (_offSentAtEnding || SessionOffOutLocked())
                {
                    // The session's off is the quit's too: nothing drops it any more.
                    foreach (var work in _queue.Where(w => w.IsSessionOff))
                    {
                        work.AtQuit = true;
                    }

                    if (_current is { IsSessionOff: true } sending)
                    {
                        sending.AtQuit = true;
                    }

                    quit = "Base stations: the off after Monado stopped is the one as Mirador quits";
                }
                else
                {
                    start |= EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, atQuit: true));
                    quit = "Base stations: switching off as Mirador quits";
                }
            }

            idle = _idle.Task;
        }

        Dropped(dropped, false, Quitting);
        if (quit is not null)
        {
            Log(quit);
        }

        // On another thread: the stopped On's worker may go on right where it is stopped.
        if (stopping is not null)
        {
            _ = Task.Run(stopping.Stop);
        }

        if (start)
        {
            StartWorker();
        }

        return idle;
    }

    private static TaskCompletionSource Idle()
    {
        var idle = NewSignal();
        idle.SetResult();
        return idle;
    }

    private static string Name(Work work) => work.Kind == WorkKind.Identify ? "Identify" : work.Command.ToString();

    private static string Unusable(BluetoothState state) =>
        state == BluetoothState.Missing ? "this PC has no Bluetooth" : "Bluetooth is off";

    /// <summary>
    /// The station is one the work is for: the one asked for, or a selected one (an On sent again after one for the
    /// selected stations: still selected).
    /// </summary>
    private static bool Wanted(Entry entry, Work work) =>
        entry.Station.Selected || (work.Address is not null && work.ResendOf is not { ForSelected: true });

    /// <summary>A command or Identify from the user.</summary>
    private Task Ask(Work work)
    {
        if (work.Ct.IsCancellationRequested)
        {
            work.Release();
            return Task.FromCanceled(work.Ct);
        }

        // Registered before it is queued: a cancellation that comes first finds nothing to take out, and the worker
        // then sees the token.
        work.Registration = work.Ct.Register(() => Withdraw(work));
        List<Work> dropped = [];
        bool delayed = false, start = false, refused;
        lock (_lock)
        {
            // Checked in the same step as the queueing: a quit that comes first always wins.
            refused = work.FromStartup && _quitting;
            if (!refused)
            {
                if (work.Kind == WorkKind.Switch)
                {
                    // The session's off and the Ons still to be sent again go for any command; the user's own Off only
                    // for one for the same stations; the quit's off never.
                    dropped = TakeQueuedLocked(w => !w.AtQuit && (w.IsSessionOff || (w.IsOff && !w.FromSession && w.Address == work.Address)));
                    dropped.AddRange(CancelWakesLocked());
                    delayed = CancelDelayedOffLocked();
                }

                start = EnqueueLocked(work);
            }
        }

        if (refused)
        {
            work.Release();
            work.Done.TrySetResult();
            Log($"Base stations: {Name(work)} not sent ({Quitting})");
            return work.Done.Task;
        }

        Dropped(dropped, delayed, "a later command replaced it");
        if (start)
        {
            StartWorker();
        }

        return work.Done.Task;
    }

    /// <summary>The caller cancelled: a queued work is taken out; one running stops through its token.</summary>
    private void Withdraw(Work work)
    {
        bool removed;
        lock (_lock)
        {
            removed = _queue.Remove(work);
        }

        if (removed)
        {
            work.Release();
            work.Done.TrySetCanceled(work.Ct);
            Log($"Base stations: {Name(work)} cancelled");
        }
    }

    /// <summary>Under the lock: whether a known station is selected.</summary>
    private bool AnySelectedLocked() => _stations?.Exists(e => e.Station.Selected) ?? false;

    /// <summary>Under the lock: whether the session's off is queued or being sent.</summary>
    private bool SessionOffOutLocked() => _queue.Exists(w => w.IsSessionOff) || _current is { IsSessionOff: true };

    /// <summary>Under the lock: takes the queued (not started) work that <paramref name="match"/> picks out of the queue.</summary>
    private List<Work> TakeQueuedLocked(Predicate<Work> match)
    {
        var taken = _queue.FindAll(match);
        _queue.RemoveAll(match);
        return taken;
    }

    /// <summary>Under the lock: cancels the delayed off; false when none was waiting.</summary>
    private bool CancelDelayedOffLocked()
    {
        if (_offToken is null)
        {
            return false;
        }

        _offTimer?.Dispose();
        _offTimer = null;
        _offToken = null;
        return true;
    }

    /// <summary>Under the lock: queues the work; true when the worker has to be started.</summary>
    private bool EnqueueLocked(Work work)
    {
        work.WakeEpoch = _wakeEpoch;
        _queue.Add(work);
        if (_working)
        {
            return false;
        }

        _working = true;
        _idle = NewSignal();
        return true;
    }

    private void Dropped(List<Work> dropped, bool delayedOff, string why)
    {
        foreach (var work in dropped)
        {
            work.Release();
            work.Done.TrySetResult();
            Log(work.ResendOf is null
                ? $"Base stations: {Name(work)} not sent ({why})"
                : $"Base stations: {Name(work)} {work.Address}: not sent again ({why})");
        }

        if (delayedOff)
        {
            Log($"Base stations: the off after Monado stopped isn't sent ({why})");
        }
    }

    /// <summary>The delayed off's timer: queues the session's off, unless it was cancelled meanwhile.</summary>
    private void OffDue(object? token)
    {
        try
        {
            var command = OffCommand();
            List<Work> dropped;
            bool start;
            lock (_lock)
            {
                if (token is null || !ReferenceEquals(token, _offToken))
                {
                    return;
                }

                CancelDelayedOffLocked();
                dropped = CancelWakesLocked();
                start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
            }

            Dropped(dropped, false, "the off after Monado stopped replaced it");
            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: the off after Monado stopped couldn't be queued ({Reason(e)})");
        }
    }

    private void StartWorker() => _ = Task.Run(WorkAsync);

    /// <summary>The one worker: runs the queue in order until it is empty, then marks the manager idle.</summary>
    private async Task WorkAsync()
    {
        while (true)
        {
            Work? work = null;
            TaskCompletionSource? idle = null;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _working = false;
                    idle = _idle;
                }
                else
                {
                    work = _queue[0];
                    _queue.RemoveAt(0);
                    _current = work;
                }
            }

            if (work is null)
            {
                idle!.TrySetResult();
                return;
            }

            var cancelled = false;
            try
            {
                work.Token.ThrowIfCancellationRequested();
                await RunAsync(work).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (work.Ct.IsCancellationRequested)
            {
                Log($"Base stations: {Name(work)} cancelled");
                cancelled = true;
            }
            catch (OperationCanceledException) when (work.Stopped)
            {
                Log($"Base stations: {Name(work)} stopped ({Quitting})");
            }
            catch (Exception e)
            {
                Log($"Base stations: {Name(work)} failed ({Reason(e)})");
            }
            finally
            {
                lock (_lock)
                {
                    _current = null;

                    // An On sent again that didn't reach the station: Mirador stops watching it.
                    if (work.ResendOf is { } wake && ReferenceEquals(wake.Resend, work))
                    {
                        _wakes.Remove(wake);
                    }
                }

                work.Release();
            }

            if (cancelled)
            {
                work.Done.TrySetCanceled(work.Ct);
            }
            else
            {
                work.Done.TrySetResult();
            }
        }
    }

    private async Task RunAsync(Work work)
    {
        var name = Name(work);
        var bluetooth = _radio.State;
        if (bluetooth != BluetoothState.Ready)
        {
            Log($"Base stations: {name} not sent ({Unusable(bluetooth)})");
            return;
        }

        EnsureLoaded();
        var targets = new List<string>();
        var notSent = new List<string>();
        lock (_lock)
        {
            var candidates = work.Address is null
                ? Ordered().Where(e => e.Station.Selected).ToList()
                : Lookup(work.Address) is { } one ? [one] : new List<Entry>();
            foreach (var entry in candidates)
            {
                var line = Line(work, entry.Station.DisplayName, entry.Station.Address);
                if (!Wanted(entry, work))
                {
                    // An On sent again after one for the selected stations, to a station deselected since.
                    notSent.Add($"{line}: not sent (it is no longer selected)");
                }
                else if (work.Kind == WorkKind.Identify && StationProtocol.VersionOf(entry.Station.Name) != StationVersion.V2)
                {
                    notSent.Add($"{line}: not sent (1.0 base stations can't blink)");
                }
                else if (work.Kind == WorkKind.Identify && entry.Power != StationPower.On)
                {
                    notSent.Add($"{line}: not sent (it isn't on)");
                }
                else
                {
                    targets.Add(entry.Station.Address);
                }
            }
        }

        foreach (var line in notSent)
        {
            Log(line);
        }

        if (targets.Count == 0)
        {
            if (notSent.Count == 0)
            {
                Log(work.Address is null
                    ? $"Base stations: {name} not sent (no base station is selected)"
                    : $"Base stations: {name} not sent (Mirador doesn't list {work.Address})");
            }

            return;
        }

        lock (_lock)
        {
            _running = work.Kind == WorkKind.Switch ? work.Command : null;
            _commandRunning = true;
        }

        UpdateWatch();
        Raise();
        try
        {
            await FindAsync(targets, work).ConfigureAwait(false);
            await TryAllAsync(targets, work).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _running = null;
                _commandRunning = false;
                _settling = true;
                _settleSince = _time.GetTimestamp();
                _settleTimer ??= _time.CreateTimer(_ => Settled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _settleTimer.Change(SettleTime, Timeout.InfiniteTimeSpan);
            }

            UpdateWatch();
            Raise();
        }
    }

    /// <summary>Listens until every station of the command has been heard since Mirador started, at most <see cref="FindTime"/>.</summary>
    private async Task FindAsync(List<string> targets, Work work)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            Task heard;
            lock (_lock)
            {
                if (targets.All(a => Lookup(a) is not { } e || !Wanted(e, work) || e.RandomAddress is not null))
                {
                    return;
                }

                heard = _newlyHeard.Task;
            }

            var left = FindTime - _time.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await heard.WaitAsync(left, _time, work.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The settle timer: stops listening once <see cref="SettleTime"/> has passed since the last command and no station
    /// an On reached is watched any more.
    /// </summary>
    private void Settled()
    {
        try
        {
            lock (_lock)
            {
                var now = _time.GetTimestamp();
                var left = SettleTime - _time.GetElapsedTime(_settleSince, now);
                var wakes = WakesLeftLocked(now);
                if (wakes > left)
                {
                    left = wakes;
                }

                if (left > TimeSpan.Zero)
                {
                    _settleTimer?.Change(left, Timeout.InfiniteTimeSpan);
                    return;
                }

                _settling = false;
            }

            Follow();
        }
        catch (Exception e)
        {
            Log($"Base stations: {Reason(e)}");
        }
    }

    /// <summary>A command, or Identify, waiting in the queue or running.</summary>
    private sealed class Work(
        WorkKind kind,
        StationCommand command,
        string? address,
        CancellationToken ct,
        bool fromSession = false,
        Wake? resendOf = null,
        bool atQuit = false,
        bool fromStartup = false)
    {
        private readonly Stopper _stopper = new(ct);
        private volatile bool _stopped;

        public WorkKind Kind { get; } = kind;

        public StationCommand Command { get; } = command;

        /// <summary>The one station asked for, or null for the selected ones.</summary>
        public string? Address { get; } = address;

        /// <summary>The caller's token.</summary>
        public CancellationToken Ct { get; } = ct;

        /// <summary>Cancelled by the caller's token or by <see cref="Stop"/>: every wait of the work uses it.</summary>
        public CancellationToken Token => _stopper.Token;

        /// <summary>Queued by the session (its On or its off), not asked for by the user.</summary>
        public bool FromSession { get; } = fromSession;

        /// <summary>
        /// The quit's off ("Power off when Mirador quits"): Mirador's own, or the session's off taken for it (set under
        /// the lock then). Never dropped.
        /// </summary>
        public bool AtQuit { get; set; } = atQuit;

        /// <summary>The On of Mirador's start ("Power on when Mirador starts"): refused once Mirador is quitting.</summary>
        public bool FromStartup { get; } = fromStartup;

        /// <summary>For an On sent again to one station that is stuck waking up: its watch; null otherwise.</summary>
        public Wake? ResendOf { get; } = resendOf;

        /// <summary>The watches' cancellation count when it was queued (set under the lock): see <see cref="WokenLocked"/>.</summary>
        public long WakeEpoch { get; set; }

        public TaskCompletionSource Done { get; } = NewSignal();

        public CancellationTokenRegistration Registration { get; set; }

        /// <summary>Whether <see cref="Stop"/> was called.</summary>
        public bool Stopped => _stopped;

        public bool IsOn => Kind == WorkKind.Switch && Command == StationCommand.On;

        public bool IsOff => Kind == WorkKind.Switch && Command != StationCommand.On;

        public bool IsSessionOff => FromSession && IsOff;

        /// <summary>Ends the running work at its next wait; what it has sent stays sent.</summary>
        public void Stop()
        {
            _stopped = true;
            _stopper.Cancel();
        }

        /// <summary>Once the work is done or dropped.</summary>
        public void Release()
        {
            Registration.Unregister();
            _stopper.Dispose();
        }
    }
}
