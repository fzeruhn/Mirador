using Mirador.Core.BaseStations;

namespace Mirador.Core.Tests.BaseStations;

public class StationStoreTests
{
    [Fact]
    public void Missing_file_is_empty()
    {
        using var dir = new TempDir();

        Assert.Empty(new StationStore(dir.File("basestations.json")).Load());
    }

    [Fact]
    public void Round_trips()
    {
        using var dir = new TempDir();
        var store = new StationStore(dir.File("basestations.json"));
        KnownStation[] stations =
        [
            new("CE:36:CF:38:E9:CB", "LHB-ABCD1234", Selected: true, CustomName: "Left corner"),
            new("01:02:03:04:05:06", "HTC BS 1A2B3C4D", Id: "1A2B3C4D"),
        ];

        store.Save(stations);

        Assert.Equal(stations, store.Load());
        Assert.False(File.Exists(dir.File("basestations.json.bad")));
    }

    [Fact]
    public void Version_and_display_name_come_from_the_name_and_the_custom_name()
    {
        Assert.Equal(StationVersion.V2, new KnownStation("CE:36:CF:38:E9:CB", "LHB-1").Version);
        Assert.Equal(StationVersion.V1, new KnownStation("CE:36:CF:38:E9:CB", "HTC BS 1").Version);
        Assert.Equal("LHB-1", new KnownStation("CE:36:CF:38:E9:CB", "LHB-1").DisplayName);
        Assert.Equal("Mine", new KnownStation("CE:36:CF:38:E9:CB", "LHB-1", CustomName: "Mine").DisplayName);
        Assert.Throws<InvalidOperationException>(() => new KnownStation("CE:36:CF:38:E9:CB", "Other").Version);
    }

    [Fact]
    public void Same_address_twice_keeps_the_first_and_a_bad_copy()
    {
        using var dir = new TempDir();
        var path = dir.File("basestations.json");
        File.WriteAllText(path, """
            {"stations":[{"address":"CE:36:CF:38:E9:CB","name":"LHB-1"},{"address":"ce:36:cf:38:e9:cb","name":"LHB-2"}]}
            """);

        var stations = new StationStore(path).Load();

        var station = Assert.Single(stations);
        Assert.Equal("LHB-1", station.Name);
        Assert.True(File.Exists(path + ".bad"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Addresses_come_back_in_the_standard_form()
    {
        using var dir = new TempDir();
        var path = dir.File("basestations.json");
        File.WriteAllText(path, """{"stations":[{"address":"ce:36:cf:38:e9:cb","name":"LHB-1"}]}""");

        Assert.Equal("CE:36:CF:38:E9:CB", Assert.Single(new StationStore(path).Load()).Address);
        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Unknown_or_empty_name_and_bad_address_are_left_out()
    {
        using var dir = new TempDir();
        var path = dir.File("basestations.json");
        File.WriteAllText(path, """
            {"stations":[
              {"address":"CE:36:CF:38:E9:CB","name":"LHB-1"},
              {"address":"01:02:03:04:05:06","name":"Some speaker"},
              {"address":"01:02:03:04:05:07","name":""},
              {"address":"01:02:03:04:05:08"},
              {"address":"not an address","name":"LHB-2"},
              {"name":"LHB-3"},
              null
            ]}
            """);

        var stations = new StationStore(path).Load();

        Assert.Equal(["LHB-1"], stations.Select(s => s.Name));
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Bad_id_becomes_null_with_a_bad_copy()
    {
        using var dir = new TempDir();
        var path = dir.File("basestations.json");
        File.WriteAllText(path, """{"stations":[{"address":"01:02:03:04:05:06","name":"HTC BS 1","id":"xyz"}]}""");

        var station = Assert.Single(new StationStore(path).Load());

        Assert.Null(station.Id);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Long_custom_name_is_cut()
    {
        using var dir = new TempDir();
        var path = dir.File("basestations.json");
        File.WriteAllText(path, $$"""
            {"stations":[
              {"address":"01:02:03:04:05:06","name":"LHB-1","customName":"{{new string('x', 60)}}"},
              {"address":"01:02:03:04:05:07","name":"LHB-2","customName":"  Padded  "},
              {"address":"01:02:03:04:05:08","name":"LHB-3","customName":"   "}
            ]}
            """);

        var stations = new StationStore(path).Load();

        Assert.Equal(new string('x', KnownStation.MaxNameLength), stations[0].CustomName);
        Assert.Equal("Padded", stations[1].CustomName);
        Assert.Null(stations[2].CustomName);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Broken_json_is_set_aside()
    {
        using var dir = new TempDir();
        var path = dir.File("basestations.json");
        File.WriteAllText(path, "{not json");

        Assert.Empty(new StationStore(path).Load());

        Assert.False(File.Exists(path));
        Assert.Equal("{not json", File.ReadAllText(path + ".bad"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"stations":null}""")]
    public void A_file_without_a_station_list_is_set_aside(string text)
    {
        using var dir = new TempDir();
        var path = dir.File("basestations.json");
        File.WriteAllText(path, text);

        Assert.Empty(new StationStore(path).Load());

        Assert.False(File.Exists(path));
        Assert.Equal(text, File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void ToString_of_a_station_with_an_unknown_name_does_not_throw()
    {
        var text = new KnownStation("CE:36:CF:38:E9:CB", "Some speaker").ToString();

        Assert.Contains("Some speaker", text);
    }
}
