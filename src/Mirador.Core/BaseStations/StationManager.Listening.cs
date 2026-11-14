namespace Mirador.Core.BaseStations;

// Listening to the stations' adverts: who is listening (a scan, a command, "Constant" tracking), what each
// advert says, and the timer for Home's "waking up".
public sealed partial class StationManager
{
    // Under _lock.
    private int _listens;
    private bool _live;
    private int _scans;
    private long _adverts;
    private bool _watching;
    private ITimer? _tickTimer;
    private ITimer? _followTimer;
    private TaskCompletionSource _newlyHeard = NewSignal();

    // Under _watchGate.
    private IDisposable? _watch;

    /// <summary>Whether a scan (<see cref="ScanAsync"/>) is running and Mirador is listening for it.</summary>
    public bool Looking
    {
        get
        {
            lock (_lock)
            {
                return _scans > 0 && _watching;
            }
        }
    }

    /// <summary>
    /// For a page on screen: reads "Station state tracking rate" (<see cref="AppSettings.BaseStationsState"/>) again. With
    /// "Constant" (<see cref="StationStateMode.Live"/>) Mirador listens to the stations' adverts all the time it runs,
    /// whether or not a page holds a listen; with "When Relevant" only for a scan and around a command. Kept for the pages,
    /// which hold one while shown. Listening is passive: nothing is sent and nothing connects. It isn't a scan:
    /// <see cref="Looking"/> stays as it is, and so does what is <see cref="StationView.Seen"/>.
    /// </summary>
    public IDisposable Listen()
    {
        lock (_liveGate)
        {
            var live = _settings().BaseStationsState == StationStateMode.Live;
            lock (_lock)
            {
                _listens++;
                _live = live;
            }
        }

        UpdateWatch();
        return new Listening(this);
    }

    /// <summary>
    /// Called at Mirador's start and by the pages after saving a base-station setting: "Station state tracking rate" is
    /// read again, so that Mirador starts or stops listening all the time ("Constant").
    /// </summary>
    public void SettingsChanged()
    {
        // Read and kept in one step: an older read never overwrites a newer one.
        lock (_liveGate)
        {
            var live = _settings().BaseStationsState == StationStateMode.Live;
            lock (_lock)
            {
                _live = live;
            }
        }

        UpdateWatch();
    }

    /// <summary>
    /// Scans: listens to the stations' adverts for <see cref="ScanTime"/>, whatever "Station state" says, with
    /// <see cref="Looking"/> true meanwhile. Each station heard is added (unselected, when new) and its state read; when
    /// the scan is over, a station not heard since it began is no longer <see cref="StationView.Seen"/>. A scan that
    /// couldn't listen (Bluetooth off or missing at its start or end, or Windows refused to listen) says so in the log
    /// and leaves what is seen as it was. Passive: nothing is sent and nothing connects. Cancelled only through
    /// <paramref name="ct"/>, which ends it early and leaves what is seen as it was; it never fails otherwise.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureLoaded();
        long since;
        lock (_lock)
        {
            _scans++;
            since = _adverts;
        }

