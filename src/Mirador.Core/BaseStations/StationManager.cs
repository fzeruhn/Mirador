using Mirador.Core.Modules;
using Mirador.Core.Session;

namespace Mirador.Core.BaseStations;

/// <summary>One known base station as the Lighthouse page shows it.</summary>
/// <param name="Station">What Mirador keeps about it.</param>
/// <param name="Seen">
/// Its advert was heard in the last scan or since (before Mirador's first scan: since Mirador started). It doesn't
/// lapse with time; only a scan that doesn't hear the station takes it away.
/// </param>
/// <param name="Power">What it is doing: a 2.0 station's from its adverts, a 1.0 station's from what Mirador last told it.</param>
/// <param name="Problem">"didn't answer" when the last command or Identify didn't get through to it; null otherwise.</param>
public sealed record StationView(KnownStation Station, bool Seen, StationPower Power, string? Problem);

/// <summary>One line about the selected base stations, for Home.</summary>
/// <param name="Text">What the line says: Bluetooth's state, that none is selected, or how many of them are powered.</param>
/// <param name="Level">Its icon: the check only when every selected station is on and none has a problem.</param>
/// <param name="Busy">
/// The stations are changing state: a command is being sent, or a selected station is waking up. Home shows a spinner
/// in place of the icon then. Never while the line is about Bluetooth or about none being selected.
/// </param>
public sealed record StationsSummary(string Text, StatusLevel Level, bool Busy);

/// <summary>
/// Keeps the list of base stations Mirador has found, follows their states from their Bluetooth adverts, and switches
/// the selected ones: on the user's On and Off, when a Monado session starts, a few seconds after it ends, and with
/// Mirador's start and quit. Commands run one at a time, in the order asked, each station tried up to
/// <see cref="Tries"/> times. A 2.0 station that took an On but stays stuck waking up gets it again, at most
/// <see cref="Resends"/> times. Mirador listens to the adverts only during a scan (<see cref="ScanAsync"/>: at
/// Mirador's start and on "Scan"), around a command, and all the time with "Station state tracking rate: Constant".
/// </summary>
/// <remarks>
/// Everything here may be called from any thread. <see cref="Changed"/> is raised on whichever thread made the change
/// (often a Bluetooth or timer thread), never while the manager holds its lock; a subscriber should hand the work to
/// its UI thread and read the properties there. Nothing is ever sent to a station that isn't selected, except what
/// <see cref="SwitchAsync"/> and <see cref="IdentifyAsync"/> are asked for that one station, and nothing connects to a
/// station except to send it a command. The <see cref="ISessionHook"/> calls return at once and never throw.
/// </remarks>
public sealed partial class StationManager : ISessionHook
{
    /// <summary>How long a scan listens (<see cref="ScanAsync"/>), with <see cref="Looking"/> true meanwhile.</summary>
    public static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a command listens first for its stations that haven't been heard since Mirador started: Windows reaches
    /// a station by its address only once it has heard the station's advert.
    /// </summary>
    public static readonly TimeSpan FindTime = TimeSpan.FromSeconds(5);

    /// <summary>How long Mirador keeps listening after a command, so that the stations' new states show.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long at most Mirador keeps listening to a station whose last advert said it was waking up or going to sleep,
    /// whatever "Station state tracking rate" says, counted from its last change of state: until it says it is on or
    /// asleep, so that what Home and the Lighthouse page show doesn't stop half way. A station that went quiet meanwhile
    /// (unplugged) isn't listened for forever.
    /// </summary>
    public static readonly TimeSpan FollowTime = TimeSpan.FromMinutes(2);

    /// <summary>How long after a session ends the stations are switched off, so that "Restart Monado" doesn't switch them off and on.</summary>
    public static readonly TimeSpan OffDelay = TimeSpan.FromSeconds(5);

    /// <summary>The longest one try at one station may take (Windows can't cancel a connection, so Mirador stops waiting for it).</summary>
    public static readonly TimeSpan TryLimit = TimeSpan.FromSeconds(10);

    /// <summary>The pause between two rounds of tries.</summary>
    public static readonly TimeSpan TryPause = TimeSpan.FromSeconds(1);

    /// <summary>How many rounds of tries a command makes before it names the stations that didn't answer.</summary>
    public const int Tries = 3;

