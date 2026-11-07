namespace Mirador.Core.BaseStations;

// Listening to the stations' adverts: who is listening, what each advert says, and the timer for what lapses.
public sealed partial class StationManager
{
    // Under _lock.
    private int _listens;
    private bool _watching;
    private long _lookingSince;
    private ITimer? _tickTimer;
    private TaskCompletionSource _newlyHeard = NewSignal();

    // Under _watchGate.
    private IDisposable? _watch;

    /// <summary>Whether Mirador is listening and began less than <see cref="ScanTime"/> ago (or <see cref="Rescan"/> was).</summary>
    public bool Looking
    {
        get
        {
            lock (_lock)
            {
                return _watching && _time.GetElapsedTime(_lookingSince) < ScanTime;
            }
        }
    }

    /// <summary>
    /// Listens to the stations' adverts until the returned object is disposed (several may be open at once). Listening
    /// is passive: nothing is sent and nothing connects. The first listener starts <see cref="Looking"/> afresh, even
    /// when Mirador was listening already for a command.
    /// </summary>
    public IDisposable Listen()
    {
        bool first;
        lock (_lock)
        {
            first = _listens++ == 0;
            if (first)
            {
                _lookingSince = _time.GetTimestamp();
            }
        }

        UpdateWatch();
        if (first)
        {
            ScheduleTick();
            Raise();
        }

        return new Listening(this);
    }

    /// <summary>Starts afresh: forgets when each station was heard, and <see cref="Looking"/> begins again.</summary>
    public void Rescan()
    {
        EnsureLoaded();
        lock (_lock)
        {
            foreach (var entry in _stations ?? [])
            {
                entry.HeardAt = null;
            }

            _lookingSince = _time.GetTimestamp();
        }

        ScheduleTick();
        Raise();
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

                changed |= !Seen(entry, now);
                entry.HeardAt = now;
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

    /// <summary>Watches the radio while a listener is open, a command runs or one has just run; stops otherwise.</summary>
    private void UpdateWatch()
    {
        lock (_watchGate)
        {
            bool want;
            lock (_lock)
            {
                want = _listens > 0 || _commandRunning || _settling;
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
                    _lookingSince = _time.GetTimestamp();
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

        ScheduleTick();
        Raise();
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

    /// <summary>
    /// Sets a timer for the next moment <see cref="Looking"/> ends, a station stops being seen or Home stops counting a
    /// station as waking after an On.
    /// </summary>
    private void ScheduleTick()
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            TimeSpan? due = null;
            if (_watching)
            {
                Earliest(ref due, ScanTime - _time.GetElapsedTime(_lookingSince, now));
            }

            foreach (var entry in _stations ?? [])
            {
                if (entry.HeardAt is { } heard)
                {
                    Earliest(ref due, SeenWindow - _time.GetElapsedTime(heard, now));
                }

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
