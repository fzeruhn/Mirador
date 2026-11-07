namespace Mirador.Core.BaseStations;

// After an On: watching the 2.0 stations it reached until they are on, and sending the On again to one that is stuck
// waking up.
public sealed partial class StationManager
{
    /// <summary>
    /// How long a 2.0 station's advert may stay at its first waking state (<see cref="StationProtocol.StartingState"/>)
    /// after an On reached it before Mirador sends it the On again. A healthy one moves on after about 1.5 s.
    /// </summary>
    public static readonly TimeSpan StallTime = TimeSpan.FromSeconds(8);

    /// <summary>How many times at most one On is sent again to a station that is stuck waking up.</summary>
    public const int Resends = 2;

    // Under _lock.
    private readonly List<Wake> _wakes = [];
    private ITimer? _wakeTimer;

    // Under _lock: counts the cancellations, so that an On asked for before the latest one starts no watch.
    private long _wakeEpoch;

    /// <summary>Under the lock: the station is one the watch is for (selected, unless the user picked it on its own).</summary>
    private static bool Wanted(Entry entry, Wake wake) => entry.Station.Selected || !wake.ForSelected;

    /// <summary>
    /// Under the lock: an On reached the 2.0 station. Home counts it as waking for a while, and it is watched until it
    /// is on; after an On sent again, its watch goes on from now. An On asked for before a later command, Monado's
    /// start, the session's off or quitting (one still sending then goes on) starts no watch: what came after it stands.
    /// </summary>
    private void WokenLocked(Entry entry, Work work, long now)
    {
        entry.WokenAt = now;
        if (work.WakeEpoch != _wakeEpoch)
        {
            return;
        }

        if (work.ResendOf is { } wake)
        {
            wake.Since = now;
            wake.Resend = null;
            return;
        }

        _wakes.RemoveAll(w => w.Address == entry.Station.Address);
        _wakes.Add(new Wake(entry.Station.Address, work.Address is null, work.FromSession, now));
    }

    /// <summary>
    /// Under the lock: stops watching the stations an On reached, also those an On already asked for reaches from now
    /// on, and takes the Ons sent again that are still queued (one being sent goes on), for a later command, Monado's
    /// start, the session's off or quitting.
    /// </summary>
    private List<Work> CancelWakesLocked()
    {
        _wakeEpoch++;
        _wakes.Clear();
        _wakeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return TakeQueuedLocked(w => w.ResendOf is not null);
    }

    /// <summary>
    /// Under the lock: how much longer Mirador listens for the stations it watches after an On: until the last watch
    /// ends, and for as long as an On sent again is still queued or being sent. Zero when none is watched.
    /// </summary>
    private TimeSpan WakesLeftLocked(long now)
    {
        var left = TimeSpan.Zero;
        foreach (var wake in _wakes)
        {
            var wakeLeft = wake.Resend is not null ? SettleTime : SettleTime - _time.GetElapsedTime(wake.Since, now);
            if (wakeLeft > left)
            {
                left = wakeLeft;
            }
        }

        return left;
    }

    /// <summary>
    /// Looks at the stations watched after an On. One that is on, no longer wanted, out of Ons to send again or watched
    /// for <see cref="SettleTime"/> since an On last reached it isn't watched any more. One whose advert has stayed at
    /// <see cref="StationProtocol.StartingState"/> for <see cref="StallTime"/> since then gets the On again, queued
    /// like a command. Then the timer is set for the next such moment. Never throws.
    /// </summary>
    private void CheckWakes()
    {
        try
        {
            var start = false;
            lock (_lock)
            {
                var now = _time.GetTimestamp();
                TimeSpan? due = null;
                foreach (var wake in _wakes.ToList())
                {
                    // Queued or being sent: watched again once it has reached the station.
                    if (wake.Resend is not null)
                    {
                        continue;
                    }

                    var watched = _time.GetElapsedTime(wake.Since, now);
                    if (Lookup(wake.Address) is not { } entry
                        || !Wanted(entry, wake)
                        || entry.Power == StationPower.On
                        || wake.Left == 0
                        || watched >= SettleTime)
                    {
                        _wakes.Remove(wake);
                        continue;
                    }

                    Earliest(ref due, SettleTime - watched);
                    if (entry.State != StationProtocol.StartingState)
                    {
                        continue;
                    }

                    // From when it got there, or from when the On reached it if it was there already.
                    var stuck = StallTime - _time.GetElapsedTime(Math.Max(entry.StateSince, wake.Since), now);
                    if (stuck > TimeSpan.Zero)
                    {
                        Earliest(ref due, stuck);
                        continue;
                    }

                    wake.Left--;
                    wake.Resend = new Work(WorkKind.Switch, StationCommand.On, wake.Address, CancellationToken.None, wake.FromSession, wake);
                    start |= EnqueueLocked(wake.Resend);
                }

                if (due is { } wait)
                {
                    _wakeTimer ??= _time.CreateTimer(_ => CheckWakes(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _wakeTimer.Change(wait, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _wakeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }

            if (start)
            {
                StartWorker();
            }
        }
        catch (Exception e)
        {
            Log($"Base stations: watching the base stations wake up failed ({Reason(e)})");
        }
    }

    /// <summary>A 2.0 station an On reached, watched until it is on. Changed only under the lock.</summary>
    private sealed class Wake(string address, bool forSelected, bool fromSession, long since)
    {
        public string Address { get; } = address;

        /// <summary>The On was for the selected stations, so it is sent again only while this one is selected.</summary>
        public bool ForSelected { get; } = forSelected;

        /// <summary>The On was the session's.</summary>
        public bool FromSession { get; } = fromSession;

        /// <summary>When the On, or the last one sent again, reached the station.</summary>
        public long Since { get; set; } = since;

        /// <summary>How many more times the On may be sent again.</summary>
        public int Left { get; set; } = Resends;

        /// <summary>The On sent again, while it is queued or being sent.</summary>
        public Work? Resend { get; set; }
    }
}