    /// <summary>How long after the list couldn't be read Mirador waits before reading it again.</summary>
    internal static readonly TimeSpan LoadRetry = TimeSpan.FromSeconds(5);

    private const string NoAnswer = "didn't answer";
    private const string BadId = "That isn't 8 letters and digits (0–9, A–F).";
    private const string NotListed = "Mirador no longer lists that base station.";
    private static readonly string TooLong = $"Use at most {KnownStation.MaxNameLength} characters.";

    private readonly IStationRadio _radio;
    private readonly StationStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    // Lock order: _watchGate, _saveGate or _liveGate first, then _lock. Nothing outside the manager is called under
    // _lock except the clock's timers. Under _watchGate the radio's Watch and its watch's Dispose are called (WinRT
    // returns from them at once and calls nothing back under them); under _saveGate the store's Save; under _liveGate
    // the settings function, for "Station state". No event is raised under any of them.
    private readonly object _lock = new();
    private readonly object _watchGate = new();
    private readonly object _saveGate = new();
    private readonly object _liveGate = new();

    // Under _lock.
    private List<Entry>? _stations;
    private long? _loadFailedAt;
    private long _version;
    private string? _saveProblem;

    // Under _saveGate.
    private long _savedVersion;

    /// <summary>
    /// Makes the manager. The list isn't read here but at first use, so that a store that sets aside a broken file is
    /// seen doing it by whoever reads it first.
    /// </summary>
    /// <param name="radio">The PC's Bluetooth.</param>
    /// <param name="store">Where the list is kept.</param>
    /// <param name="settings">Mirador's settings, read for every decision; it must not throw.</param>
    /// <param name="time">The clock for every wait and every "how long ago".</param>
    /// <param name="log">Takes a line for Mirador's log.</param>
    public StationManager(IStationRadio radio, StationStore store, Func<AppSettings> settings, TimeProvider time, Action<string> log)
    {
        _radio = radio;
        _store = store;
        _settings = settings;
        _time = time;
        _log = log;
        radio.StateChanged += Raise;
    }

    /// <summary>
    /// Raised, on any thread, whenever something the pages show may have changed: the list, a station's state, problem or
    /// whether it is seen, <see cref="Looking"/>, <see cref="Running"/>, <see cref="Bluetooth"/> or <see cref="SaveProblem"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>Whether Bluetooth can be used now (the radio's state).</summary>
    public BluetoothState Bluetooth => _radio.State;

    /// <summary>The known stations, by display name.</summary>
    public IReadOnlyList<StationView> Stations
    {
        get
        {
            EnsureLoaded();
            lock (_lock)
            {
                return [.. Ordered().Select(e => new StationView(e.Station, e.Seen, e.Power, e.Problem))];
            }
        }
    }

    /// <summary>The command being sent now, or null (also while only Identify runs).</summary>
    public StationCommand? Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>Why the list couldn't be saved the last time, in plain words, or null when it was.</summary>
    public string? SaveProblem
    {
        get
        {
            lock (_lock)
            {
                return _saveProblem;
            }
        }
    }

    /// <summary>
    /// What Home says about the selected stations: "1/2 Powered", the stations on (a 1.0 station by what Mirador last
    /// told it) and <see cref="StationView.Seen"/> out of those selected, with " · Desk didn't answer." for the selected
    /// ones whose last command or Identify didn't get through. A selected station not heard in the last scan counts
    /// among the selected, never as powered.
    /// </summary>
    public StationsSummary Summary
    {
        get
        {
            switch (_radio.State)
            {
                case BluetoothState.Missing:
                    return new StationsSummary("This PC has no Bluetooth.", StatusLevel.Info, false);
                case BluetoothState.Off:
                    return new StationsSummary("Bluetooth is off.", StatusLevel.Warning, false);
            }

            EnsureLoaded();
            lock (_lock)
            {
                var selected = Ordered().Where(e => e.Station.Selected).ToList();
                if (selected.Count == 0)
                {
                    return new StationsSummary("None selected: choose yours on the Lighthouse page.", StatusLevel.Info, false);
                }

                var now = _time.GetTimestamp();
                var shown = selected.Select(e => Shown(e, now)).ToList();
                var on = selected.Where((e, i) => e.Seen && shown[i] == StationPower.On).Count();
                var busy = _running is not null || shown.Contains(StationPower.Waking);
                var text = $"{on}/{selected.Count} Powered";
                var problems = selected.Where(e => e.Problem is not null).Select(e => e.Station.DisplayName).ToList();
                if (problems.Count > 0)
                {
                    return new StationsSummary($"{text} · {string.Join(", ", problems)} didn't answer.", StatusLevel.Warning, busy);
                }

                return new StationsSummary(text, on == selected.Count ? StatusLevel.Ok : StatusLevel.Info, busy);
            }
        }
    }

