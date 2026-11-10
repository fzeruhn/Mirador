using Microsoft.Extensions.Time.Testing;
using Mirador.Core.BaseStations;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Core.Session;
using Mirador.Core.Tests.Session;

namespace Mirador.Core.Tests.BaseStations;

public class StationManagerTests
{
    private const ulong Left = 0xCE36CF38E9CB;
    private const string LeftName = "LHB-6889F521";
    private const ulong Right = 0xC658F77FDCD9;
    private const string RightName = "LHB-2649BF2A";
    private const ulong Old = 0x010203040506;
    private const string OldName = "HTC BS 1A2B3C";

    private static readonly TimeSpan Limit = StationHarness.Limit;
    private static readonly string LeftAddress = StationProtocol.FormatAddress(Left);
    private static readonly string RightAddress = StationProtocol.FormatAddress(Right);
    private static readonly string OldAddress = StationProtocol.FormatAddress(Old);

    [Fact]
    public void New_stations_are_added_unselected()
    {
        using var h = new StationHarness();
        var changed = 0;
        h.Manager.Changed += () => Interlocked.Increment(ref changed);

        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        var view = Assert.Single(h.Manager.Stations);
        Assert.Equal(new KnownStation(LeftAddress, LeftName), view.Station);
        Assert.Equal([new KnownStation(LeftAddress, LeftName)], new StationStore(h.Path).Load());
        Assert.True(changed > 0);
        Assert.Empty(h.Radio.Connections);
    }

    [Fact]
    public void Adverts_give_states_and_seen()
    {
        using var h = new StationHarness();
        using var listen = h.Manager.Listen();

        h.Hear(Left, LeftName, 0x00);
        Assert.Equal(StationPower.Asleep, h.View(Left).Power);
        Assert.True(h.View(Left).Seen);
        h.Hear(Left, LeftName, 0x01);
        Assert.Equal(StationPower.Waking, h.View(Left).Power);
        h.Hear(Left, LeftName, 0x02);
        Assert.Equal(StationPower.Waking, h.View(Left).Power);
        h.Hear(Left, LeftName, 0x09);
        Assert.Equal(StationPower.Unknown, h.View(Left).Power);
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal(StationPower.On, h.View(Left).Power);

        // An advert without Valve's data says nothing about the state; a 1.0 station never says.
        h.Hear(Left, LeftName, null);
        Assert.Equal(StationPower.On, h.View(Left).Power);
        h.Hear(Old, OldName, null);
        Assert.Equal(StationPower.Unknown, h.View(Old).Power);
        Assert.True(h.View(Old).Seen);

        // The name is refreshed and saved.
        h.Hear(Left, "LHB-AAAAAAAA", 0x03);
        Assert.Equal("LHB-AAAAAAAA", h.View(Left).Station.Name);
        Assert.Contains(new StationStore(h.Path).Load(), s => s.Name == "LHB-AAAAAAAA");

        // Seen doesn't lapse with time: only a scan that doesn't hear a station takes it away.
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(h.View(Left).Seen);
        Assert.True(h.View(Old).Seen);
        Assert.Empty(h.Radio.Connections);
    }

