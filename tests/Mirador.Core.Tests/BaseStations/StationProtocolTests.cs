using Mirador.Core.BaseStations;

namespace Mirador.Core.Tests.BaseStations;

public class StationProtocolTests
{
    private static byte[] Padded(params byte[] head)
    {
        var bytes = new byte[20];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public void Names_give_versions()
    {
        Assert.Equal(StationVersion.V2, StationProtocol.VersionOf("LHB-6889F521"));
        Assert.Equal(StationVersion.V1, StationProtocol.VersionOf("HTC BS 7A3B"));
        Assert.Null(StationProtocol.VersionOf("lhb-6889F521"));
        Assert.Null(StationProtocol.VersionOf("AirPods"));
        Assert.Null(StationProtocol.VersionOf(""));
        Assert.Null(StationProtocol.VersionOf(null));
    }

    [Fact]
    public void Identifiers_are_the_known_ones()
    {
        Assert.Equal(Guid.Parse("00001523-1212-efde-1523-785feabcd124"), StationProtocol.V2Service);
        Assert.Equal(Guid.Parse("00001525-1212-efde-1523-785feabcd124"), StationProtocol.V2PowerCharacteristic);
        Assert.Equal(Guid.Parse("00008421-1212-efde-1523-785feabcd124"), StationProtocol.V2IdentifyCharacteristic);
        Assert.Equal(Guid.Parse("0000cb00-0000-1000-8000-00805f9b34fb"), StationProtocol.V1Service);
        Assert.Equal(Guid.Parse("0000cb01-0000-1000-8000-00805f9b34fb"), StationProtocol.V1PowerCharacteristic);
        Assert.Equal((ushort)0x055D, StationProtocol.ValveCompany);
    }

    [Fact]
    public void V2_bytes()
    {
        Assert.Equal([new byte[] { 0x01 }], StationProtocol.V2Writes(StationCommand.On));
        Assert.Equal([new byte[] { 0x02 }], StationProtocol.V2Writes(StationCommand.Standby));
        Assert.Equal([new byte[] { 0x00 }], StationProtocol.V2Writes(StationCommand.Sleep));
        Assert.Equal(new byte[] { 0x01 }, StationProtocol.V2IdentifyValue);
    }

    [Fact]
    public void V2_writes_cannot_change_shared_state()
    {
        StationProtocol.V2Writes(StationCommand.On)[0][0] = 0x7F;
        StationProtocol.V2IdentifyValue[0] = 0x7F;

        Assert.Equal(new byte[] { 0x01 }, StationProtocol.V2Writes(StationCommand.On)[0]);
        Assert.Equal(new byte[] { 0x01 }, StationProtocol.V2IdentifyValue);
    }

    [Theory]
    [InlineData("00020701000600", StationPower.Asleep)]
    [InlineData("00020701010600", StationPower.Waking)]
    [InlineData("00020701020600", StationPower.Waking)]
    [InlineData("00020701030600", StationPower.On)]
    [InlineData("00020701090600", StationPower.Unknown)]
    [InlineData("00020701", StationPower.Unknown)]
    [InlineData("", StationPower.Unknown)]
    public void Advert_states(string hex, StationPower expected)
    {
        Assert.Equal(expected, StationProtocol.AdvertState(Convert.FromHexString(hex)));
    }

    [Fact]
    public void V1_on_ignores_id()
    {
        var expected = Padded(0x12, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF);

        Assert.Equal(expected, StationProtocol.V1Write(StationCommand.On, 0xAABBCCDD));
        Assert.Equal(expected, StationProtocol.V1Write(StationCommand.On, null));
    }

    [Fact]
    public void V1_sleep_with_id_is_one_second()
    {
        var expected = Padded(0x12, 0x02, 0x00, 0x01, 0xDD, 0xCC, 0xBB, 0xAA);

        Assert.Equal(expected, StationProtocol.V1Write(StationCommand.Sleep, 0xAABBCCDD));
    }

    [Fact]
    public void V1_sleep_without_id_is_the_minute()
    {
        var expected = Padded(0x12, 0x01, 0x00, 0x3C, 0xFF, 0xFF, 0xFF, 0xFF);

        Assert.Equal(expected, StationProtocol.V1Write(StationCommand.Sleep, null));
    }

    [Fact]
    public void V1_standby_is_sleep()
    {
        Assert.Equal(StationProtocol.V1Write(StationCommand.Sleep, 0xAABBCCDD), StationProtocol.V1Write(StationCommand.Standby, 0xAABBCCDD));
        Assert.Equal(StationProtocol.V1Write(StationCommand.Sleep, null), StationProtocol.V1Write(StationCommand.Standby, null));
    }

    [Fact]
    public void Ids_parse()
    {
        Assert.True(StationProtocol.TryParseV1Id(" aabbccdd ", out var id));
        Assert.Equal(0xAABBCCDDu, id);
        Assert.True(StationProtocol.TryParseV1Id("AABBCCDD", out id));
        Assert.Equal(0xAABBCCDDu, id);

        Assert.False(StationProtocol.TryParseV1Id("AABBCCD", out _));
        Assert.False(StationProtocol.TryParseV1Id("AABBCCDDE", out _));
        Assert.False(StationProtocol.TryParseV1Id("GGBBCCDD", out _));
        Assert.False(StationProtocol.TryParseV1Id("0xAABBCC", out _));
        Assert.False(StationProtocol.TryParseV1Id(null, out _));
    }

    [Fact]
    public void Addresses_round_trip()
    {
        Assert.Equal("CE:36:CF:38:E9:CB", StationProtocol.FormatAddress(0xCE36CF38E9CB));
        Assert.Equal("00:00:00:00:00:0A", StationProtocol.FormatAddress(0xA));

        Assert.True(StationProtocol.TryParseAddress("CE:36:CF:38:E9:CB", out var address));
        Assert.Equal(0xCE36CF38E9CBul, address);
        Assert.True(StationProtocol.TryParseAddress("ce:36:cf:38:e9:cb", out address));
        Assert.Equal(0xCE36CF38E9CBul, address);

        Assert.False(StationProtocol.TryParseAddress("CE:36:CF", out _));
        Assert.False(StationProtocol.TryParseAddress("CE:36:CF:38:E9:CB:00", out _));
        Assert.False(StationProtocol.TryParseAddress("CE:36:CF:38:E9:GG", out _));
        Assert.False(StationProtocol.TryParseAddress(null, out _));
    }
}