    /// <summary>Selects the station, so that Mirador switches it, or deselects it. The list is saved.</summary>
    public void SetSelected(string address, bool selected) =>
        Change(address, s => s with { Selected = selected });

    /// <summary>
    /// Gives the station a name of the user's (trimmed, at most <see cref="KnownStation.MaxNameLength"/> characters; empty
    /// goes back to its own name). Null when it is saved, else what went wrong in plain words.
    /// </summary>
    public string? Rename(string address, string text)
    {
        var name = text?.Trim() ?? string.Empty;
        if (name.Length > KnownStation.MaxNameLength)
        {
            return Find(address) is null ? NotListed : TooLong;
        }

        return Change(address, s => s with { CustomName = name.Length == 0 ? null : name });
    }

    /// <summary>
    /// Keeps a 1.0 station's ID (8 hex digits, kept as typed without the spaces around it; empty clears it). Null when
    /// it is saved, else what went wrong in plain words.
    /// </summary>
    public string? SetId(string address, string text)
    {
        var id = text?.Trim() ?? string.Empty;
        if (id.Length > 0 && !StationProtocol.TryParseV1Id(id, out _))
        {
            return Find(address) is null ? NotListed : BadId;
        }

        return Change(address, s => s with { Id = id.Length == 0 ? null : id });
    }

    /// <summary>Removes the station from the list. Heard again, it comes back unselected.</summary>
    public void Forget(string address)
    {
        var key = Normalize(address);
        EnsureLoaded();
        lock (_lock)
        {
            if (key is null || _stations is null || _stations.RemoveAll(e => e.Station.Address == key) == 0)
            {
                return;
            }

            _version++;
        }

        Save();
        Raise();
    }

    /// <summary>
    /// The command for every Off Mirador sends (the user's, the session's, as Mirador quits): always
    /// <see cref="StationCommand.Sleep"/>. Standby needs newer firmware and did nothing visible on the stations tried.
    /// </summary>
    public StationCommand OffCommand() => StationCommand.Sleep;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? Normalize(string? address) =>
        StationProtocol.TryParseAddress(address, out var bits) ? StationProtocol.FormatAddress(bits) : null;

    /// <summary>An exception's message as the inside of a parenthesis: trimmed, without its full stop.</summary>
    private static string Reason(Exception e)
    {
        var text = e.Message.Trim().TrimEnd('.');
        return text.Length > 0 ? text : e.GetType().Name;
    }

    /// <summary>Under the lock: the state Home counts for the station, waking for a while after an On reached it (<see cref="Entry.WokenAt"/>).</summary>
    private StationPower Shown(Entry entry, long now) =>
        entry.Power != StationPower.On && entry.WokenAt is { } woken && _time.GetElapsedTime(woken, now) < SettleTime
            ? StationPower.Waking
            : entry.Power;

    /// <summary>Under the lock: the stations by display name.</summary>
    private IEnumerable<Entry> Ordered() =>
        (_stations ?? [])
            .OrderBy(e => e.Station.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Station.Address, StringComparer.Ordinal);

    /// <summary>Under the lock: the station with this address (in the standard form), or null.</summary>
    private Entry? Lookup(string address) => _stations?.Find(e => e.Station.Address == address);

    private KnownStation? Find(string address)
    {
        var key = Normalize(address);
        if (key is null)
        {
            return null;
        }

        EnsureLoaded();
        lock (_lock)
        {
            return Lookup(key)?.Station;
        }
    }