    [Fact]
    public void Selection_and_names_survive_a_new_manager()
    {
        using var h = new StationHarness();
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Old, OldName, null);
        }

        h.Manager.SetSelected(LeftAddress, true);
        Assert.Null(h.Manager.Rename(LeftAddress, "Desk"));
        Assert.Null(h.Manager.SetId(OldAddress, "AABBCCDD"));

        var again = h.NewManager();

        Assert.Equal(
            [new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"), new KnownStation(OldAddress, OldName, Id: "AABBCCDD")],
            again.Stations.Select(s => s.Station));
        Assert.All(again.Stations, s => Assert.False(s.Seen));
    }

    [Fact]
    public async Task Unseen_selected_station_keeps_its_check()
    {
        using var h = new StationHarness();
        var manager = h.Manager;

        // Written after the manager was made: the list is read at first use.
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));

        var view = Assert.Single(manager.Stations);
        Assert.True(view.Station.Selected);
        Assert.False(view.Seen);
        Assert.Equal(StationPower.Unknown, view.Power);
        Assert.Null(view.Problem);

        // Not heard in a scan (unplugged, say): still listed, still checked.
        var scan = manager.ScanAsync(CancellationToken.None);
        h.Hear(Right, RightName);
        await h.EndScan(scan);

        Assert.True(h.View(Left).Station.Selected);
        Assert.False(h.View(Left).Seen);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, false), manager.Summary);
    }

    [Fact]
    public async Task Nothing_is_sent_or_connected_to_unselected()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
            Assert.Empty(h.Radio.Connections);
        }

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.SwitchSelectedAsync(h.Manager.OffCommand(), CancellationToken.None).WaitAsync(Limit);
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.QueueDrained();
        Assert.Equal(3, h.Radio.WritesTo(Left).Length);
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 4).WaitAsync(Limit);
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        Assert.Equal(["01", "00", "01", "00", "00"], h.Radio.WritesTo(Left));
        Assert.Empty(h.Radio.ConnectionsTo(Right));
        Assert.All(h.Radio.Connections, c => Assert.Equal(LeftAddress, c.Address));
    }

    [Fact]
    public async Task V2_commands_write_their_bytes()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName, random: false);
        }

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.SwitchSelectedAsync(StationCommand.Standby, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(
            [
                new FakeStationRadio.Write(LeftAddress, StationProtocol.V2Service, StationProtocol.V2PowerCharacteristic, "01"),
                new FakeStationRadio.Write(LeftAddress, StationProtocol.V2Service, StationProtocol.V2PowerCharacteristic, "00"),
                new FakeStationRadio.Write(LeftAddress, StationProtocol.V2Service, StationProtocol.V2PowerCharacteristic, "02"),
            ],
            h.Radio.Writes);
        Assert.Equal(
            [
                $"connect {LeftAddress}", $"write {LeftAddress} 01", $"close {LeftAddress}",
                $"connect {LeftAddress}", $"write {LeftAddress} 00", $"close {LeftAddress}",
                $"connect {LeftAddress}", $"write {LeftAddress} 02", $"close {LeftAddress}",
            ],
            h.Radio.Steps);
        Assert.All(h.Radio.Connections, c => Assert.True(c.RandomAddress));
        Assert.Contains($"Base stations: On {LeftName} ({LeftAddress}): done", h.Logged);
        Assert.Contains($"Base stations: Sleep {LeftName} ({LeftAddress}): done", h.Logged);

        // One station from its own menu, selected or not; with the address type its advert had.
        await h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(["01"], h.Radio.WritesTo(Right));
        Assert.False(Assert.Single(h.Radio.ConnectionsTo(Right)).RandomAddress);
        Assert.Equal(0, h.Radio.OpenLinks);
    }

    [Fact]
    public async Task V1_off_with_and_without_id()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(OldAddress, OldName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Old, OldName, null);
        }

        await h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(
            new FakeStationRadio.Write(OldAddress, StationProtocol.V1Service, StationProtocol.V1PowerCharacteristic, Convert.ToHexString(StationProtocol.V1Write(StationCommand.Sleep, null))),
            Assert.Single(h.Radio.Writes));
        Assert.Equal(StationPower.Asleep, h.View(Old).Power);

        Assert.Null(h.Manager.SetId(OldAddress, "AABBCCDD"));
        await h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(
            [
                Convert.ToHexString(StationProtocol.V1Write(StationCommand.Sleep, null)),
                Convert.ToHexString(StationProtocol.V1Write(StationCommand.Sleep, 0xAABBCCDD)),
                Convert.ToHexString(StationProtocol.V1Write(StationCommand.On, null)),
            ],
            h.Radio.WritesTo(Old));
        Assert.All(h.Radio.Writes, w => Assert.Equal(StationProtocol.V1PowerCharacteristic, w.Characteristic));
        Assert.Equal(StationPower.On, h.View(Old).Power);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);
    }

    [Fact]
    public async Task V1_standby_is_sleep()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(OldAddress, OldName, Selected: true, Id: "AABBCCDD"));
        using (h.Manager.Listen())
        {
            h.Hear(Old, OldName, null);
        }

        await h.Manager.SwitchAsync(OldAddress, StationCommand.Standby, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal([Convert.ToHexString(StationProtocol.V1Write(StationCommand.Sleep, 0xAABBCCDD))], h.Radio.WritesTo(Old));
        Assert.Equal(StationPower.Asleep, h.View(Old).Power);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, false), h.Manager.Summary);
    }

    [Fact]
    public async Task Off_is_always_sleep()
    {
        // Even from a settings file of before 2026-11-07 that chose standby.
        using var h = new StationHarness();
        Assert.Equal(StationCommand.Sleep, h.Manager.OffCommand());
        File.WriteAllText(h.Dir.File("settings.json"), """{"baseStationsOff": "Standby", "baseStationsOffAtMiradorQuit": true}""");
        h.Settings = new AppSettingsStore(h.Dir.File("settings.json")).Load() with { BaseStationsState = StationStateMode.Live };
        Assert.Equal(StationCommand.Sleep, h.Manager.OffCommand());
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        // Monado's stop.
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
        await h.QueueDrained();

        // Windows ending the session while Monado runs.
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 2).WaitAsync(Limit);
        h.Manager.EndingNow(StationHarness.Profile(true), null);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 3).WaitAsync(Limit);
        await h.QueueDrained();

        // Mirador quitting.
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 4).WaitAsync(Limit);
        await h.QueueDrained();
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        Assert.Equal(["00", "01", "00", "01", "00"], h.Radio.WritesTo(Left));
        Assert.Contains("Base stations: switching off as Mirador quits", h.Logged);
    }

    [Fact]
    public async Task Unreachable_station_is_named_and_the_others_switched_first()
    {
        using var h = new StationHarness();
        h.Known(
            new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"),
            new KnownStation(RightAddress, RightName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        h.Radio.Unreachable(Left);

        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.PassPauses(2);
        await on.WaitAsync(Limit);

        // Desk comes first by name: it is tried, the other one is switched, then Desk twice more, a second apart.
        Assert.Equal(
            [
                $"connect {LeftAddress}",
                $"connect {RightAddress}", $"write {RightAddress} 01", $"close {RightAddress}",
                $"connect {LeftAddress}",
                $"connect {LeftAddress}",
            ],
            h.Radio.Steps);
        Assert.Equal(StationManager.Tries, h.Radio.ConnectionsTo(Left).Length);
        Assert.Equal("didn't answer", h.View(Left).Problem);
        Assert.Null(h.View(Right).Problem);

        // Both were on already (their adverts say so); Desk just didn't take the On.
        Assert.Equal(new StationsSummary("2/2 Powered · Desk didn't answer.", StatusLevel.Warning, false), h.Manager.Summary);
        Assert.Contains($"Base stations: On {RightName} ({RightAddress}): done", h.Logged);
        Assert.Contains($"Base stations: On Desk ({LeftAddress}): didn't answer (Windows couldn't connect to the base station over Bluetooth)", h.Logged);

        // A success clears it.
        h.Radio.Reachable(Left);
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        Assert.Null(h.View(Left).Problem);
        Assert.Equal(new StationsSummary("2/2 Powered", StatusLevel.Ok, false), h.Manager.Summary);
    }

    [Fact]
    public async Task Slow_try_is_given_up_and_its_late_link_closed()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        var hold = h.Radio.Hold(Left);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        h.Clock.Advance(StationManager.TryLimit);

        // Windows answers after Mirador gave up on the try: the link is closed unused.
        await h.Time.Set(StationManager.TryPause).WaitAsync(Limit);
        hold.SetResult();
        await h.Radio.Until(r => r.Steps.Contains($"close {LeftAddress}")).WaitAsync(Limit);
        await h.PassPauses(1);
        await on.WaitAsync(Limit);

        Assert.Equal(
            [$"connect {LeftAddress}", $"close {LeftAddress}", $"connect {LeftAddress}", $"write {LeftAddress} 01", $"close {LeftAddress}"],
            h.Radio.Steps);
        Assert.Equal(0, h.Radio.OpenLinks);
        Assert.Null(h.View(Left).Problem);
    }

    [Fact]
    public async Task Command_listens_first_for_stations_not_heard_yet()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName, Selected: true));

        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Time.Set(StationManager.FindTime).WaitAsync(Limit);

        Assert.Empty(h.Radio.Connections);
        Assert.True(h.Radio.Watching);
        h.Hear(Left, LeftName, random: false);

        // Right is never heard: after the 5 s Mirador tries anyway, with a random address as base stations have.
        await h.Time.Set(StationManager.FindTime, 2).WaitAsync(Limit);
        Assert.Empty(h.Radio.Connections);
        h.Clock.Advance(StationManager.FindTime);
        await on.WaitAsync(Limit);

        Assert.Equal([new FakeStationRadio.Connection(RightAddress, true), new FakeStationRadio.Connection(LeftAddress, false)], h.Radio.Connections);
    }

    [Fact]
    public async Task Watches_while_listening_and_for_a_while_after_a_command()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        Assert.False(h.Radio.Watching);

        var first = h.Manager.Listen();
        var second = h.Manager.Listen();
        Assert.True(h.Radio.Watching);
        h.Hear(Left, LeftName);
        first.Dispose();
        first.Dispose();
        Assert.True(h.Radio.Watching);
        second.Dispose();
        Assert.True(h.Radio.Watching); // "Constant" listens with or without a page's listen
        h.WhenRelevant();
        Assert.False(h.Radio.Watching);
        Assert.False(h.Manager.Looking);

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        Assert.True(h.Radio.Watching);
        h.Hear(Left, LeftName, 0x01);
        h.Clock.Advance(StationManager.SettleTime - TimeSpan.FromSeconds(1));
        Assert.True(h.Radio.Watching);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(h.Radio.Watching);
        Assert.Equal(StationPower.Waking, h.View(Left).Power);
    }

    [Fact]
    public async Task Bluetooth_off_sends_nothing()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        h.Radio.State = BluetoothState.Off;

        Assert.Equal(BluetoothState.Off, h.Manager.Bluetooth);
        Assert.Equal(new StationsSummary("Bluetooth is off.", StatusLevel.Warning, false), h.Manager.Summary);
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.SwitchAsync(LeftAddress, StationCommand.Sleep, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.IdentifyAsync(LeftAddress, CancellationToken.None).WaitAsync(Limit);
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.QueueDrained();
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        await h.QueueDrained();
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        h.Radio.State = BluetoothState.Missing;
        Assert.Equal(new StationsSummary("This PC has no Bluetooth.", StatusLevel.Info, false), h.Manager.Summary);
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        Assert.Empty(h.Radio.Connections);
        Assert.Null(h.View(Left).Problem);
        Assert.Contains("Base stations: On not sent (Bluetooth is off)", h.Logged);
        Assert.Contains("Base stations: On not sent (this PC has no Bluetooth)", h.Logged);
    }

    [Fact]
    public async Task Radio_off_mid_command_is_a_problem_not_an_exception()
    {
        using var h = new StationHarness();
        h.Known(
            new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"),
            new KnownStation(RightAddress, RightName, Selected: true, CustomName: "Shelf"));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        h.Radio.Unreachable(Left, () => new InvalidOperationException("The radio went away."));
        h.Radio.OnConnect = _ => h.Radio.State = BluetoothState.Off;

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        // Desk was tried and is named; Shelf was never tried, so nothing is said against it.
        Assert.Single(h.Radio.ConnectionsTo(Left));
        Assert.Empty(h.Radio.ConnectionsTo(Right));
        Assert.Equal("didn't answer", h.View(Left).Problem);
        Assert.Null(h.View(Right).Problem);
        Assert.Equal(new StationsSummary("Bluetooth is off.", StatusLevel.Warning, false), h.Manager.Summary);
        Assert.Contains($"Base stations: On Desk ({LeftAddress}): didn't answer (The radio went away)", h.Logged);
        Assert.Contains($"Base stations: On Shelf ({RightAddress}): not sent (Bluetooth is off)", h.Logged);

        h.Radio.OnConnect = null;
        h.Radio.State = BluetoothState.Ready;

        Assert.Equal(new StationsSummary("2/2 Powered · Desk didn't answer.", StatusLevel.Warning, false), h.Manager.Summary);
    }

    [Fact]
    public async Task Summary_follows_the_radio_coming_back()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        var changed = 0;
        h.Manager.Changed += () => Interlocked.Increment(ref changed);

        h.Radio.State = BluetoothState.Off;
        Assert.Equal(1, changed);
        Assert.Equal(new StationsSummary("Bluetooth is off.", StatusLevel.Warning, false), h.Manager.Summary);
        h.Radio.State = BluetoothState.Missing;
        Assert.Equal(2, changed);
        Assert.Equal(new StationsSummary("This PC has no Bluetooth.", StatusLevel.Info, false), h.Manager.Summary);
        h.Radio.State = BluetoothState.Ready;
        Assert.Equal(3, changed);
        Assert.Equal(BluetoothState.Ready, h.Manager.Bluetooth);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, false), h.Manager.Summary);

        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(["01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Started_switches_on_only_with_the_setting_and_the_profile()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        var dreamAir = new FakeModule(new Steps()) { UsesSteamVrLighthouse = true };
        var other = new FakeModule(new Steps()) { UsesSteamVrLighthouse = false };

        h.Manager.Started(StationHarness.Profile(null), null);
        h.Manager.Started(StationHarness.Profile(null), other);
        h.Manager.Started(StationHarness.Profile(false), dreamAir);
        h.Settings = StationHarness.Live with { BaseStationsOnAtStart = false };
        h.Manager.Started(StationHarness.Profile(true), dreamAir);
        await h.QueueDrained();

        Assert.Empty(h.Radio.Connections);

        h.Settings = StationHarness.Live;
        h.Manager.Started(StationHarness.Profile(null), dreamAir);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 2).WaitAsync(Limit);

        Assert.Equal(["01", "01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Started_skips_nothing_else_returns_at_once()
    {
        using var h = new StationHarness();
        h.Radio.NeverAnswers = true;
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        ISessionHook hook = h.Manager;

        await Task.Run(() =>
        {
            hook.Started(StationHarness.Profile(true), null);
            hook.Ended(StationHarness.Profile(true), null);
            hook.Started(StationHarness.Profile(true), null);
            hook.Ended(StationHarness.Profile(true), null);
        }).WaitAsync(Limit);

        // The On did go out: after listening 5 s for the station Mirador connects, and the connection never ends.
        await h.Time.Set(StationManager.FindTime, 3).WaitAsync(Limit);
        h.Clock.Advance(StationManager.FindTime);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        Assert.Equal(StationCommand.On, h.Manager.Running);

        await Task.Run(() =>
        {
            hook.Started(StationHarness.Profile(true), null);
            hook.Ended(StationHarness.Profile(true), null);
        }).WaitAsync(Limit);
    }

    [Fact]
    public async Task Ended_waits_five_seconds()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay - TimeSpan.FromMilliseconds(1));
        await h.QueueDrained();

        Assert.Empty(h.Radio.Connections);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Ended_without_the_setting_or_the_profile_switches_nothing_off()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        h.Manager.Ended(StationHarness.Profile(false), null);
        h.Settings = StationHarness.Live with { BaseStationsOffAtStop = false };
        h.Manager.Ended(StationHarness.Profile(true), null);
        h.Clock.Advance(StationManager.OffDelay * 2);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        Assert.Empty(h.Radio.Connections);
    }

    [Fact]
    public async Task Started_within_the_delay_cancels_the_off()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(4));
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.QueueDrained();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));

        // With "Power on when Monado starts" off too.
        h.Settings = StationHarness.Live with { BaseStationsOnAtStart = false };
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay, 2).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(4));
        h.Manager.Started(StationHarness.Profile(true), null);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Manual_command_cancels_the_delayed_off()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.QueueDrained();

        Assert.Empty(h.Radio.ConnectionsTo(Left));

        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay, 2).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Queued_off_is_cancelled_too()
    {
        using var h = new StationHarness();
        h.Settings = StationHarness.Live with { BaseStationsOnAtStart = false };
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        // The delayed off is queued behind a command that is still sending; a Start cancels it.
        var hold = h.Radio.Hold(Right);
        var right = h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None);
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        h.Manager.Started(StationHarness.Profile(true), null);
        hold.SetResult();
        await right.WaitAsync(Limit);
        await h.QueueDrained();

        Assert.Empty(h.Radio.ConnectionsTo(Left));
        Assert.Contains("Base stations: Sleep not sent (Monado started again)", h.Logged);

        // An Off the user asked for is cancelled by a command for the same stations asked for after it; its task still
        // completes.
        hold = h.Radio.Hold(Right);
        right = h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.ConnectionsTo(Right).Length == 2).WaitAsync(Limit);
        var off = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await off.WaitAsync(Limit);
        hold.SetResult();
        await Task.WhenAll(right, on).WaitAsync(Limit);

        Assert.Equal(["01"], h.Radio.WritesTo(Left));

        // One already sending isn't cancelled.
        hold = h.Radio.Hold(Left);
        h.Settings = StationHarness.Live;
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay, 2).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        await h.Radio.Until(r => r.ConnectionsTo(Left).Length == 2).WaitAsync(Limit);
        h.Manager.Started(StationHarness.Profile(true), null);
        hold.SetResult();
        await h.Radio.Until(r => r.WritesTo(Left).Length == 3).WaitAsync(Limit);

        Assert.Equal(["01", "00", "01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Single_station_offs_are_all_sent()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName), new KnownStation(RightAddress, RightName), new KnownStation(OldAddress, OldName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
            h.Hear(Old, OldName, null);
        }

        var hold = h.Radio.Hold(Left);
        var left = h.Manager.SwitchAsync(LeftAddress, StationCommand.Sleep, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        var right = h.Manager.SwitchAsync(RightAddress, StationCommand.Sleep, CancellationToken.None);
        var old = h.Manager.SwitchAsync(OldAddress, StationCommand.Sleep, CancellationToken.None);
        hold.SetResult();
        await Task.WhenAll(left, right, old).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Equal(["00"], h.Radio.WritesTo(Right));
        Assert.Equal([Convert.ToHexString(StationProtocol.V1Write(StationCommand.Sleep, null))], h.Radio.WritesTo(Old));
    }

    [Fact]
    public async Task Switching_one_station_on_keeps_a_queued_off_of_the_selected()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        var hold = h.Radio.Hold(Right);
        var first = h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        var off = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        var on = h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None);
        hold.SetResult();
        await Task.WhenAll(first, off, on).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Equal(["01", "01"], h.Radio.WritesTo(Right));
    }

    [Fact]
    public async Task Off_pending_while_the_session_off_waits_is_queued_or_sends()
    {
        using var h = new StationHarness();
        h.Settings = StationHarness.Live with { BaseStationsOnAtStart = false };
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        Assert.False(h.Manager.OffPending);
        h.Manager.Ended(StationHarness.Profile(true), null);
        Assert.True(h.Manager.OffPending);
        h.Manager.Started(StationHarness.Profile(true), null);
        Assert.False(h.Manager.OffPending);

        // Waiting, then queued and sending, then done.
        var hold = h.Radio.Hold(Left);
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay, 2).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        Assert.True(h.Manager.OffPending);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        Assert.True(h.Manager.OffPending);
        hold.SetResult();
        await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
        await h.QueueDrained();
        Assert.False(h.Manager.OffPending);

        // The user's own Off isn't the session's.
        hold = h.Radio.Hold(Left);
        var off = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == 2).WaitAsync(Limit);
        Assert.False(h.Manager.OffPending);
        hold.SetResult();
        await off.WaitAsync(Limit);

        // Flushed: sent at once, then nothing is pending.
        h.Manager.Ended(StationHarness.Profile(true), null);
        Assert.True(h.Manager.OffPending);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);
        Assert.False(h.Manager.OffPending);

        h.Settings = StationHarness.Live with { BaseStationsOffAtStop = false };
        h.Manager.Ended(StationHarness.Profile(true), null);
        Assert.False(h.Manager.OffPending);
    }

    [Fact]
    public async Task Flush_stops_a_running_on()
    {
        using var h = new StationHarness();
        h.Known(
            new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"),
            new KnownStation(RightAddress, RightName, Selected: true, CustomName: "Shelf"));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        // Monado's On is stuck at Desk, which never answers, when Mirador quits after the session ended.
        h.Radio.Hang(Left);
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        h.Manager.Ended(StationHarness.Profile(true), null);
        var flush = h.Manager.FlushAsync(TimeSpan.FromSeconds(15));

        // The On ends; the off tries Desk (10 s), then switches Shelf off, well within the 15 s.
        await h.Time.Set(StationManager.TryLimit, 2).WaitAsync(Limit);
        h.Clock.Advance(StationManager.TryLimit);
        await h.Radio.Until(r => r.WritesTo(Right).Length == 1).WaitAsync(Limit);
        await h.PassPauses(1);
        await h.Time.Set(StationManager.TryLimit, 3).WaitAsync(Limit);
        Assert.False(flush.IsCompleted);
        h.Clock.Advance(TimeSpan.FromSeconds(4));
        await flush.WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Right));
        Assert.Contains("Base stations: On stopped (Mirador is quitting)", h.Logged);
        Assert.Null(h.View(Right).Problem);
    }

    [Fact]
    public async Task A_scan_while_Mirador_still_listens_is_looking()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Clock.Advance(StationManager.ScanTime + TimeSpan.FromSeconds(2));
        Assert.True(h.Radio.Watching);
        Assert.False(h.Manager.Looking);

        // A page shown isn't a scan, live or not.
        using var listen = h.Manager.Listen();
        Assert.False(h.Manager.Looking);

        // A scan uses the watch that is open, and is looking for its 8 s.
        var watches = h.Radio.WatchCalls;
        var changed = 0;
        h.Manager.Changed += () => Interlocked.Increment(ref changed);
        var scan = h.Manager.ScanAsync(CancellationToken.None);
        Assert.True(h.Manager.Looking);
        Assert.True(changed > 0);
        h.Clock.Advance(StationManager.ScanTime - TimeSpan.FromMilliseconds(1));
        Assert.True(h.Manager.Looking);
        changed = 0;
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await scan.WaitAsync(Limit);
        Assert.False(h.Manager.Looking);
        Assert.True(changed > 0);
        Assert.True(h.Radio.Watching);
        Assert.Equal(watches, h.Radio.WatchCalls);
    }

    [Fact]
    public void Unreadable_list_is_read_again_after_five_seconds()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));

        using (new FileStream(h.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(h.Manager.Stations);
            using (h.Manager.Listen())
            {
                h.Hear(Left, LeftName);
                h.Hear(Left, LeftName);
            }
        }

        // Readable again, but not read again until five seconds after it failed.
        Assert.Empty(h.Manager.Stations);
        Assert.Single(h.Logged, line => line.StartsWith("Base stations: Mirador couldn't read its base station list (", StringComparison.Ordinal));
        h.Clock.Advance(StationManager.LoadRetry);

        Assert.Equal([new KnownStation(LeftAddress, LeftName, Selected: true)], h.Manager.Stations.Select(s => s.Station));
    }

    [Fact]
    public async Task Throwing_subscriber_stays_inside()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        var after = 0;
        h.Manager.Changed += () => throw new InvalidOperationException("The page broke.");
        h.Manager.Changed += () => Interlocked.Increment(ref after);

        // A watch starting, an advert, a scan, the radio, the list and a command each raise it.
        var listen = h.Manager.Listen();
        h.Hear(Left, LeftName);
        await h.EndScan(h.Manager.ScanAsync(CancellationToken.None));
        h.Manager.SetSelected(LeftAddress, true);
        h.Radio.State = BluetoothState.Off;
        h.Radio.State = BluetoothState.Ready;
        listen.Dispose();
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
        Assert.Contains("Base stations: showing a change failed (The page broke)", h.Logged);
        Assert.True(after >= 6);
    }

    [Fact]
    public async Task Throwing_watch_stays_inside()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        h.Radio.WatchFailure = () => new InvalidOperationException("No watcher.");

        var first = h.Manager.Listen();

        Assert.False(h.Radio.Watching);
        Assert.False(h.Manager.Looking);
        Assert.Contains("Base stations: Mirador couldn't listen for the base stations (No watcher)", h.Logged);

        // The next listener starts it; a failing stop is logged.
        h.Radio.WatchFailure = null;
        var second = h.Manager.Listen();
        Assert.True(h.Radio.Watching);
        h.Hear(Left, LeftName);
        h.Radio.UnwatchFailure = () => new InvalidOperationException("Stuck.");
        first.Dispose();
        second.Dispose();
        h.WhenRelevant();
        Assert.Contains("Base stations: listening didn't stop cleanly (Stuck)", h.Logged);
        Assert.False(h.Manager.Looking);

        // A command still works, failing watch and all.
        h.Radio.UnwatchFailure = null;
        h.Radio.WatchFailure = () => new InvalidOperationException("No watcher.");
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(["01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Cancelled_while_queued_is_dropped()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        var hold = h.Radio.Hold(Left);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        using var cancel = new CancellationTokenSource();
        var identify = h.Manager.IdentifyAsync(LeftAddress, cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => identify.WaitAsync(Limit));
        hold.SetResult();
        await on.WaitAsync(Limit);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Flush_sends_the_pending_off_at_once()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));

        // Nothing pending: it returns at once, and the timer that was cancelled sends nothing later.
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        await h.QueueDrained();

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Flush_drops_a_queued_on()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        var hold = h.Radio.Hold(Right);
        var right = h.Manager.SwitchAsync(RightAddress, StationCommand.Sleep, CancellationToken.None);
        await h.Radio.Until(r => r.ConnectionsTo(Right).Length == 1).WaitAsync(Limit);
        h.Manager.Started(StationHarness.Profile(true), null);
        var userOn = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        h.Manager.Ended(StationHarness.Profile(true), null);
        var flush = h.Manager.FlushAsync(TimeSpan.FromSeconds(15));
        await userOn.WaitAsync(Limit);
        Assert.False(flush.IsCompleted);
        hold.SetResult();
        await flush.WaitAsync(Limit);

        Assert.True(right.IsCompleted);
        Assert.Equal(["00"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Flush_gives_up_at_the_limit()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        h.Radio.Hang(Left);
        h.Manager.Ended(StationHarness.Profile(true), null);
        var flush = h.Manager.FlushAsync(TimeSpan.FromSeconds(15));

        // The first try is given up at 10 s and the second begins at 11 s; Flush still waits.
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        h.Clock.Advance(StationManager.TryLimit);
        await h.PassPauses(1);
        await h.Time.Set(StationManager.TryLimit, 2).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        await h.Radio.Until(r => r.Connections.Length == 2).WaitAsync(Limit);
        Assert.False(flush.IsCompleted);
        h.Clock.Advance(TimeSpan.FromSeconds(1));

        await flush.WaitAsync(Limit);
        Assert.Contains("Base stations: Mirador stopped waiting for the base stations after 15 s", h.Logged);
    }

    [Fact]
    public async Task Deselected_during_a_command_is_skipped()
    {
        using var h = new StationHarness();
        h.Known(
            new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"),
            new KnownStation(RightAddress, RightName, Selected: true, CustomName: "Shelf"),
            new KnownStation(OldAddress, OldName, Selected: true, CustomName: "Tower"));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
            h.Hear(Old, OldName, null);
        }

        var hold = h.Radio.Hold(Left);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        h.Manager.SetSelected(RightAddress, false);
        h.Manager.Forget(OldAddress);
        hold.SetResult();
        await on.WaitAsync(Limit);

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
        Assert.Empty(h.Radio.ConnectionsTo(Right));
        Assert.Empty(h.Radio.ConnectionsTo(Old));

        // Deselected between rounds: not tried again, and not named.
        h.Manager.SetSelected(RightAddress, true);
        h.Radio.Unreachable(Right);
        on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Time.Set(StationManager.TryPause).WaitAsync(Limit);
        h.Manager.SetSelected(RightAddress, false);
        h.Clock.Advance(StationManager.TryPause);
        await on.WaitAsync(Limit);

        Assert.Single(h.Radio.ConnectionsTo(Right));
        Assert.Null(h.View(Right).Problem);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);
    }

    [Fact]
    public async Task Commands_run_one_at_a_time_in_order()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        var hold = h.Radio.Hold(Left);
        var sleep = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        var standby = h.Manager.SwitchAsync(LeftAddress, StationCommand.Standby, CancellationToken.None);
        var identify = h.Manager.IdentifyAsync(LeftAddress, CancellationToken.None);

        Assert.Single(h.Radio.Connections);
        Assert.False(sleep.IsCompleted || on.IsCompleted || standby.IsCompleted || identify.IsCompleted);
        hold.SetResult();
        await Task.WhenAll(sleep, on, standby, identify).WaitAsync(Limit);

        Assert.Equal(["00", "01", "02", "01"], h.Radio.WritesTo(Left));
        Assert.Equal(StationProtocol.V2IdentifyCharacteristic, h.Radio.Writes[^1].Characteristic);
        Assert.Equal(1, h.Radio.MostOpenLinks);
    }

    [Fact]
    public async Task Identify_only_while_on()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName), new KnownStation(OldAddress, OldName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x03);
        h.Hear(Old, OldName, null);

        await h.Manager.IdentifyAsync(LeftAddress, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Left, LeftName, 0x02);
        await h.Manager.IdentifyAsync(LeftAddress, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.IdentifyAsync(OldAddress, CancellationToken.None).WaitAsync(Limit);

        Assert.Empty(h.Radio.Connections);

        h.Hear(Left, LeftName, 0x03);
        await h.Manager.IdentifyAsync(LeftAddress, CancellationToken.None).WaitAsync(Limit);
        await h.Manager.IdentifyAsync(RightAddress, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(
            [
                new FakeStationRadio.Write(LeftAddress, StationProtocol.V2Service, StationProtocol.V2IdentifyCharacteristic, "01"),
                new FakeStationRadio.Write(RightAddress, StationProtocol.V2Service, StationProtocol.V2IdentifyCharacteristic, "01"),
            ],
            h.Radio.Writes);
        Assert.Contains($"Base stations: Identify {LeftName} ({LeftAddress}): done", h.Logged);
    }

    [Fact]
    public async Task Summary_counts_powered()
    {
        using var h = new StationHarness();
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);
        h.Hear(Old, OldName, null);

        Assert.Equal(new StationsSummary("None selected: choose yours on the Lighthouse page.", StatusLevel.Info, false), h.Manager.Summary);
        h.Manager.SetSelected(LeftAddress, true);
        h.Manager.SetSelected(RightAddress, true);
        Assert.Equal(new StationsSummary("0/2 Powered", StatusLevel.Info, false), h.Manager.Summary);
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, false), h.Manager.Summary);
        h.Hear(Right, RightName, 0x03);
        Assert.Equal(new StationsSummary("2/2 Powered", StatusLevel.Ok, false), h.Manager.Summary);

        // Only on counts: not a state Mirador can't read.
        h.Hear(Right, RightName, 0x09);
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, false), h.Manager.Summary);

        // A station that isn't selected doesn't count.
        h.Manager.SetSelected(RightAddress, false);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);

        // A 1.0 station by what Mirador last told it.
        h.Manager.SetSelected(OldAddress, true);
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, false), h.Manager.Summary);
        await h.Manager.SwitchAsync(OldAddress, StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(new StationsSummary("2/2 Powered", StatusLevel.Ok, false), h.Manager.Summary);
        await h.Manager.SwitchAsync(OldAddress, StationCommand.Sleep, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, false), h.Manager.Summary);

        // A selected station not heard in the last scan doesn't count as powered, whatever it said last; it still counts
        // among the selected. Heard again, it counts again.
        h.Manager.SetSelected(OldAddress, false);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);
        await h.EndScan(h.Manager.ScanAsync(CancellationToken.None));
        Assert.False(h.View(Left).Seen);
        Assert.Equal(StationPower.On, h.View(Left).Power);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, false), h.Manager.Summary);
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);
    }

    [Fact]
    public async Task Summary_busy_while_running_or_waking()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, false), h.Manager.Summary);

        // A selected station waking up by its advert; one that isn't selected doesn't count.
        h.Hear(Right, RightName, 0x01);
        Assert.False(h.Manager.Summary.Busy);
        h.Hear(Left, LeftName, 0x01);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, true), h.Manager.Summary);
        h.Hear(Left, LeftName, 0x02);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, true), h.Manager.Summary);
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);

        // A command being sent, whichever it is and whoever it is for; not Identify, which changes nothing.
        var tries = 0;
        foreach (var (command, address) in new[] { (StationCommand.Sleep, LeftAddress), (StationCommand.Sleep, RightAddress), (StationCommand.Standby, LeftAddress) })
        {
            var station = address == LeftAddress ? Left : Right;
            var hold = h.Radio.Hold(station);
            var running = h.Manager.SwitchAsync(address, command, CancellationToken.None);
            tries++;
            await h.Radio.Until(r => r.Connections.Length == tries).WaitAsync(Limit);
            Assert.Equal(command, h.Manager.Running);
            Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, true), h.Manager.Summary);
            hold.SetResult();
            await running.WaitAsync(Limit);
            Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);
        }

        var identifyHold = h.Radio.Hold(Left);
        var identify = h.Manager.IdentifyAsync(LeftAddress, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == tries + 1).WaitAsync(Limit);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);
        identifyHold.SetResult();
        await identify.WaitAsync(Limit);

        // Right after an On reached it, while its adverts still lag behind, until it is on or the On's time is up.
        h.Hear(Left, LeftName, 0x00);
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, true), h.Manager.Summary);
        h.Clock.Advance(StationManager.SettleTime);
        Assert.Equal(new StationsSummary("0/1 Powered", StatusLevel.Info, false), h.Manager.Summary);
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        Assert.True(h.Manager.Summary.Busy);
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);

        // Bluetooth's state comes first, with its own icon.
        var before = h.Radio.Connections.Length;
        var last = h.Radio.Hold(Left);
        var off = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        await h.Radio.Until(r => r.Connections.Length == before + 1).WaitAsync(Limit);
        h.Radio.State = BluetoothState.Off;
        Assert.Equal(new StationsSummary("Bluetooth is off.", StatusLevel.Warning, false), h.Manager.Summary);
        h.Radio.State = BluetoothState.Ready;
        last.SetResult();
        await off.WaitAsync(Limit);
    }

    [Fact]
    public async Task Summary_names_who_didnt_answer()
    {
        using var h = new StationHarness();
        h.Known(
            new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"),
            new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x03);
        h.Radio.Unreachable(Left);

        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.PassPauses(2);
        await on.WaitAsync(Limit);

        Assert.Equal(new StationsSummary("1/2 Powered · Desk didn't answer.", StatusLevel.Warning, false), h.Manager.Summary);

        // All on, but one didn't take the Off: a warning still, never the check.
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal(new StationsSummary("2/2 Powered", StatusLevel.Ok, false), h.Manager.Summary);
        var off = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        await h.PassPauses(2);
        await off.WaitAsync(Limit);

        Assert.Equal(new StationsSummary("2/2 Powered · Desk didn't answer.", StatusLevel.Warning, false), h.Manager.Summary);

        // Desk answers the next one; their adverts say they are on until they are asleep.
        h.Radio.Reachable(Left);
        await h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new StationsSummary("2/2 Powered", StatusLevel.Ok, false), h.Manager.Summary);
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);
        Assert.Equal(new StationsSummary("0/2 Powered", StatusLevel.Info, false), h.Manager.Summary);
    }

    [Fact]
    public async Task Summary_names_every_station_that_did_not_answer()
    {
        using var h = new StationHarness();
        h.Known(
            new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Shelf"),
            new KnownStation(RightAddress, RightName, Selected: true, CustomName: "Desk"),
            new KnownStation(OldAddress, OldName, CustomName: "Tower"));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName);
        h.Hear(Right, RightName);
        h.Hear(Old, OldName, null);
        h.Radio.Unreachable(Left);
        h.Radio.Unreachable(Right);
        h.Radio.Unreachable(Old);

        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.PassPauses(2);
        await on.WaitAsync(Limit);
        var tower = h.Manager.SwitchAsync(OldAddress, StationCommand.On, CancellationToken.None);
        await h.PassPauses(2);
        await tower.WaitAsync(Limit);

        // Only selected stations are named, by name, also while a command runs; Bluetooth's state comes before them.
        Assert.Equal(new StationsSummary("2/2 Powered · Desk, Shelf didn't answer.", StatusLevel.Warning, false), h.Manager.Summary);
        Assert.Equal("didn't answer", h.View(Old).Problem);
        var hold = h.Radio.Hold(Right);
        var again = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.ConnectionsTo(Right).Length == 4).WaitAsync(Limit);
        Assert.Equal(new StationsSummary("2/2 Powered · Desk, Shelf didn't answer.", StatusLevel.Warning, true), h.Manager.Summary);
        h.Radio.State = BluetoothState.Off;
        Assert.Equal(new StationsSummary("Bluetooth is off.", StatusLevel.Warning, false), h.Manager.Summary);
        h.Radio.State = BluetoothState.Missing;
        Assert.Equal(new StationsSummary("This PC has no Bluetooth.", StatusLevel.Info, false), h.Manager.Summary);
        hold.SetResult();
        await again.WaitAsync(Limit);
    }

    [Fact]
    public void Rename_and_set_id_reject_bad_text()
    {
        using var h = new StationHarness();
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Old, OldName, null);
        }

        Assert.Equal("Use at most 40 characters.", h.Manager.Rename(LeftAddress, new string('x', 41)));
        Assert.Null(h.View(Left).Station.CustomName);
        Assert.Null(h.Manager.Rename(LeftAddress, new string('x', 40)));
        Assert.Equal(new string('x', 40), h.View(Left).Station.CustomName);
        Assert.Null(h.Manager.Rename(LeftAddress, "  Desk  "));
        Assert.Equal("Desk", h.View(Left).Station.DisplayName);
        Assert.Null(h.Manager.Rename(LeftAddress, "   "));
        Assert.Null(h.View(Left).Station.CustomName);
        Assert.Equal(LeftName, h.View(Left).Station.DisplayName);

        Assert.Equal("That isn't 8 letters and digits (0–9, A–F).", h.Manager.SetId(OldAddress, "GGBBCCDD"));
        Assert.Equal("That isn't 8 letters and digits (0–9, A–F).", h.Manager.SetId(OldAddress, "AABBCCD"));
        Assert.Null(h.View(Old).Station.Id);
        Assert.Null(h.Manager.SetId(OldAddress, " aabbccdd "));
        Assert.Equal("aabbccdd", h.View(Old).Station.Id);
        Assert.Equal("aabbccdd", Assert.Single(new StationStore(h.Path).Load(), s => s.Address == OldAddress).Id);
        Assert.Null(h.Manager.SetId(OldAddress, ""));
        Assert.Null(h.View(Old).Station.Id);

        Assert.NotNull(h.Manager.Rename(RightAddress, "Shelf"));
        Assert.NotNull(h.Manager.SetId("not an address", "AABBCCDD"));
        Assert.Equal(2, h.Manager.Stations.Count);
    }

    [Fact]
    public void Failed_save_is_shown()
    {
        using var h = new StationHarness();

        // The store writes a .tmp file first: a folder in its place makes every save fail.
        Directory.CreateDirectory(h.Path + ".tmp");
        var changed = 0;
        h.Manager.Changed += () => Interlocked.Increment(ref changed);
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        var problem = h.Manager.SaveProblem;
        Assert.NotNull(problem);
        Assert.StartsWith("Mirador couldn't save its base station list (", problem);
        Assert.EndsWith(").", problem);
        Assert.DoesNotContain(".).", problem);
        Assert.Contains(h.Logged, line => line.Contains(problem, StringComparison.Ordinal));
        Assert.Single(h.Manager.Stations);
        Assert.Equal(problem, h.Manager.Rename(LeftAddress, "Desk"));
        Assert.Equal("Desk", h.View(Left).Station.DisplayName);

        Directory.Delete(h.Path + ".tmp");
        changed = 0;
        h.Manager.SetSelected(LeftAddress, true);

        Assert.Null(h.Manager.SaveProblem);
        Assert.True(changed > 0);
        Assert.Equal([new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk")], new StationStore(h.Path).Load());
    }

    [Fact]
    public void Forget_removes()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"), new KnownStation(RightAddress, RightName));

        h.Manager.Forget(LeftAddress.ToLowerInvariant());

        Assert.Equal([RightAddress], h.Manager.Stations.Select(s => s.Station.Address));
        Assert.Equal([new KnownStation(RightAddress, RightName)], new StationStore(h.Path).Load());

        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        Assert.Equal(new KnownStation(LeftAddress, LeftName), h.View(Left).Station);
    }

    [Fact]
    public async Task A_station_stuck_waking_up_gets_the_on_again()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Right, RightName, 0x00);

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Hear(Right, RightName, 0x01);

        // A healthy one is at 02 1.5 s later; this one stays at 01.
        h.Clock.Advance(StationManager.StallTime - TimeSpan.FromMilliseconds(1));
        h.Hear(Right, RightName, 0x01);
        await h.QueueDrained();
        Assert.Equal(["01"], h.Radio.WritesTo(Right));

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await h.Radio.Until(r => r.WritesTo(Right).Length == 2).WaitAsync(Limit);
        await h.QueueDrained();

        // Sent again, it wakes.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Hear(Right, RightName, 0x02);
        h.Clock.Advance(TimeSpan.FromSeconds(9));
        h.Hear(Right, RightName, 0x03);
        h.Clock.Advance(StationManager.SettleTime);
        await h.QueueDrained();

        Assert.Equal(["01", "01"], h.Radio.WritesTo(Right));
        Assert.Equal(StationPower.On, h.View(Right).Power);
        Assert.Equal(
            [$"Base stations: On {RightName} ({RightAddress}): done", $"Base stations: On {RightName} ({RightAddress}): sent again (stuck waking up)"],
            h.Logged.Where(l => l.StartsWith($"Base stations: On {RightName}", StringComparison.Ordinal)));
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);
    }

    [Fact]
    public async Task A_station_that_keeps_waking_up_is_not_sent_the_on_again()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(RightAddress, RightName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Right, RightName, 0x00);
        }

        h.WhenRelevant();

        // As Finn's stations wake: 01 at 3 s, 02 at 4.5 s, 03 at 13.6 s (Mirador listens after a command).
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Hear(Right, RightName, 0x01);
        h.Clock.Advance(TimeSpan.FromSeconds(1.5));
        h.Hear(Right, RightName, 0x02);
        h.Clock.Advance(TimeSpan.FromSeconds(9.1));
        h.Hear(Right, RightName, 0x03);
        h.Clock.Advance(StationManager.SettleTime);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Right));
        Assert.DoesNotContain(h.Logged, l => l.Contains("sent again", StringComparison.Ordinal));
        Assert.False(h.Radio.Watching);
    }

    [Fact]
    public async Task The_on_is_sent_again_at_most_twice()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(RightAddress, RightName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Right, RightName, 0x00);
        }

        h.WhenRelevant();

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Right, RightName, 0x01);
        for (var sent = 2; sent <= 1 + StationManager.Resends; sent++)
        {
            h.Clock.Advance(StationManager.StallTime);
            var expected = sent;
            await h.Radio.Until(r => r.WritesTo(Right).Length == expected).WaitAsync(Limit);
            await h.QueueDrained();
        }

        // Still stuck: nothing more, and listening ends as after any command.
        h.Clock.Advance(StationManager.StallTime);
        await h.QueueDrained();
        h.Clock.Advance(StationManager.SettleTime);
        await h.QueueDrained();

        Assert.Equal(["01", "01", "01"], h.Radio.WritesTo(Right));
        Assert.Equal(2, h.Logged.Count(l => l.EndsWith("sent again (stuck waking up)", StringComparison.Ordinal)));
        Assert.False(h.Radio.Watching);
    }

    [Fact]
    public async Task A_station_no_longer_selected_is_never_sent_the_on_again()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Left, LeftName, 0x01);
        h.Hear(Right, RightName, 0x01);
        h.Manager.SetSelected(LeftAddress, false);
        h.Clock.Advance(StationManager.StallTime * 2);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
        Assert.Empty(h.Radio.ConnectionsTo(Right));

        // One the user picked on its own is still wanted.
        await h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Clock.Advance(StationManager.StallTime);
        await h.Radio.Until(r => r.WritesTo(Right).Length == 2).WaitAsync(Limit);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Flush_cancels_a_pending_resend()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x03);

        // Still to come.
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Left, LeftName, 0x01);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);
        h.Clock.Advance(StationManager.StallTime);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));

        // Queued behind Identify.
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        var hold = h.Radio.Hold(Right);
        var identify = h.Manager.IdentifyAsync(RightAddress, CancellationToken.None);
        await h.Time.Set(StationManager.TryLimit, 3).WaitAsync(Limit);
        h.Clock.Advance(StationManager.StallTime);
        var flush = h.Manager.FlushAsync(TimeSpan.FromSeconds(15));
        hold.SetResult();
        await Task.WhenAll(identify, flush).WaitAsync(Limit);
        await h.QueueDrained();

        Assert.Equal(["01", "01"], h.Radio.WritesTo(Left));
        Assert.Contains($"Base stations: On {LeftAddress}: not sent again (Mirador is quitting)", h.Logged);
    }

    [Theory]
    [InlineData(StationCommand.Sleep)]
    [InlineData(StationCommand.Standby)]
    public async Task An_off_asked_for_while_an_on_is_sending_is_not_followed_by_the_on_again(StationCommand command)
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);

        // Right (first by name) takes the On at once; Left is slow, and the Off is asked for meanwhile.
        var hold = h.Radio.Hold(Left);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.ConnectionsTo(Left).Length == 1).WaitAsync(Limit);
        var off = h.Manager.SwitchSelectedAsync(command, CancellationToken.None);
        hold.SetResult();
        await Task.WhenAll(on, off).WaitAsync(Limit);

        // Left's advert stays at 01: it is stalling, or in standby, which the advert doesn't show.
        h.Hear(Left, LeftName, 0x01);
        h.Clock.Advance(StationManager.StallTime);
        h.Hear(Left, LeftName, 0x01);
        h.Clock.Advance(StationManager.StallTime);
        await h.QueueDrained();

        var offValue = command == StationCommand.Standby ? "02" : "00";
        Assert.Equal(["01", offValue], h.Radio.WritesTo(Left));
        Assert.Equal(["01", offValue], h.Radio.WritesTo(Right));
        Assert.DoesNotContain(h.Logged, l => l.Contains("again (stuck", StringComparison.Ordinal) || l.Contains("sent again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_off_that_did_not_reach_a_station_is_not_followed_by_the_on_again()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);

        // Left takes the On late, after the Off was asked for, and then the Off doesn't get through to it.
        var hold = h.Radio.Hold(Left);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.ConnectionsTo(Left).Length == 1).WaitAsync(Limit);
        var off = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        h.Radio.Unreachable(Left);
        hold.SetResult();
        await on.WaitAsync(Limit);
        await h.PassPauses(2);
        await off.WaitAsync(Limit);
        Assert.Equal("didn't answer", h.View(Left).Problem);

        h.Hear(Left, LeftName, 0x01);
        h.Clock.Advance(StationManager.StallTime * 2);
        await h.QueueDrained();

        Assert.Equal(1 + StationManager.Tries, h.Radio.ConnectionsTo(Left).Length);
        Assert.Equal(["01"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Flush_while_an_on_is_sending_leaves_nothing_to_send_again()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);

        // Mirador quits just as Left, the second, takes the On: the On may still see it answer.
        Task? flush = null;
        h.Radio.OnWrite = address =>
        {
            if (address == Left)
            {
                flush ??= h.Manager.FlushAsync(TimeSpan.FromSeconds(15));
            }
        };
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
        await on.WaitAsync(Limit);
        await flush!.WaitAsync(Limit);
        h.Radio.OnWrite = null;

        h.Hear(Left, LeftName, 0x01);
        h.Hear(Right, RightName, 0x01);
        h.Clock.Advance(StationManager.StallTime * 2);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));
        Assert.Equal(["01"], h.Radio.WritesTo(Right));
    }

    [Fact]
    public async Task An_on_sent_again_that_does_not_get_through_says_so()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Right, RightName, 0x00);

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Right, RightName, 0x01);
        h.Radio.Unreachable(Right);
        h.Clock.Advance(StationManager.StallTime);
        await h.Radio.Until(r => r.ConnectionsTo(Right).Length == 2).WaitAsync(Limit);
        await h.PassPauses(2);
        await h.QueueDrained();

        Assert.Equal(1 + StationManager.Tries, h.Radio.ConnectionsTo(Right).Length);
        Assert.Contains(
            $"Base stations: On {RightName} ({RightAddress}) again (stuck waking up): didn't answer (Windows couldn't connect to the base station over Bluetooth)",
            h.Logged);
    }

    [Fact]
    public async Task An_on_sent_again_to_a_station_no_longer_selected_is_dropped_quietly()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(RightAddress, RightName, Selected: true), new KnownStation(LeftAddress, LeftName));
        using var listen = h.Manager.Listen();
        h.Hear(Right, RightName, 0x00);
        h.Hear(Left, LeftName, 0x03);

        // The On sent again waits behind Left's Identify, and Right is deselected meanwhile.
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Right, RightName, 0x01);
        var hold = h.Radio.Hold(Left);
        var identify = h.Manager.IdentifyAsync(LeftAddress, CancellationToken.None);
        await h.Time.Set(StationManager.TryLimit, 2).WaitAsync(Limit);
        h.Clock.Advance(StationManager.StallTime);
        h.Manager.SetSelected(RightAddress, false);
        var running = new List<StationCommand?>();
        h.Manager.Changed += () =>
        {
            lock (running)
            {
                running.Add(h.Manager.Running);
            }
        };
        hold.SetResult();
        await identify.WaitAsync(Limit);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Right));
        Assert.Single(h.Logged, l => l == $"Base stations: On {RightName} ({RightAddress}) again (stuck waking up): not sent (it is no longer selected)");
        lock (running)
        {
            Assert.DoesNotContain(StationCommand.On, running);
        }
    }

    [Fact]
    public async Task A_later_command_and_a_start_cancel_a_pending_resend()
    {
        using var h = new StationHarness();
        h.Settings = StationHarness.Live with { BaseStationsOnAtStart = false };
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x03);

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Left, LeftName, 0x01);
        await h.Manager.SwitchAsync(RightAddress, StationCommand.Standby, CancellationToken.None).WaitAsync(Limit);
        h.Clock.Advance(StationManager.StallTime);
        await h.QueueDrained();

        Assert.Equal(["01"], h.Radio.WritesTo(Left));

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Manager.Started(StationHarness.Profile(true), null);
        h.Clock.Advance(StationManager.StallTime);
        await h.QueueDrained();

        Assert.Equal(["01", "01"], h.Radio.WritesTo(Left));
        Assert.DoesNotContain(h.Logged, l => l.Contains("sent again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_problem_goes_once_the_advert_shows_what_the_station_was_told()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true, CustomName: "Desk"));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Radio.Unreachable(Left);

        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.PassPauses(2);
        await on.WaitAsync(Limit);
        Assert.Equal(new StationsSummary("0/1 Powered · Desk didn't answer.", StatusLevel.Warning, false), h.Manager.Summary);

        // The write got through after all.
        h.Hear(Left, LeftName, 0x01);
        Assert.Equal("didn't answer", h.View(Left).Problem);
        h.Hear(Left, LeftName, 0x03);
        Assert.Null(h.View(Left).Problem);
        Assert.Equal(new StationsSummary("1/1 Powered", StatusLevel.Ok, false), h.Manager.Summary);

        var sleep = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
        await h.PassPauses(2);
        await sleep.WaitAsync(Limit);
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal("didn't answer", h.View(Left).Problem);
        h.Hear(Left, LeftName, 0x00);
        Assert.Null(h.View(Left).Problem);
    }

    [Fact]
    public async Task Summary_is_busy_while_the_stations_wake_after_an_on()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);

        // The adverts are about 3 s behind.
        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(new StationsSummary("0/2 Powered", StatusLevel.Info, true), h.Manager.Summary);
        h.Hear(Left, LeftName, 0x03);
        h.Hear(Right, RightName, 0x00);
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, true), h.Manager.Summary);

        // One that is still asleep once the On's time is up reads as it is, and Home hears of it.
        var changed = 0;
        h.Manager.Changed += () => Interlocked.Increment(ref changed);
        h.Clock.Advance(StationManager.SettleTime - TimeSpan.FromSeconds(1));
        h.Hear(Left, LeftName, 0x03);
        h.Hear(Right, RightName, 0x00);
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, true), h.Manager.Summary);
        changed = 0;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, false), h.Manager.Summary);
        Assert.True(changed > 0);

        // One that didn't answer is named.
        h.Radio.Unreachable(Right);
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.PassPauses(2);
        await on.WaitAsync(Limit);
        Assert.Equal(new StationsSummary($"1/2 Powered · {RightName} didn't answer.", StatusLevel.Warning, false), h.Manager.Summary);
    }

    [Fact]
    public async Task Summary_after_an_on_still_counts_a_station_put_to_sleep()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName, Selected: true));
        using var listen = h.Manager.Listen();
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x00);

        await h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Left, LeftName, 0x03);
        h.Hear(Right, RightName, 0x03);
        Assert.Equal(new StationsSummary("2/2 Powered", StatusLevel.Ok, false), h.Manager.Summary);
        await h.Manager.SwitchAsync(RightAddress, StationCommand.Sleep, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Right, RightName, 0x00);

        // Within the On's time, the one put to sleep isn't waking.
        Assert.Equal(new StationsSummary("1/2 Powered", StatusLevel.Info, false), h.Manager.Summary);

        // Only the station switched on is waking, until its On's time is up: the other one was left asleep.
        h.Hear(Left, LeftName, 0x00);
        await h.Manager.SwitchAsync(LeftAddress, StationCommand.On, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new StationsSummary("0/2 Powered", StatusLevel.Info, true), h.Manager.Summary);
        h.Clock.Advance(StationManager.SettleTime);
        Assert.Equal(new StationsSummary("0/2 Powered", StatusLevel.Info, false), h.Manager.Summary);
    }

    [Fact]
    public async Task A_queued_session_off_is_dropped_by_a_later_command_for_one_station()
    {
        using var h = new StationHarness();
        h.Settings = StationHarness.Live with { BaseStationsOnAtStart = false };
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        var hold = h.Radio.Hold(Right);
        var first = h.Manager.SwitchAsync(RightAddress, StationCommand.On, CancellationToken.None);
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        Assert.True(h.Manager.OffPending);

        // Unlike the user's own Off of the selected, the session's off goes for a command for another station too.
        var later = h.Manager.SwitchAsync(RightAddress, StationCommand.Standby, CancellationToken.None);
        Assert.False(h.Manager.OffPending);
        hold.SetResult();
        await Task.WhenAll(first, later).WaitAsync(Limit);
        await h.QueueDrained();

        Assert.Empty(h.Radio.ConnectionsTo(Left));
        Assert.Equal(["01", "02"], h.Radio.WritesTo(Right));
        Assert.Contains("Base stations: Sleep not sent (a later command replaced it)", h.Logged);
    }

    [Fact]
    public async Task Windows_ending_sends_the_off_at_once_and_once()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        // The session still runs (its module may take long to end): the off doesn't wait for its Ended.
        h.Manager.EndingNow(StationHarness.Profile(true), null);
        Assert.True(h.Manager.OffPending);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));

        // The session's own end then sends nothing.
        h.Manager.Ended(StationHarness.Profile(true), null);
        Assert.False(h.Manager.OffPending);
        h.Clock.Advance(StationManager.OffDelay * 2);
        await h.QueueDrained();

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Contains("Base stations: the off after Monado stopped went out already (Windows is ending the session)", h.Logged);

        // A session after that (Windows' ending was cancelled after all) is followed by its off again.
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.Radio.Until(r => r.WritesTo(Left).Length == 2).WaitAsync(Limit);
        h.Manager.Ended(StationHarness.Profile(true), null);
        Assert.True(h.Manager.OffPending);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);

        Assert.Equal(["00", "01", "00"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task Windows_ending_stops_a_running_on_first()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        // Monado's On is still sending when Windows ends: the off follows it, the flush stops it.
        var hold = h.Radio.Hold(Left);
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        h.Manager.EndingNow(StationHarness.Profile(true), null);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);
        hold.SetResult();
        await h.Radio.Until(r => r.Steps.Count(s => s == $"close {LeftAddress}") == 2).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Contains("Base stations: On stopped (Mirador is quitting)", h.Logged);

        h.Manager.Ended(StationHarness.Profile(true), null);
        h.Clock.Advance(StationManager.OffDelay);
        await h.QueueDrained();

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
    }

    [Fact]
    public async Task A_stopped_on_sends_nothing_once_the_next_command_begins()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        // Monado's On is stuck connecting when Windows ends; the off is queued behind it, and the flush stops the On.
        var hold = h.Radio.Hold(Left);
        h.Manager.Started(StationHarness.Profile(true), null);
        await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        h.Manager.EndingNow(StationHarness.Profile(true), null);

        // The moment the off begins, on the thread the stop goes on, the stopped On's connection comes after all; the
        // off goes on once that late link is closed.
        var released = 0;
        h.Manager.Changed += () =>
        {
            if (h.Manager.Running == StationCommand.Sleep && Interlocked.Exchange(ref released, 1) == 0)
            {
                hold.SetResult();
                h.Radio.Until(r => r.Steps.Contains($"close {LeftAddress}")).Wait(Limit);
            }
        };
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);

        Assert.Equal(1, released);
        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Equal(
            [$"connect {LeftAddress}", $"close {LeftAddress}", $"connect {LeftAddress}", $"write {LeftAddress} 00", $"close {LeftAddress}"],
            h.Radio.Steps);
    }

    [Fact]
    public async Task Windows_ending_without_the_setting_or_the_profile_sends_nothing()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
        }

        Assert.True(h.Manager.OffFollows(StationHarness.Profile(true), null));
        Assert.False(h.Manager.OffFollows(StationHarness.Profile(false), null));
        h.Manager.EndingNow(StationHarness.Profile(false), null);
        h.Settings = StationHarness.Live with { BaseStationsOffAtStop = false };
        Assert.False(h.Manager.OffFollows(StationHarness.Profile(true), null));
        h.Manager.EndingNow(StationHarness.Profile(true), null);
        Assert.False(h.Manager.OffPending);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);

        Assert.Empty(h.Radio.Connections);

        // Nothing was sent, so the session's end isn't skipped.
        h.Settings = StationHarness.Live;
        h.Manager.Ended(StationHarness.Profile(true), null);
        Assert.True(h.Manager.OffPending);
    }

    [Fact]
    public async Task Startup_scans_once_and_finds_stations()
    {
        using var h = new StationHarness();
        h.Settings = new AppSettings();
        var changed = 0;
        h.Manager.Changed += () => Interlocked.Increment(ref changed);

        var startup = h.Manager.StartupAsync(CancellationToken.None);

        Assert.True(h.Radio.Watching);
        Assert.True(h.Manager.Looking);
        Assert.True(changed > 0);
        h.Hear(Left, LeftName, 0x00);
        h.Hear(Right, RightName, 0x03);
        Assert.False(startup.IsCompleted);
        await h.EndScan(startup);

        // Found, unselected, with their states; then nothing listens any more, and nothing was sent.
        Assert.False(h.Radio.Watching);
        Assert.False(h.Manager.Looking);
        Assert.Equal(1, h.Radio.WatchCalls);
        Assert.Equal([new KnownStation(RightAddress, RightName), new KnownStation(LeftAddress, LeftName)], h.Manager.Stations.Select(s => s.Station));
        Assert.All(h.Manager.Stations, s => Assert.True(s.Seen));
        Assert.Equal(StationPower.Asleep, h.View(Left).Power);
        Assert.Equal(StationPower.On, h.View(Right).Power);
        h.Clock.Advance(StationManager.SettleTime * 2);
        Assert.False(h.Radio.Watching);
        Assert.Equal(1, h.Radio.WatchCalls);
        Assert.Empty(h.Radio.Connections);
    }

    [Fact]
    public async Task Startup_switches_on_only_with_the_setting()
    {
        // Without it, the scan sends nothing.
        using (var h = new StationHarness())
        {
            h.Settings = new AppSettings();
            h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
            var startup = h.Manager.StartupAsync(CancellationToken.None);
            h.Hear(Left, LeftName, 0x00);
            h.Hear(Right, RightName, 0x00);
            await h.EndScan(startup);
            await h.QueueDrained();

            Assert.Empty(h.Radio.Connections);
        }

        // With it, On to the selected stations after the scan, queued like a command: Mirador's start doesn't wait for it.
        using (var h = new StationHarness())
        {
            h.Settings = new AppSettings(BaseStationsOnAtMiradorStart: true);
            h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
            var hold = h.Radio.Hold(Left);
            var startup = h.Manager.StartupAsync(CancellationToken.None);
            h.Hear(Left, LeftName, 0x00);
            h.Hear(Right, RightName, 0x00);
            Assert.Empty(h.Radio.Connections);
            await h.EndScan(startup);
            await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
            Assert.Equal(StationCommand.On, h.Manager.Running);
            hold.SetResult();
            await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
            await h.QueueDrained();

            Assert.Equal(["01"], h.Radio.WritesTo(Left));
            Assert.Empty(h.Radio.ConnectionsTo(Right));
            Assert.Contains($"Base stations: On {LeftName} ({LeftAddress}): done", h.Logged);
        }

        // Cancelled during the scan: no On, and no exception.
        using (var h = new StationHarness())
        {
            h.Settings = new AppSettings(BaseStationsOnAtMiradorStart: true);
            h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
            using var cancel = new CancellationTokenSource();
            var startup = h.Manager.StartupAsync(cancel.Token);
            h.Hear(Left, LeftName, 0x00);
            cancel.Cancel();
            await startup.WaitAsync(Limit);
            await h.QueueDrained();

            Assert.False(h.Radio.Watching);
            Assert.Empty(h.Radio.Connections);
        }

        // Mirador quits during the scan: its On doesn't follow the quit.
        using (var h = new StationHarness())
        {
            h.Settings = new AppSettings(BaseStationsOnAtMiradorStart: true);
            h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));
            var startup = h.Manager.StartupAsync(CancellationToken.None);
            h.Hear(Left, LeftName, 0x00);
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);
            await h.EndScan(startup);
            await h.QueueDrained();

            Assert.Empty(h.Radio.Connections);
        }
    }

    [Fact]
    public async Task Last_scan_mode_listens_only_while_scanning_or_commanding()
    {
        using var h = new StationHarness();
        h.Settings = new AppSettings();
        Assert.Equal(StationStateMode.LastScan, h.Settings.BaseStationsState);
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName, Selected: true));

        // A page or Home's card on screen holds nothing.
        using var listen = h.Manager.Listen();
        Assert.False(h.Radio.Watching);
        Assert.Equal(0, h.Radio.WatchCalls);
        Assert.False(h.Manager.Looking);

        // A scan listens for its 8 s.
        var scan = h.Manager.ScanAsync(CancellationToken.None);
        Assert.True(h.Radio.Watching);
        Assert.True(h.Manager.Looking);
        h.Hear(Left, LeftName);
        h.Clock.Advance(StationManager.ScanTime - TimeSpan.FromMilliseconds(1));
        Assert.True(h.Radio.Watching);
        await h.EndScan(scan);
        Assert.False(h.Radio.Watching);
        Assert.False(h.Manager.Looking);

        // A command listens first for its station not heard since Mirador started, and for a while after it.
        var on = h.Manager.SwitchSelectedAsync(StationCommand.On, CancellationToken.None);
        await h.Time.Set(StationManager.FindTime).WaitAsync(Limit);
        Assert.True(h.Radio.Watching);
        Assert.False(h.Manager.Looking);
        h.Hear(Right, RightName);
        await on.WaitAsync(Limit);
        Assert.Equal(["01"], h.Radio.WritesTo(Left));
        Assert.Equal(["01"], h.Radio.WritesTo(Right));
        Assert.True(h.Radio.Watching);
        h.Clock.Advance(StationManager.SettleTime - TimeSpan.FromSeconds(1));
        Assert.True(h.Radio.Watching);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(h.Radio.Watching);

        // The page is still shown: nothing listens.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(h.Radio.Watching);
        Assert.Equal(2, h.Radio.WatchCalls);
    }

    [Fact]
    public async Task Constant_mode_listens_all_the_time()
    {
        using var h = new StationHarness();
        h.Settings = new AppSettings(BaseStationsState: StationStateMode.Live);

        // From Mirador's start on, through and after its scan, whether or not a page holds a listen.
        var start = h.Manager.StartupAsync(CancellationToken.None);
        Assert.True(h.Radio.Watching);
        await h.EndScan(start);
        Assert.True(h.Radio.Watching);
        Assert.False(h.Manager.Looking);

        // Listening all the time isn't a scan: the list isn't started afresh.
        h.Hear(Left, LeftName, 0x00);
        Assert.True(h.View(Left).Seen);
        Assert.Equal(StationPower.Asleep, h.View(Left).Power);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Hear(Left, LeftName, 0x03);
        Assert.Equal(StationPower.On, h.View(Left).Power);

        h.Manager.Listen().Dispose();
        Assert.True(h.Radio.Watching);
        Assert.Equal(1, h.Radio.WatchCalls);
        Assert.Empty(h.Radio.Connections);

        // "When Relevant" stops it.
        h.WhenRelevant();
        Assert.False(h.Radio.Watching);
        Assert.True(h.View(Left).Seen);
    }

    [Fact]
    public async Task Changing_the_mode_while_held_starts_or_stops_listening()
    {
        using var h = new StationHarness();
        h.Settings = new AppSettings();
        var listen = h.Manager.Listen();
        Assert.False(h.Radio.Watching);

        // Saved, but not said yet: nothing changes until the page calls SettingsChanged.
        h.Settings = new AppSettings(BaseStationsState: StationStateMode.Live);
        Assert.False(h.Radio.Watching);
        h.Manager.SettingsChanged();
        Assert.True(h.Radio.Watching);
        h.Hear(Left, LeftName);
        Assert.True(h.View(Left).Seen);

        h.Settings = new AppSettings();
        h.Manager.SettingsChanged();
        Assert.False(h.Radio.Watching);

        // A scan listens in either mode, and the mode changing doesn't stop it.
        var scan = h.Manager.ScanAsync(CancellationToken.None);
        h.Manager.SettingsChanged();
        Assert.True(h.Radio.Watching);
        h.Settings = new AppSettings(BaseStationsState: StationStateMode.Live);
        h.Manager.SettingsChanged();
        await h.EndScan(scan);
        Assert.True(h.Radio.Watching);

        // "Constant" goes on with nobody holding a listen; "When Relevant" stops it.
        listen.Dispose();
        Assert.True(h.Radio.Watching);
        h.WhenRelevant();
        Assert.False(h.Radio.Watching);
    }

    [Fact]
    public async Task Not_heard_in_the_last_scan_is_not_seen()
    {
        using var h = new StationHarness();
        h.Settings = new AppSettings();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true));

        // Known but not heard since Mirador started.
        Assert.False(h.View(Left).Seen);

        var scan = h.Manager.ScanAsync(CancellationToken.None);
        h.Hear(Left, LeftName);
        h.Hear(Right, RightName);
        await h.EndScan(scan);

        Assert.True(h.View(Left).Seen);
        Assert.True(h.View(Right).Seen);

        // It doesn't lapse: an hour later, with no scan since, both still are.
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(h.View(Left).Seen);
        Assert.True(h.View(Right).Seen);

        // The next scan hears only Right: Left is still seen while it runs, and isn't once it is over.
        var changed = 0;
        h.Manager.Changed += () => Interlocked.Increment(ref changed);
        scan = h.Manager.ScanAsync(CancellationToken.None);
        h.Hear(Right, RightName);
        Assert.True(h.View(Left).Seen);
        changed = 0;
        await h.EndScan(scan);

        Assert.False(h.View(Left).Seen);
        Assert.True(h.View(Right).Seen);
        Assert.True(changed > 0);
        Assert.True(h.View(Left).Station.Selected);

        // A scan that is cancelled isn't the last scan: what is seen stays.
        using (var cancel = new CancellationTokenSource())
        {
            scan = h.Manager.ScanAsync(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(Limit));
        }

        Assert.True(h.View(Right).Seen);
        Assert.False(h.Manager.Looking);
        Assert.False(h.Radio.Watching);

        // Heard since the last scan (a command listens while it settles): seen again.
        await h.Manager.SwitchAsync(LeftAddress, StationCommand.On, CancellationToken.None).WaitAsync(Limit);
        h.Hear(Left, LeftName);
        Assert.True(h.View(Left).Seen);
    }

    [Fact]
    public async Task Quit_off_with_the_setting()
    {
        // Mirador quits with no session: the off goes to the selected stations at once.
        using (var h = QuitHarness())
        {
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
            Assert.Empty(h.Radio.ConnectionsTo(Right));
            Assert.Contains("Base stations: switching off as Mirador quits", h.Logged);
            Assert.False(h.Manager.OffPending);
        }

        // Monado's stop switched them off a while ago: each setting acts on its own event, so quitting does too.
        using (var h = QuitHarness())
        {
            h.Manager.Ended(StationHarness.Profile(true), null);
            await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
            h.Clock.Advance(StationManager.OffDelay);
            await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
            await h.QueueDrained();
            h.Clock.Advance(TimeSpan.FromMinutes(10));
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

            Assert.Equal(["00", "00"], h.Radio.WritesTo(Left));
        }

        // Behind a command still sending it waits its turn; nothing that drops the session's off or the user's own drops
        // it.
        using (var h = QuitHarness())
        {
            h.Settings = h.Settings with { BaseStationsOnAtStart = false };
            var hold = h.Radio.Hold(Right);
            var right = h.Manager.SwitchAsync(RightAddress, StationCommand.Sleep, CancellationToken.None);
            await h.Radio.Until(r => r.ConnectionsTo(Right).Length == 1).WaitAsync(Limit);
            var flush = h.Manager.FlushAsync(TimeSpan.FromSeconds(15));
            h.Manager.Started(StationHarness.Profile(true), null);
            var sleep = h.Manager.SwitchSelectedAsync(StationCommand.Sleep, CancellationToken.None);
            hold.SetResult();
            await Task.WhenAll(right, sleep, flush).WaitAsync(Limit);

            // The quit's off, then the user's.
            Assert.Equal(["00", "00"], h.Radio.WritesTo(Left));
            Assert.Equal(["00"], h.Radio.WritesTo(Right));
        }
    }

    [Fact]
    public async Task Quit_off_once_when_monado_off_also_due()
    {
        // Quitting while Monado runs: its stop leaves the session's off waiting its 5 s, and the flush sends that one alone.
        using (var h = QuitHarness())
        {
            h.Manager.Ended(StationHarness.Profile(true), null);
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

            Assert.Equal(["00"], h.Radio.WritesTo(Left));

            // A second flush (Windows ending as the window closes) sends nothing more.
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);
            h.Clock.Advance(StationManager.OffDelay * 2);
            await h.QueueDrained();

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
            Assert.DoesNotContain("Base stations: switching off as Mirador quits", h.Logged);
        }

        // The session's off queued after its delay and still sending as Mirador quits.
        using (var h = QuitHarness())
        {
            var hold = h.Radio.Hold(Left);
            h.Manager.Ended(StationHarness.Profile(true), null);
            await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
            h.Clock.Advance(StationManager.OffDelay);
            await h.Radio.Until(r => r.Connections.Length == 1).WaitAsync(Limit);
            var flush = h.Manager.FlushAsync(TimeSpan.FromSeconds(15));
            hold.SetResult();
            await flush.WaitAsync(Limit);
            await h.QueueDrained();

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
        }

        // Windows ending while Monado runs: the session's off goes out at once; neither Monado's end nor the flush sends
        // another, even when that off is done before them.
        using (var h = QuitHarness())
        {
            h.Manager.EndingNow(StationHarness.Profile(true), null);
            await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
            await h.QueueDrained();
            h.Manager.Ended(StationHarness.Profile(true), null);
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);
            h.Clock.Advance(StationManager.OffDelay * 2);
            await h.QueueDrained();

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
            Assert.Empty(h.Radio.ConnectionsTo(Right));
        }
    }

    [Fact]
    public async Task No_quit_off_without_the_setting_or_a_selection()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        Assert.Empty(h.Radio.Connections);

        h.Settings = StationHarness.Live with { BaseStationsOffAtMiradorQuit = true };
        h.Manager.SetSelected(LeftAddress, false);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);
        await h.QueueDrained();

        Assert.Empty(h.Radio.Connections);
        Assert.DoesNotContain(h.Logged, line => line.Contains("quits", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_at_quit_wanted()
    {
        using var h = new StationHarness();
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));

        Assert.False(h.Manager.OffAtQuitWanted);
        h.Settings = StationHarness.Live with { BaseStationsOffAtMiradorQuit = true };
        Assert.True(h.Manager.OffAtQuitWanted);
        h.Manager.SetSelected(LeftAddress, false);
        Assert.False(h.Manager.OffAtQuitWanted);
        h.Manager.SetSelected(RightAddress, true);
        Assert.True(h.Manager.OffAtQuitWanted);

        // Mirador's own off isn't the session's.
        Assert.False(h.Manager.OffPending);
    }

    [Fact]
    public async Task Quit_off_goes_before_monados_stop_finishes()
    {
        // A session of a profile without base stations still stopping (a headset's end can take long): the quit's off is
        // decided and sent at once, before the session has ended.
        using (var h = QuitHarness())
        {
            h.Manager.Started(StationHarness.Profile(false), null);
            h.Manager.QuittingNow();
            await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
            h.Manager.Ended(StationHarness.Profile(false), null);
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
            Assert.Empty(h.Radio.ConnectionsTo(Right));
            Assert.Contains("Base stations: switching off as Mirador quits", h.Logged);
        }

        // A Lighthouse session with "Power off when Monado stops" too: the quit's off goes at once, and the session's end
        // then sends no second one.
        using (var h = QuitHarness())
        {
            h.Settings = h.Settings with { BaseStationsOnAtStart = false };
            h.Manager.Started(StationHarness.Profile(true), null);
            h.Manager.QuittingNow();
            await h.Radio.Until(r => r.WritesTo(Left).Length == 1).WaitAsync(Limit);
            h.Manager.Ended(StationHarness.Profile(true), null);
            Assert.False(h.Manager.OffPending);
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);
            h.Clock.Advance(StationManager.OffDelay * 2);
            await h.QueueDrained();

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
            Assert.Contains("Base stations: the off after Monado stopped isn't sent (Mirador's off as it quits went out already)", h.Logged);
        }

        // Quitting twice (Windows ending as the window closes) decides once.
        using (var h = QuitHarness())
        {
            h.Manager.QuittingNow();
            h.Manager.QuittingNow();
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
        }

        // Windows ends while the tray's Quit still stops a Lighthouse session: the quit's off is the one.
        using (var h = QuitHarness())
        {
            h.Settings = h.Settings with { BaseStationsOnAtStart = false };
            h.Manager.Started(StationHarness.Profile(true), null);
            h.Manager.QuittingNow();
            h.Manager.EndingNow(StationHarness.Profile(true), null);
            h.Manager.QuittingNow();
            await h.Manager.FlushAsync(TimeSpan.FromSeconds(10)).WaitAsync(Limit);
            h.Manager.Ended(StationHarness.Profile(true), null);
            h.Clock.Advance(StationManager.OffDelay * 2);
            await h.QueueDrained();

            Assert.Equal(["00"], h.Radio.WritesTo(Left));
        }
    }

    [Fact]
    public async Task The_session_off_the_quit_relies_on_is_not_dropped()
    {
        using var h = QuitHarness();
        h.Settings = h.Settings with { BaseStationsOnAtStart = false };

        // The session's off is queued behind a command still sending as Mirador quits: it is the quit's off too.
        var hold = h.Radio.Hold(Right);
        var right = h.Manager.SwitchAsync(RightAddress, StationCommand.Sleep, CancellationToken.None);
        await h.Radio.Until(r => r.ConnectionsTo(Right).Length == 1).WaitAsync(Limit);
        h.Manager.Ended(StationHarness.Profile(true), null);
        await h.Time.Set(StationManager.OffDelay).WaitAsync(Limit);
        h.Clock.Advance(StationManager.OffDelay);
        Assert.True(h.Manager.OffPending);
        h.Manager.QuittingNow();
        Assert.Contains("Base stations: the off after Monado stopped is the one as Mirador quits", h.Logged);

        // Neither a start nor a later command drops it now.
        h.Manager.Started(StationHarness.Profile(true), null);
        var later = h.Manager.SwitchAsync(RightAddress, StationCommand.Sleep, CancellationToken.None);
        hold.SetResult();
        await Task.WhenAll(right, later).WaitAsync(Limit);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Equal(["00", "00"], h.Radio.WritesTo(Right));
        Assert.DoesNotContain(h.Logged, line => line.StartsWith("Base stations: Sleep not sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quitting_during_the_startup_scan_sends_the_off_and_no_on()
    {
        using var h = QuitHarness();
        h.Settings = h.Settings with { BaseStationsOnAtMiradorStart = true };

        // Both of Mirador's own settings on, and Mirador quits while its start still scans: the quit's off is on its way
        // when the scan ends.
        var hold = h.Radio.Hold(Left);
        var startup = h.Manager.StartupAsync(CancellationToken.None);
        h.Manager.QuittingNow();
        await h.Radio.Until(r => r.ConnectionsTo(Left).Length == 1).WaitAsync(Limit);
        await h.Time.Set(StationManager.TryLimit).WaitAsync(Limit);
        await h.EndScan(startup);
        hold.SetResult();
        await h.QueueDrained();

        // The off went out; no On followed it, before the flush (which would drop a queued one) or after it.
        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Contains("Base stations: On not sent (Mirador is quitting)", h.Logged);
        await h.Manager.FlushAsync(TimeSpan.FromSeconds(15)).WaitAsync(Limit);

        Assert.Equal(["00"], h.Radio.WritesTo(Left));
        Assert.Empty(h.Radio.ConnectionsTo(Right));
    }

    [Fact]
    public async Task A_scan_without_Bluetooth_leaves_seen_as_it_was()
    {
        using var h = new StationHarness();
        h.Settings = new AppSettings();
        var first = h.Manager.ScanAsync(CancellationToken.None);
        h.Hear(Left, LeftName);
        await h.EndScan(first);
        Assert.True(h.View(Left).Seen);

        foreach (var (state, why) in new[] { (BluetoothState.Off, "Bluetooth is off"), (BluetoothState.Missing, "this PC has no Bluetooth") })
        {
            h.Radio.State = state;
            await h.EndScan(h.Manager.ScanAsync(CancellationToken.None));

            Assert.True(h.View(Left).Seen);
            Assert.Contains($"Base stations: couldn't scan ({why})", h.Logged);
        }

        // Nor when Mirador couldn't listen.
        h.Radio.State = BluetoothState.Ready;
        h.Radio.WatchFailure = () => new InvalidOperationException("No watcher.");
        await h.EndScan(h.Manager.ScanAsync(CancellationToken.None));

        Assert.True(h.View(Left).Seen);
        Assert.Contains("Base stations: couldn't scan (Mirador couldn't listen for the base stations)", h.Logged);
        Assert.Single(h.Logged, line => line.StartsWith("Base stations: scanned for", StringComparison.Ordinal));
    }

    /// <summary>A harness with "Power off when Mirador quits" on, Left selected and Right not, both heard.</summary>
    private static StationHarness QuitHarness()
    {
        var h = new StationHarness();
        h.Settings = StationHarness.Live with { BaseStationsOffAtMiradorQuit = true };
        h.Known(new KnownStation(LeftAddress, LeftName, Selected: true), new KnownStation(RightAddress, RightName));
        using (h.Manager.Listen())
        {
            h.Hear(Left, LeftName);
            h.Hear(Right, RightName);
        }

        return h;
    }
}

/// <summary>
/// A manager on a fake radio, a fake clock and a station list in a temp folder. The tests move the clock only while the
/// manager's worker is idle (after a command's task, or <see cref="QueueDrained"/>) or waits on a timer the test has seen
/// it set (<see cref="TimerWatch.Set"/>): the fake clock can miss a timer that is due if another thread sets a timer
/// while the clock moves.
/// </summary>
internal sealed class StationHarness : IDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Mirador's defaults but with "Station state: Live while shown", the harness's own default: a <c>Listen()</c> then
    /// hears adverts, as the pages did before 2026-11-07. The tests of "From the last scan" (Mirador's default) set
    /// <c>new AppSettings()</c> themselves.
    /// </summary>
    public static readonly AppSettings Live = new(BaseStationsState: StationStateMode.Live);

    /// <summary>Switches "Station state tracking rate" to "When Relevant" and tells the manager, as the page does.</summary>
    public void WhenRelevant()
    {
        Settings = new AppSettings();
        Manager.SettingsChanged();
    }

    private readonly List<string> _log = [];
    private volatile AppSettings _settings = Live;
    private int _pauses;

    public StationHarness()
    {
        Time = new TimerWatch(Clock);
        Manager = NewManager();
    }

    public TempDir Dir { get; } = new();

    public FakeTimeProvider Clock { get; } = new();

    public TimerWatch Time { get; }

    public FakeStationRadio Radio { get; } = new();

    public StationManager Manager { get; }

    public string Path => Dir.File("basestations.json");

    /// <summary>What the manager's settings function returns from now on.</summary>
    public AppSettings Settings
    {
        get => _settings;
        set => _settings = value;
    }

    public string[] Logged
    {
        get
        {
            lock (_log)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>A profile whose Lighthouse toggle is <paramref name="lighthouse"/>, with no headset module and no variables.</summary>
    public static Profile Profile(bool? lighthouse) =>
        new("p", "Test", null, BuildSource.Mirador, [], new Dictionary<string, string>(), lighthouse);

    /// <summary>Another manager on the same radio, clock and file.</summary>
    public StationManager NewManager() => new(Radio, new StationStore(Path), () => Settings, Time, Log);

    /// <summary>Writes the station list, as an earlier Mirador would have.</summary>
    public void Known(params KnownStation[] stations) => new StationStore(Path).Save(stations);

    /// <summary>A 2.0 advert with the state byte where Finn's stations have it (null: no Valve data, as a 1.0 sends).</summary>
    public void Hear(ulong address, string name, byte? state = 0x03, bool random = true) =>
        Radio.Advertise(new Advert(address, random, name, state is { } s ? [0x00, 0x02, 0x07, 0x01, s, 0x06, 0x00] : null));

    public StationView View(ulong address) =>
        Manager.Stations.Single(s => s.Station.Address == StationProtocol.FormatAddress(address));

    /// <summary>
    /// Ends the scan that <paramref name="scan"/> is (a <see cref="StationManager.ScanAsync"/> or a
    /// <see cref="StationManager.StartupAsync"/>): moves the clock past <see cref="StationManager.ScanTime"/> and waits for
    /// it. A scan sets its timer before it returns, so the clock never moves ahead of it.
    /// </summary>
    public async Task EndScan(Task scan)
    {
        Clock.Advance(StationManager.ScanTime);
        await scan.WaitAsync(Limit);
    }

    /// <summary>
    /// Completes once everything queued before it has run: Identify of a station Mirador doesn't know sends nothing, and the
    /// queue runs in order.
    /// </summary>
    public Task QueueDrained() => Manager.IdentifyAsync("00:00:00:00:00:01", CancellationToken.None).WaitAsync(Limit);

    /// <summary>Moves the clock past the next <paramref name="count"/> pauses between rounds of tries, each once it is waited on.</summary>
    public async Task PassPauses(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _pauses++;
            await Time.Set(StationManager.TryPause, _pauses).WaitAsync(Limit);
            Clock.Advance(StationManager.TryPause);
        }
    }

    public void Dispose() => Dir.Dispose();

    private void Log(string text)
    {
        lock (_log)
        {
            _log.Add(text);
        }
    }
}
