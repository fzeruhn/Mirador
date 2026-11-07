using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.BaseStations;

// The queue of commands with its one worker, the listening a command does before and after it, the session's off and
// the session's hook.
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

    private enum WorkKind
    {
        Switch,
        Identify,
    }

    /// <summary>
    /// Whether the off that follows a session is still to come: waiting its <see cref="OffDelay"/>, queued, or being
    /// sent. An Off the user asked for doesn't count.
    /// </summary>
    public bool OffPending
    {
        get
        {
            lock (_lock)
            {
                return _offToken is not null || _queue.Exists(w => w.IsSessionOff) || _current is { IsSessionOff: true };
            }
        }
    }

    /// <summary>
    /// Switches every selected station, after the commands asked for before it. It drops the session's off (the delayed
    /// one, or one still queued) and an Off for the selected stations that the user asked for and is still queued; one
    /// already sending goes on. Completes when the command has been sent, dropped or stopped (it never fails: a station
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
    /// For a profile with base stations: drops the session's off, delayed or queued (one already sending goes on), then,
    /// with "Switch on when Monado starts", queues On.
    /// </remarks>
    public void Started(Profile profile, IHeadsetModule? module)
    {
        try
        {
            if (!profile.UsesBaseStations(module?.UsesSteamVrLighthouse ?? false))
            {
                return;
            }

            var on = _settings().BaseStationsOnAtStart;
            List<Work> dropped;
            bool delayed, start = false;
            lock (_lock)
            {
                dropped = TakeQueuedLocked(w => w.IsSessionOff);
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
    /// For a profile with base stations and with "Switch off when Monado stops": queues <see cref="OffCommand"/> after
    /// <see cref="OffDelay"/>, replacing an off still waiting.
    /// </remarks>
    public void Ended(Profile profile, IHeadsetModule? module)
    {
        try
        {
            if (!profile.UsesBaseStations(module?.UsesSteamVrLighthouse ?? false) || !_settings().BaseStationsOffAtStop)
            {
                return;
            }

            lock (_lock)
            {
                CancelDelayedOffLocked();
                var token = new object();
                _offToken = token;
                _offTimer = _time.CreateTimer(OffDue, token, OffDelay, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: Mirador couldn't follow Monado's stop ({Reason(e)})");
        }
    }

    /// <summary>
    /// For quitting: drops the queued Ons and stops one that is running (what it has sent stays sent), queues the
    /// delayed off at once, and returns when the queue is empty or after <paramref name="limit"/>, whichever comes
    /// first. Never throws.
    /// </summary>
    public async Task FlushAsync(TimeSpan limit)
    {
        try
        {
            var command = OffCommand();
            List<Work> dropped;
            Work? stopping;
            var start = false;
            Task idle;
            lock (_lock)
            {
                dropped = TakeQueuedLocked(w => w.IsOn);
                stopping = _current is { IsOn: true } running ? running : null;
                if (CancelDelayedOffLocked())
                {
                    start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
                }

                idle = _idle.Task;
            }

            Dropped(dropped, false, Quitting);

            // On another thread: the stopped On's worker may go on right where it is stopped.
            if (stopping is not null)
            {
                _ = Task.Run(stopping.Stop);
            }

            if (start)
            {
                StartWorker();
            }

            await idle.WaitAsync(limit, _time).ConfigureAwait(false);
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

    private static TaskCompletionSource Idle()
    {
        var idle = NewSignal();
        idle.SetResult();
        return idle;
    }

    private static string Name(Work work) => work.Kind == WorkKind.Identify ? "Identify" : work.Command.ToString();

    private static string Unusable(BluetoothState state) =>
        state == BluetoothState.Missing ? "this PC has no Bluetooth" : "Bluetooth is off";

    /// <summary>The station is one the work is for: the one asked for, or a selected one.</summary>
    private static bool Wanted(Entry entry, Work work) => work.Address is not null || entry.Station.Selected;

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
        bool delayed = false, start;
        lock (_lock)
        {
            if (work.Kind == WorkKind.Switch)
            {
                // The session's off goes for any command; the user's own Off only for one for the same stations.
                dropped = TakeQueuedLocked(w => w.IsSessionOff || (w.IsOff && !w.FromSession && w.Address == work.Address));
                delayed = CancelDelayedOffLocked();
            }

            start = EnqueueLocked(work);
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
            Log($"Base stations: {Name(work)} not sent ({why})");
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
            bool start;
            lock (_lock)
            {
                if (token is null || !ReferenceEquals(token, _offToken))
                {
                    return;
                }

                CancelDelayedOffLocked();
                start = EnqueueLocked(new Work(WorkKind.Switch, command, null, CancellationToken.None, fromSession: true));
            }

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
                if (work.Kind == WorkKind.Identify && StationProtocol.VersionOf(entry.Station.Name) != StationVersion.V2)
                {
                    notSent.Add($"Base stations: {name} {entry.Station.DisplayName} ({entry.Station.Address}): not sent (1.0 base stations can't blink)");
                }
                else if (work.Kind == WorkKind.Identify && entry.Power != StationPower.On)
                {
                    notSent.Add($"Base stations: {name} {entry.Station.DisplayName} ({entry.Station.Address}): not sent (it isn't on)");
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

    /// <summary>The settle timer: stops listening once <see cref="SettleTime"/> has passed since the last command.</summary>
    private void Settled()
    {
        try
        {
            lock (_lock)
            {
                var left = SettleTime - _time.GetElapsedTime(_settleSince);
                if (left > TimeSpan.Zero)
                {
                    _settleTimer?.Change(left, Timeout.InfiniteTimeSpan);
                    return;
                }

                _settling = false;
            }

            UpdateWatch();
        }
        catch (Exception e)
        {
            Log($"Base stations: {Reason(e)}");
        }
    }

    /// <summary>A command, or Identify, waiting in the queue or running.</summary>
    private sealed class Work(WorkKind kind, StationCommand command, string? address, CancellationToken ct, bool fromSession = false)
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
