using System.Collections.Concurrent;
using Mirador.Core.BaseStations;

namespace Mirador.Core.Tests.BaseStations;

/// <summary>Talks to the real base stations; skipped unless MIRADOR_BLUETOOTH_TESTS=1 (see <see cref="BluetoothFactAttribute"/>).</summary>
public class WinRtStationRadioTests
{
    private static string[] StationNames
    {
        get
        {
            var names = (Environment.GetEnvironmentVariable(BluetoothFactAttribute.StationsVariable) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.True(names.Length > 0, $"Set {BluetoothFactAttribute.StationsVariable} to the stations' Bluetooth names, comma-separated.");
            return names;
        }
    }

    private static async Task<WinRtStationRadio> StartedRadio()
    {
        var radio = new WinRtStationRadio();
        await radio.StartAsync();
        Assert.Equal(BluetoothState.Ready, radio.State);
        return radio;
    }

    private static async Task<Advert> Find(ConcurrentDictionary<string, Advert> heard, string name, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            if (heard.TryGetValue(name, out var advert))
            {
                return advert;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"{name} was not heard within {within.TotalSeconds:F0} s.");
    }

    private static async Task WaitForPower(ConcurrentDictionary<string, Advert> heard, string name, StationPower power, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        var last = StationPower.Unknown;
        while (DateTime.UtcNow < until)
        {
            if (heard.TryGetValue(name, out var advert) && advert.ValveData is { } data)
            {
                last = StationProtocol.AdvertState(data);
                if (last == power)
                {
                    return;
                }
            }

            await Task.Delay(300);
        }

        throw new Xunit.Sdk.XunitException($"{name} did not show {power} within {within.TotalSeconds:F0} s (last seen: {last}).");
    }

    private static async Task Send(IStationRadio radio, Advert station, StationCommand command)
    {
        await using var link = await radio.ConnectAsync(station.Address, station.RandomAddress, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        foreach (var value in StationProtocol.V2Writes(command))
        {
            await link.WriteAsync(StationProtocol.V2Service, StationProtocol.V2PowerCharacteristic, value, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void State_is_ready_before_start()
    {
        Assert.Equal(BluetoothState.Ready, new WinRtStationRadio().State);
    }

    [BluetoothFact]
    public async Task State_is_ready()
    {
        var radio = new WinRtStationRadio();
        Assert.Equal(BluetoothState.Ready, radio.State);

        await radio.StartAsync();

        Assert.Equal(BluetoothState.Ready, radio.State);
    }

    [BluetoothFact]
    public async Task Watch_hears_the_stations()
    {
        var radio = await StartedRadio();
        var heard = new ConcurrentDictionary<string, Advert>();

        using (radio.Watch(a => heard[a.Name] = a))
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
        }

        var names = StationNames;
        foreach (var name in names)
        {
            Assert.True(heard.TryGetValue(name, out var advert), $"{name} was not heard.");
            Assert.NotNull(advert!.ValveData);
        }
    }

    [BluetoothFact]
    public async Task Wake_and_sleep_show_in_the_advert()
    {
        var radio = await StartedRadio();
        var heard = new ConcurrentDictionary<string, Advert>();
        var name = StationNames[0];

        using var watch = radio.Watch(a => heard[a.Name] = a);
        var station = await Find(heard, name, TimeSpan.FromSeconds(15));
        try
        {
            await Send(radio, station, StationCommand.On);
            await WaitForPower(heard, name, StationPower.On, TimeSpan.FromSeconds(30));
        }
        finally
        {
            await Send(radio, station, StationCommand.Sleep);
        }

        await WaitForPower(heard, name, StationPower.Asleep, TimeSpan.FromSeconds(15));
    }

    [BluetoothFact]
    public async Task Identify_is_accepted()
    {
        var radio = await StartedRadio();
        var heard = new ConcurrentDictionary<string, Advert>();
        var name = StationNames[0];

        using var watch = radio.Watch(a => heard[a.Name] = a);
        var station = await Find(heard, name, TimeSpan.FromSeconds(15));
        try
        {
            await Send(radio, station, StationCommand.On);
            await WaitForPower(heard, name, StationPower.On, TimeSpan.FromSeconds(30));

            await using var link = await radio.ConnectAsync(station.Address, station.RandomAddress, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            await link.WriteAsync(StationProtocol.V2Service, StationProtocol.V2IdentifyCharacteristic, StationProtocol.V2IdentifyValue, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await Send(radio, station, StationCommand.Sleep);
        }

        await WaitForPower(heard, name, StationPower.Asleep, TimeSpan.FromSeconds(15));
    }

    [BluetoothFact]
    public async Task Unknown_address_throws_station_exception()
    {
        var radio = await StartedRadio();

        // Windows may hand back a device for an address it has never seen; the failure then comes at the first write.
        await Assert.ThrowsAsync<StationException>(async () =>
        {
            await using var link = await radio.ConnectAsync(0x0000_DEAD_BEEF_01, true, CancellationToken.None);
            await link.WriteAsync(StationProtocol.V2Service, StationProtocol.V2PowerCharacteristic, [0x01], CancellationToken.None);
        }).WaitAsync(TimeSpan.FromSeconds(60));
    }
}
