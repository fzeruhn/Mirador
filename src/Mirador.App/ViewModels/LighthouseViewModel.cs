using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.BaseStations;

namespace Mirador.App.ViewModels;

/// <summary>One base station on the Lighthouse page: what its card shows, and what its buttons and menu do.</summary>
public sealed partial class StationItem : ObservableObject
{
    private readonly LighthouseViewModel owner;
    private bool updating;

    public StationItem(LighthouseViewModel owner, string address)
    {
        this.owner = owner;
        Address = address;
    }

    /// <summary>The Bluetooth address, which names the station to the manager.</summary>
    public string Address { get; }

    /// <summary>The station's own Bluetooth name ("LHB-…", "HTC BS …"): the rename dialog's placeholder.</summary>
    public string Name { get; private set; } = "";

    /// <summary>The name the user gave it, or null.</summary>
    public string? CustomName { get; private set; }

    /// <summary>A 1.0 station's ID, or null.</summary>
    public string? Id { get; private set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    /// <summary>The address, after the station's own name when the user renamed it.</summary>
    [ObservableProperty]
    public partial string AddressLine { get; set; } = "";

    /// <summary>What it is doing: "2.0 · On", "Not seen in the last scan", "Looking…" (during a scan), "Didn't answer", …</summary>
    [ObservableProperty]
    public partial string StateLine { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsV2 { get; set; }

    public bool IsV1 => !IsV2;

    /// <summary>Whether Identify can be sent: a 2.0 station that is on, and Bluetooth ready.</summary>
    [ObservableProperty]
    public partial bool CanIdentify { get; set; }

    /// <summary>For screen readers, why Identify is greyed (its tooltip is "Identify" either way); empty while the station is on.</summary>
    [ObservableProperty]
    public partial string IdentifyHelp { get; set; } = "";

    /// <summary>Whether its menu's Power on and Power off can be sent (Bluetooth is ready).</summary>
    public bool CanSwitch { get; private set; }

    /// <summary>The station blinks (Identify).</summary>
    public void Identify() => owner.Identify(this);

    public void PowerOn() => owner.Switch(this, StationCommand.On);

    /// <summary>Power off: the station sleeps.</summary>
    public void PowerOff() => owner.SwitchOff(this);

    public void Forget() => owner.Forget(this);

    /// <summary>Takes what the manager says about the station now.</summary>
    internal void Update(StationView view, bool looking, bool bluetoothReady)
    {
        var station = view.Station;
        var v2 = station.Version == StationVersion.V2;
        var on = v2 && view.Seen && view.Power == StationPower.On;
        updating = true;
        try
        {
            Name = station.Name;
            CustomName = station.CustomName;
            Id = station.Id;
            DisplayName = station.DisplayName;
            AddressLine = station.CustomName is null ? station.Address : $"{station.Name} · {station.Address}";
            StateLine = Describe(view, looking);
            IsSelected = station.Selected;
            IsV2 = v2;
            CanIdentify = on && bluetoothReady;
            IdentifyHelp = on ? "" : "Switch it on to make it blink.";
            CanSwitch = bluetoothReady;
        }
        finally
        {
            updating = false;
        }
    }

    private static string Describe(StationView view, bool looking)
    {
        if (view.Problem is not null)
        {
            return "Didn't answer";
        }

        if (!view.Seen)
        {
            return looking ? "Looking…" : "Not seen in the last scan";
        }

        if (view.Station.Version == StationVersion.V1)
        {
            return view.Station.Id is null ? "1.0 · Off takes a minute without its ID" : "1.0";
        }

        return view.Power switch
        {
            StationPower.On => "2.0 · On",
            StationPower.Waking => "2.0 · Waking up",
            StationPower.Asleep => "2.0 · Asleep",
            _ => "2.0 · State unknown",
        };
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!updating)
        {
            owner.SetSelected(this, value);
        }
    }

    partial void OnIsV2Changed(bool value) => OnPropertyChanged(nameof(IsV1));
}

/// <summary>
/// The Lighthouse page: Bluetooth's state, the known base stations with their states, switching them, and the base
/// stations' settings. It follows the manager's changes while it is shown (<see cref="SetShown"/>), and holds a listen
/// then (the manager decides whether to listen, by "Station state tracking rate"); commands go on when the
/// page is left. The settings save at once, as App settings do.
/// </summary>
public sealed partial class LighthouseViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly StationManager stations;
    private readonly DispatcherQueue ui;
    private readonly bool loading;
    private IDisposable? listening;
    private int refreshQueued;