    /// <summary>Reads the list the first time it is needed; when the file can't be read, tries again next time.</summary>
    private void EnsureLoaded()
    {
        lock (_lock)
        {
            // Not read again for a while after it failed: adverts come several times a second.
            if (_stations is not null || (_loadFailedAt is { } failed && _time.GetElapsedTime(failed) < LoadRetry))
            {
                return;
            }
        }

        IReadOnlyList<KnownStation> loaded;
        try
        {
            loaded = _store.Load();
        }
        catch (Exception e)
        {
            bool first;
            lock (_lock)
            {
                first = _loadFailedAt is null;
                _loadFailedAt = _time.GetTimestamp();
            }

            if (first)
            {
                Log($"Base stations: Mirador couldn't read its base station list ({Reason(e)}); it tries again when it needs it.");
            }

            return;
        }

        lock (_lock)
        {
            _stations ??= [.. loaded.Select(s => new Entry(s))];
            _loadFailedAt = null;
        }
    }

    /// <summary>Changes one station and saves the list: null when saved, else what went wrong.</summary>
    private string? Change(string address, Func<KnownStation, KnownStation> change)
    {
        var key = Normalize(address);
        if (key is null)
        {
            return NotListed;
        }

        EnsureLoaded();
        bool changed;
        lock (_lock)
        {
            if (Lookup(key) is not { } entry)
            {
                return NotListed;
            }

            var station = change(entry.Station);
            changed = station != entry.Station;
            if (changed)
            {
                entry.Station = station;
                _version++;
            }
        }

        var problem = Save();
        if (changed)
        {
            Raise();
        }

        return problem;
    }

    /// <summary>
    /// Saves the list as it is now, unless it was saved since its last change, and raises <see cref="Changed"/> when
    /// <see cref="SaveProblem"/> changes. Returns <see cref="SaveProblem"/>. Saves go one at a time, so that an older
    /// list never overwrites a newer one.
    /// </summary>
    private string? Save()
    {
        string? problem;
        bool shown;
        lock (_saveGate)
        {
            KnownStation[] stations;
            long version;
            lock (_lock)
            {
                if (_stations is null || _version == _savedVersion)
                {
                    return _saveProblem;
                }

                version = _version;
                stations = [.. _stations.Select(e => e.Station)];
            }

            try
            {
                _store.Save(stations);
                _savedVersion = version;
                problem = null;
            }
            catch (Exception e)
            {
                problem = $"Mirador couldn't save its base station list ({Reason(e)}).";
            }

            lock (_lock)
            {
                shown = _saveProblem != problem;
                _saveProblem = problem;
            }
        }

        if (problem is not null)
        {
            Log($"Base stations: {problem}");
        }

        if (shown)
        {
            Raise();
        }

        return problem;
    }

    /// <summary>Raises <see cref="Changed"/>; a subscriber's exception is logged and dropped. Never called under a lock.</summary>
    private void Raise()
    {
        if (Changed is not { } changed)
        {
            return;
        }

        // Each subscriber on its own, so that one page's failure doesn't keep the change from another.
        foreach (var subscriber in changed.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception e)
            {
                Log($"Base stations: showing a change failed ({Reason(e)})");
            }
        }
    }

    private void Log(string text)
    {
        try
        {
            _log(text);
        }
        catch (Exception)
        {
            // The log is only for reading later; a failing one must not stop a command.
        }
    }

    /// <summary>What the manager knows about one station besides what is kept. Changed only under the lock.</summary>
    private sealed class Entry(KnownStation station)
    {
        public KnownStation Station { get; set; } = station;

        /// <summary>Heard in the last scan or since (<see cref="StationView.Seen"/>).</summary>
        public bool Seen { get; set; }

        /// <summary>The manager's count of adverts at its last one, which tells whether a scan heard it; 0 while it hasn't been heard since Mirador started.</summary>
        public long LastAdvert { get; set; }

        /// <summary>The address type of its last advert; null while it hasn't been heard since Mirador started.</summary>
        public bool? RandomAddress { get; set; }

        public StationPower Power { get; set; }

        /// <summary>The state byte of its last advert with Valve's data (2.0 only); null while there was none.</summary>
        public byte? State { get; set; }

        /// <summary>When <see cref="State"/> last changed.</summary>
        public long StateSince { get; set; }

        /// <summary>
        /// When an On last reached it (2.0 only), until its advert says it is on or another command reaches it: for
        /// <see cref="SettleTime"/> after it, Home counts it as waking, since the adverts lag behind. Null otherwise.
        /// </summary>
        public long? WokenAt { get; set; }

        public string? Problem { get; set; }

        /// <summary>With <see cref="Problem"/>: what its advert says once it has done what it was told after all, which clears the problem; null when that can't be seen.</summary>
        public StationPower? Told { get; set; }
    }
}
