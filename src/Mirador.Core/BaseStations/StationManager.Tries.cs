namespace Mirador.Core.BaseStations;

// One command's rounds of tries: connecting to each station, writing to it and naming the ones that didn't answer.
public sealed partial class StationManager
{
    /// <summary>
    /// Up to <see cref="Tries"/> rounds, <see cref="TryPause"/> apart, each trying once every station not done yet that
    /// is still wanted; then names the ones that were tried and never answered. Bluetooth going away ends the rounds,
    /// and a station not tried by then is left as it was.
    /// </summary>
    private async Task TryAllAsync(List<string> targets, Work work)
    {
        var pending = new List<string>(targets);
        var tried = new HashSet<string>();
        var reasons = new Dictionary<string, string>();
        var stop = false;
        for (var round = 1; round <= Tries && pending.Count > 0 && !stop; round++)
        {
            if (round > 1)
            {
                await Task.Delay(TryPause, _time, work.Token).ConfigureAwait(false);
            }

            foreach (var address in pending.ToList())
            {
                KnownStation station;
                bool random;
                lock (_lock)
                {
                    if (Lookup(address) is not { } entry || !Wanted(entry, work))
                    {
                        pending.Remove(address);
                        continue;
                    }

                    station = entry.Station;

                    // Never heard since Mirador started: base stations use random addresses.
                    random = entry.RandomAddress ?? true;
                }

                var bluetooth = _radio.State;
                if (bluetooth != BluetoothState.Ready)
                {
                    foreach (var rest in pending.SkipWhile(a => a != address))
                    {
                        reasons[rest] = Unusable(bluetooth);
                    }

                    stop = true;
                    break;
                }

                tried.Add(address);
                if (await TryOnceAsync(station, random, work).ConfigureAwait(false) is { } reason)
                {
                    reasons[address] = reason;
                }
                else
                {
                    pending.Remove(address);
                    Answered(station, work);
                }
            }
        }

        var lines = new List<string>();
        var named = false;
        lock (_lock)
        {
            foreach (var address in pending)
            {
                if (Lookup(address) is not { } entry || !Wanted(entry, work))
                {
                    continue;
                }

                var reason = reasons.GetValueOrDefault(address, "no reason given");
                var station = Line(work, entry.Station.DisplayName, address);
                if (tried.Contains(address))
                {
                    entry.Problem = NoAnswer;
                    entry.Told = work.Kind == WorkKind.Switch ? Shows(work.Command) : null;
                    named = true;
                    lines.Add($"{station}: didn't answer ({reason})");
                }
                else
                {
                    lines.Add($"{station}: not sent ({reason})");
                }
            }
        }

        foreach (var line in lines)
        {
            Log(line);
        }

        if (named)
        {
            Raise();
        }
    }

    /// <summary>How the log names the work for one station: <c>Base stations: On Desk (CE:…)</c>, with "again (stuck waking up)" for an On sent again.</summary>
    private static string Line(Work work, string display, string address) =>
        $"Base stations: {Name(work)} {display} ({address}){(work.ResendOf is null ? string.Empty : " again (stuck waking up)")}";

    /// <summary>What a 2.0 station's advert says once it has done the command; null for standby, which shows nothing Mirador can see.</summary>
    private static StationPower? Shows(StationCommand command) =>
        command switch
        {
            StationCommand.On => StationPower.On,
            StationCommand.Sleep => StationPower.Asleep,
            _ => null,
        };