    /// <param name="host">Mirador's parts; the page uses its <see cref="AppHost.Stations"/> and <see cref="AppHost.Settings"/>.</param>
    /// <param name="ui">The page's queue: every change the manager reports is shown through it.</param>
    public LighthouseViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        stations = host.Stations;
        this.ui = ui;

        // Read once: the base stations' settings are changed only here.
        loading = true;
        var settings = SettingsViewModel.Read(host, out var problem);
        SettingsProblem = problem;
        PowerOnAtMonadoStart = settings.BaseStationsOnAtStart;
        PowerOffAtMonadoStop = settings.BaseStationsOffAtStop;
        PowerOnAtMiradorStart = settings.BaseStationsOnAtMiradorStart;
        PowerOffAtMiradorQuit = settings.BaseStationsOffAtMiradorQuit;
        ControllersOffAtMonadoStop = settings.ControllersOffAtStop;
        StationState = settings.BaseStationsState == StationStateMode.Live ? 0 : 1; // Constant first, When Relevant second
        loading = false;
    }

    public ObservableCollection<StationItem> Stations { get; } = [];

    /// <summary>"Power on when Monado starts".</summary>
    [ObservableProperty]
    public partial bool PowerOnAtMonadoStart { get; set; }

    /// <summary>"Power off when Monado stops".</summary>
    [ObservableProperty]
    public partial bool PowerOffAtMonadoStop { get; set; }

    /// <summary>"Power on when Mirador starts".</summary>
    [ObservableProperty]
    public partial bool PowerOnAtMiradorStart { get; set; }

    /// <summary>"Power off when Mirador quits".</summary>
    [ObservableProperty]
    public partial bool PowerOffAtMiradorQuit { get; set; }

    /// <summary>"Turn off controllers and trackers when Monado stops".</summary>
    [ObservableProperty]
    public partial bool ControllersOffAtMonadoStop { get; set; }

    /// <summary>The "Station state" drop-down: 0 from the last scan, 1 live while shown.</summary>
    [ObservableProperty]
    public partial int StationState { get; set; }

    /// <summary>Why the settings couldn't be read or saved, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsProblem))]
    public partial string? SettingsProblem { get; set; }

    public bool HasSettingsProblem => SettingsProblem is not null;

    [ObservableProperty]
    public partial bool BluetoothMissing { get; set; }

    [ObservableProperty]
    public partial bool BluetoothOff { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool BluetoothReady { get; set; } = true;

    /// <summary>Why the list couldn't be saved, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    public partial string? SaveProblem { get; set; }

    public bool HasSaveProblem => SaveProblem is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsLooking { get; set; }

    /// <summary>Whether a command is being sent to the selected stations now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchOnCommand), nameof(SwitchOffCommand))]
    public partial bool AnySelected { get; set; }

    [ObservableProperty]
    public partial bool HasNoStations { get; set; }

    /// <summary>
    /// Follows the stations' changes, and holds a listen, while <paramref name="shown"/>: the page is the current one
    /// and the window is on screen. Stops otherwise.
    /// </summary>
    public void SetShown(bool shown)
    {
        if (shown == (listening is not null))
        {
            return;
        }

        if (shown)
        {
            stations.Changed += OnChanged;
            listening = stations.Listen();
            Refresh();
        }
        else
        {
            stations.Changed -= OnChanged;
            listening!.Dispose();
            listening = null;
        }
    }

    /// <summary>Gives the station a name of the user's; null when saved, else what went wrong (the dialog stays open).</summary>
    public string? Rename(StationItem item, string text) => stations.Rename(item.Address, text);

    /// <summary>Keeps a 1.0 station's ID; null when saved, else what went wrong (the dialog stays open).</summary>
    public string? SetId(StationItem item, string text) => stations.SetId(item.Address, text);

    internal void SetSelected(StationItem item, bool selected) => stations.SetSelected(item.Address, selected);

    internal void Switch(StationItem item, StationCommand command) =>
        _ = stations.SwitchAsync(item.Address, command, CancellationToken.None);

    internal void SwitchOff(StationItem item) => Switch(item, stations.OffCommand());

    internal void Identify(StationItem item) => _ = stations.IdentifyAsync(item.Address, CancellationToken.None);

    internal void Forget(StationItem item) => stations.Forget(item.Address);

    /// <summary>Scans for the base stations (8 s); opening the page doesn't.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan() => _ = stations.ScanAsync(CancellationToken.None);

    private bool CanScan() => BluetoothReady && !IsLooking;

    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOn() => _ = stations.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);

    /// <summary>Off: the selected stations sleep.</summary>
    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOff() => _ = stations.SwitchSelectedAsync(stations.OffCommand(), CancellationToken.None);

    /// <summary>On and Off: Bluetooth is ready, a station is selected and no command is being sent.</summary>
    private bool CanSwitchSelected() => BluetoothReady && AnySelected && !IsRunning;

    partial void OnPowerOnAtMonadoStartChanged(bool value) => SaveSetting(s => s with { BaseStationsOnAtStart = value });

    partial void OnPowerOffAtMonadoStopChanged(bool value) => SaveSetting(s => s with { BaseStationsOffAtStop = value });

    partial void OnPowerOnAtMiradorStartChanged(bool value) => SaveSetting(s => s with { BaseStationsOnAtMiradorStart = value });

    partial void OnPowerOffAtMiradorQuitChanged(bool value) => SaveSetting(s => s with { BaseStationsOffAtMiradorQuit = value });

    partial void OnControllersOffAtMonadoStopChanged(bool value) => SaveSetting(s => s with { ControllersOffAtStop = value });

    partial void OnStationStateChanged(int value) =>
        SaveSetting(s => s with { BaseStationsState = value == 0 ? StationStateMode.Live : StationStateMode.LastScan });

    /// <summary>Saves one setting at once and tells the manager, which reads "Station state" again.</summary>
    private void SaveSetting(Func<AppSettings, AppSettings> change)
    {
        if (loading)
        {
            return;
        }

        SettingsProblem = SettingsViewModel.Write(host, change);
        stations.SettingsChanged();
    }

    /// <summary>On whichever thread the manager is on: shows the change on the page's thread, once for a burst of them.</summary>
    private void OnChanged()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 0 && !ui.TryEnqueue(RefreshQueued))
        {
            Volatile.Write(ref refreshQueued, 0);
        }
    }

    private void RefreshQueued()
    {
        Volatile.Write(ref refreshQueued, 0);
        if (listening is not null)
        {
            Refresh();
        }
    }

    /// <summary>Reads everything the page shows from the manager again; the cards are updated in place.</summary>
    private void Refresh()
    {
        var bluetooth = stations.Bluetooth;
        BluetoothMissing = bluetooth == BluetoothState.Missing;
        BluetoothOff = bluetooth == BluetoothState.Off;
        BluetoothReady = bluetooth == BluetoothState.Ready;
        SaveProblem = stations.SaveProblem;
        IsLooking = stations.Looking;
        IsRunning = stations.Running is not null;

        var views = stations.Stations;
        AnySelected = views.Any(v => v.Station.Selected);
        Show(views);
        HasNoStations = views.Count == 0;
    }

    /// <summary>
    /// Puts the stations in the list in the manager's order, keeping each one's card (so that an open menu or the
    /// focus survives an update): cards of stations no longer listed go, new ones come in.
    /// </summary>
    private void Show(IReadOnlyList<StationView> views)
    {
        var addresses = views.Select(v => v.Station.Address).ToHashSet(StringComparer.Ordinal);
        for (var i = Stations.Count - 1; i >= 0; i--)
        {
            if (!addresses.Contains(Stations[i].Address))
            {
                Stations.RemoveAt(i);
            }
        }

        for (var i = 0; i < views.Count; i++)
        {
            var address = views[i].Station.Address;
            var at = IndexOf(address, i);
            StationItem item;
            if (at < 0)
            {
                item = new StationItem(this, address);
                Stations.Insert(i, item);
            }
            else
            {
                item = Stations[at];
                if (at != i)
                {
                    Stations.RemoveAt(at);
                    Stations.Insert(i, item);
                }
            }

            item.Update(views[i], IsLooking, BluetoothReady);
        }
    }

    private int IndexOf(string address, int from)
    {
        for (var i = from; i < Stations.Count; i++)
        {
            if (Stations[i].Address == address)
            {
                return i;
            }
        }

        return -1;
    }
}
