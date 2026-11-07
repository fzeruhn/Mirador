using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
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

    /// <summary>What it is doing: "2.0 · On", "Looking…", "Didn't answer", …</summary>
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

    [ObservableProperty]
    public partial string IdentifyTip { get; set; } = "";

    /// <summary>Whether its menu's Switch on, Standby and Sleep can be sent (Bluetooth is ready).</summary>
    public bool CanSwitch { get; private set; }

    /// <summary>The station blinks (Identify).</summary>
    public void Identify() => owner.Identify(this);

    public void SwitchOn() => owner.Switch(this, StationCommand.On);

    public void Standby() => owner.Switch(this, StationCommand.Standby);

    public void Sleep() => owner.Switch(this, StationCommand.Sleep);

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
            IdentifyTip = on ? "Identify: make it blink" : "Switch it on to make it blink.";
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
            return looking ? "Looking…" : "Not seen just now";
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
/// The Lighthouse page: Bluetooth's state, the known base stations with their states, and switching them. It listens to
/// the stations' adverts only while it is shown (<see cref="SetShown"/>); commands go on when the page is left.
/// </summary>
public sealed partial class LighthouseViewModel : ObservableObject
{
    private readonly StationManager stations;
    private readonly DispatcherQueue ui;
    private readonly DispatcherQueueTimer tick;
    private IDisposable? listening;
    private int refreshQueued;

    /// <param name="host">Mirador's parts; the page uses its <see cref="AppHost.Stations"/>.</param>
    /// <param name="ui">The page's queue: every change the manager reports is shown through it.</param>
    public LighthouseViewModel(AppHost host, DispatcherQueue ui)
    {
        stations = host.Stations;
        this.ui = ui;

        // A station stops being "seen" a while after its last advert, without a change of its own to say so.
        tick = ui.CreateTimer();
        tick.Interval = TimeSpan.FromSeconds(1);
        tick.IsRepeating = true;
        tick.Tick += (_, _) => Refresh();
    }

    public ObservableCollection<StationItem> Stations { get; } = [];

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
    /// Listens to the stations (and follows their changes) while <paramref name="shown"/>: the page is the current one
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
            tick.Stop();
        }
    }

    /// <summary>Gives the station a name of the user's; null when saved, else what went wrong (the dialog stays open).</summary>
    public string? Rename(StationItem item, string text) => stations.Rename(item.Address, text);

    /// <summary>Keeps a 1.0 station's ID; null when saved, else what went wrong (the dialog stays open).</summary>
    public string? SetId(StationItem item, string text) => stations.SetId(item.Address, text);

    internal void SetSelected(StationItem item, bool selected) => stations.SetSelected(item.Address, selected);

    internal void Switch(StationItem item, StationCommand command) =>
        _ = stations.SwitchAsync(item.Address, command, CancellationToken.None);

    internal void Identify(StationItem item) => _ = stations.IdentifyAsync(item.Address, CancellationToken.None);

    internal void Forget(StationItem item) => stations.Forget(item.Address);

    /// <summary>Starts looking afresh.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan() => stations.Rescan();

    private bool CanScan() => BluetoothReady && !IsLooking;

    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOn() => _ = stations.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);

    /// <summary>Off as "Off means" in the App settings says.</summary>
    [RelayCommand(CanExecute = nameof(CanSwitchSelected))]
    private void SwitchOff() => _ = stations.SwitchSelectedAsync(stations.OffCommand(), CancellationToken.None);

    /// <summary>On and Off: Bluetooth is ready, a station is selected and no command is being sent.</summary>
    private bool CanSwitchSelected() => BluetoothReady && AnySelected && !IsRunning;

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

        if (listening is not null && views.Any(v => v.Seen))
        {
            if (!tick.IsRunning)
            {
                tick.Start();
            }
        }
        else
        {
            tick.Stop();
        }
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