    /// <summary>
    /// A station took the command: its problem goes, a 1.0 station's state is what it was told, and a 2.0 station an
    /// On reached is watched while it wakes up.
    /// </summary>
    private void Answered(KnownStation station, Work work)
    {
        var display = station.DisplayName;
        var woken = false;
        lock (_lock)
        {
            if (Lookup(station.Address) is { } entry)
            {
                entry.Problem = null;
                entry.Told = null;
                display = entry.Station.DisplayName;
                if (work.Kind == WorkKind.Switch)
                {
                    if (StationProtocol.VersionOf(entry.Station.Name) == StationVersion.V1)
                    {
                        entry.Power = work.Command == StationCommand.On ? StationPower.On : StationPower.Asleep;
                    }
                    else if (work.IsOn)
                    {
                        WokenLocked(entry, work, _time.GetTimestamp());
                        woken = true;
                    }
                    else
                    {
                        // Told to go off: it isn't waking any more, whatever its advert still says.
                        entry.WokenAt = null;
                        _wakes.RemoveAll(w => w.Address == entry.Station.Address);
                    }
                }
            }
        }

        Log($"Base stations: {Name(work)} {display} ({station.Address}): {(work.ResendOf is null ? "done" : "sent again (stuck waking up)")}");
        if (woken)
        {
            CheckWakes();
        }

        ScheduleTick();
        Raise();
    }

    /// <summary>
    /// One try at one station, given up after <see cref="TryLimit"/>: Windows can't cancel a connection, so a late one
    /// is closed when it comes. Null when the station took it, else why not.
    /// </summary>
    private async Task<string?> TryOnceAsync(KnownStation station, bool random, Work work)
    {
        if (!StationProtocol.TryParseAddress(station.Address, out var address))
        {
            return "its address can't be read";
        }

        var stop = new Stopper(work.Token);
        var attempt = Task.Run(() => AttemptAsync(address, random, station, work, stop.Token));
        _ = attempt.ContinueWith(_ => stop.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        try
        {
            return await attempt.WaitAsync(TryLimit, _time, work.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stop.Cancel();
            return $"no answer within {TryLimit.TotalSeconds:0} seconds";
        }
    }

    /// <summary>Connects, sends, and closes the link whatever happens. Never throws: null when sent, else why not.</summary>
    private async Task<string?> AttemptAsync(ulong address, bool random, KnownStation station, Work work, CancellationToken ct)
    {
        try
        {
            var link = await _radio.ConnectAsync(address, random, ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                await WriteAsync(link, station, work, ct).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await link.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Closing failed; the connection goes when Windows lets it.
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "stopped";
        }
        catch (Exception e)
        {
            return Reason(e);
        }
    }

    /// <summary>Writes the command, or Identify, the way the station's generation takes it.</summary>
    private static async Task WriteAsync(IStationLink link, KnownStation station, Work work, CancellationToken ct)
    {
        switch (StationProtocol.VersionOf(station.Name), work.Kind)
        {
            case (StationVersion.V2, WorkKind.Identify):
                await link.WriteAsync(StationProtocol.V2Service, StationProtocol.V2IdentifyCharacteristic, StationProtocol.V2IdentifyValue, ct)
                    .ConfigureAwait(false);
                break;
            case (StationVersion.V2, WorkKind.Switch):
                foreach (var value in StationProtocol.V2Writes(work.Command))
                {
                    await link.WriteAsync(StationProtocol.V2Service, StationProtocol.V2PowerCharacteristic, value, ct).ConfigureAwait(false);
                }

                break;
            case (StationVersion.V1, WorkKind.Switch):
                uint? id = StationProtocol.TryParseV1Id(station.Id, out var parsed) ? parsed : null;
                await link.WriteAsync(StationProtocol.V1Service, StationProtocol.V1PowerCharacteristic, StationProtocol.V1Write(work.Command, id), ct)
                    .ConfigureAwait(false);
                break;
            default:
                throw new StationException("Mirador can't send that to this base station.");
        }
    }

    /// <summary>
    /// A token for a work or one try, linked to an outer one: cancelled when given up, stopped or the outer token is;
    /// cancelling and disposing are safe from any thread, in either order.
    /// </summary>
    private sealed class Stopper
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public Stopper(CancellationToken outer)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(outer);
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _source.Cancel();
                }
                catch (Exception)
                {
                    // A failing cancellation callback is the radio's; the work or try ends either way.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.Dispose();
            }
        }
    }
}