        UpdateWatch();
        Raise();
        var missed = CantListen();
        var done = false;
        try
        {
            await Task.Delay(ScanTime, _time, ct).ConfigureAwait(false);
            done = true;
        }
        finally
        {
            if (done)
            {
                missed ??= CantListen();
            }

            int heard = 0, count = 0;
            lock (_lock)
            {
                _scans--;
                if (done && missed is null)
                {
                    foreach (var entry in _stations ?? [])
                    {
                        count++;
                        if (entry.LastAdvert > since)
                        {
                            heard++;
                        }
                        else
                        {
                            entry.Seen = false;
                        }
                    }
                }
            }

            if (done)
            {
                Log(missed is null
                    ? $"Base stations: scanned for {ScanTime.TotalSeconds:0} s, heard {heard} of {count}"
                    : $"Base stations: couldn't scan ({missed})");
            }

            Follow();
            Raise();
        }
    }

    /// <summary>Why a scan can't hear the stations now, in plain words; null when Bluetooth is ready and Mirador listens.</summary>
    private string? CantListen()
    {
        var bluetooth = _radio.State;
        if (bluetooth != BluetoothState.Ready)
        {
            return Unusable(bluetooth);
        }

        lock (_lock)
        {
            return _watching ? null : "Mirador couldn't listen for the base stations";
        }
    }

    /// <summary>One advert, on a Bluetooth thread.</summary>
    private void Heard(Advert advert)
    {
        try
        {
            if (StationProtocol.VersionOf(advert.Name) is not { } version)
            {
                return;
            }

            var address = StationProtocol.FormatAddress(advert.Address);
            EnsureLoaded();
            bool save = false, changed = false, stateChanged = false;
            TaskCompletionSource? newlyHeard = null;
            lock (_lock)
            {
                if (_stations is null)
                {
                    return;
                }

                var now = _time.GetTimestamp();
                var entry = Lookup(address);
                if (entry is null)
                {
                    entry = new Entry(new KnownStation(address, advert.Name));
                    _stations.Add(entry);
                    _version++;
                    save = changed = true;
                }
                else if (entry.Station.Name != advert.Name)
                {
                    entry.Station = entry.Station with { Name = advert.Name };
                    _version++;
                    save = changed = true;
                }

                changed |= !entry.Seen;
                entry.Seen = true;
                entry.LastAdvert = ++_adverts;
                if (entry.RandomAddress is null)
                {
                    newlyHeard = _newlyHeard;
                    _newlyHeard = NewSignal();
                }

                entry.RandomAddress = advert.RandomAddress;

                // An advert without Valve's data says nothing about the state, so the last one stands.
                if (version == StationVersion.V2 && advert.ValveData is { } data)
                {
                    var power = StationProtocol.AdvertState(data);
                    changed |= power != entry.Power;
                    entry.Power = power;
                    var state = StationProtocol.AdvertStateByte(data);
                    if (state != entry.State)
                    {
                        entry.State = state;
                        entry.StateSince = now;
                        stateChanged = true;
                    }

                    if (power == StationPower.On)
                    {
                        entry.WokenAt = null;
                    }

                    // The command got through after all.
                    if (entry.Problem is not null && entry.Told == power)
                    {
                        entry.Problem = null;
                        entry.Told = null;
                        changed = true;
                    }
                }
            }

            newlyHeard?.TrySetResult();
            if (save)
            {
                Save();
            }

            if (stateChanged)
            {
                CheckWakes();

                // Not from this Bluetooth callback: the watch it comes from may be the one to stop.
                FollowSoon();
            }

            if (changed)
            {
                ScheduleTick();
                Raise();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: an advert couldn't be read ({Reason(e)})");
        }
    }

    /// <summary>
    /// Watches the radio while a scan runs, a command runs or one has just run, a listener is open in "Live while
    /// shown", or a station is waking up or going to sleep (<see cref="FollowTime"/>); stops otherwise.
    /// </summary>
    private void UpdateWatch()
    {
        lock (_watchGate)
        {
            bool want;
            lock (_lock)
            {
                want = _scans > 0 || _commandRunning || _settling || _live || FollowLocked(_time.GetTimestamp()) is not null;
            }

            if (want == (_watch is not null))
            {
                return;
            }

            if (want)
            {
                try
                {
                    _watch = _radio.Watch(Heard);
                }
                catch (Exception e)
                {
                    Log($"Base stations: Mirador couldn't listen for the base stations ({Reason(e)})");
                    return;
                }

                lock (_lock)
                {
                    _watching = true;
                }
            }
            else
            {
                var watch = _watch!;
                _watch = null;
                lock (_lock)
                {
                    _watching = false;
                }

                try
                {
                    watch.Dispose();
                }
                catch (Exception e)
                {
                    Log($"Base stations: listening didn't stop cleanly ({Reason(e)})");
                }
            }
        }

        Raise();
    }

    /// <summary>
    /// Under the lock: while a station's last advert says it is waking up or going to sleep, for less than
    /// <see cref="FollowTime"/>, how long until the first of them reaches it; null when none is.
    /// </summary>
    private TimeSpan? FollowLocked(long now)
    {
        TimeSpan? due = null;
        foreach (var entry in _stations ?? [])
        {
            if (entry.Power == StationPower.Waking)
            {
                Earliest(ref due, FollowTime - _time.GetElapsedTime(entry.StateSince, now));
            }
        }

        return due;
    }

    /// <summary>
    /// Looks again at once, from a timer: whether a station is still changing, and until when. The timer is set outside
    /// the lock: its callback takes the watch's lock first, as everything else does.
    /// </summary>
    private void FollowSoon() => FollowTimer().Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Starts or stops listening for a station that is changing (<see cref="UpdateWatch"/>), and sets the timer for when
    /// the first of them has been followed for <see cref="FollowTime"/>.
    /// </summary>
    private void Follow()
    {
        try
        {
            UpdateWatch();
            TimeSpan? due;
            lock (_lock)
            {
                due = FollowLocked(_time.GetTimestamp());
            }

            if (due is { } wait)
            {
                FollowTimer().Change(wait, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: {Reason(e)}");
        }
    }

    private ITimer FollowTimer()
    {
        lock (_lock)
        {
            return _followTimer ??= _time.CreateTimer(_ => Follow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopListening()
    {
        lock (_lock)
        {
            _listens--;
        }

        UpdateWatch();
    }

    /// <summary>Under the lock: makes <paramref name="due"/> <paramref name="left"/> when that is sooner and still to come.</summary>
    private static void Earliest(ref TimeSpan? due, TimeSpan left)
    {
        if (left > TimeSpan.Zero && (due is null || left < due))
        {
            due = left;
        }
    }

    /// <summary>Sets a timer for the next moment Home stops counting a station as waking after an On.</summary>
    private void ScheduleTick()
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            TimeSpan? due = null;
            foreach (var entry in _stations ?? [])
            {
                if (entry.WokenAt is { } woken)
                {
                    Earliest(ref due, SettleTime - _time.GetElapsedTime(woken, now));
                }
            }

            if (due is { } wait)
            {
                _tickTimer ??= _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _tickTimer.Change(wait, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _tickTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Tick()
    {
        try
        {
            ScheduleTick();
            Raise();
        }
        catch (Exception e)
        {
            Log($"Base stations: {Reason(e)}");
        }
    }

    private sealed class Listening(StationManager owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.StopListening();
            }
        }
    }
}
