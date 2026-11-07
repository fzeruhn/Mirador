namespace Mirador.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Round_trip()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("settings.json"));
        var settings = new AppSettings(CloseToTray: false, CheckForUpdates: false, LastUpdateCheck: new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

        store.Save(settings);

        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void Defaults_without_a_file()
    {
        using var dir = new TempDir();

        Assert.Equal(new AppSettings(true, true, null), new AppSettingsStore(dir.File("settings.json")).Load());
    }

    [Fact]
    public void A_broken_file_gives_the_defaults_and_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{");

        Assert.Equal(new AppSettings(), new AppSettingsStore(dir.File("settings.json")).Load());
        Assert.True(File.Exists(dir.File("settings.json.bad")));
    }

    [Fact]
    public void Old_file_gets_base_station_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), """{"closeToTray": false}""");

        var settings = new AppSettingsStore(dir.File("settings.json")).Load();

        Assert.False(settings.CloseToTray);
        Assert.True(settings.BaseStationsOnAtStart);
        Assert.True(settings.BaseStationsOffAtStop);
    }

    [Fact]
    public void Old_file_with_off_mode_still_loads()
    {
        // Until 2026-11-07 "Off means: Sleep / Standby" was kept; off is always sleep now, and the old value is ignored.
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("settings.json"));
        File.WriteAllText(dir.File("settings.json"), """{"closeToTray": false, "baseStationsOnAtStart": false, "baseStationsOff": "Standby", "baseStationsOffAtStop": false}""");

        var settings = store.Load();

        Assert.Equal(new AppSettings(CloseToTray: false, BaseStationsOnAtStart: false, BaseStationsOffAtStop: false), settings);
        Assert.False(File.Exists(dir.File("settings.json.bad")));

        // Saved again, it is gone.
        store.Save(settings);

        var text = File.ReadAllText(dir.File("settings.json"));
        Assert.DoesNotContain("\"baseStationsOff\"", text);
        Assert.Contains("\"baseStationsOffAtStop\": false", text);
        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void Old_file_gets_the_new_base_station_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), """{"closeToTray": false, "baseStationsOnAtStart": false, "baseStationsOff": "Standby"}""");

        var settings = new AppSettingsStore(dir.File("settings.json")).Load();

        Assert.False(settings.BaseStationsOnAtStart);
        Assert.False(settings.BaseStationsOnAtMiradorStart);
        Assert.False(settings.BaseStationsOffAtMiradorQuit);
        Assert.Equal(StationStateMode.LastScan, settings.BaseStationsState);
    }

    [Fact]
    public void State_mode_is_saved_as_text()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("settings.json"));
        var settings = new AppSettings(BaseStationsOnAtMiradorStart: true, BaseStationsOffAtMiradorQuit: true, BaseStationsState: StationStateMode.Live);

        store.Save(settings);

        var text = File.ReadAllText(dir.File("settings.json"));
        Assert.Contains("\"baseStationsState\": \"Live\"", text);
        Assert.Contains("\"baseStationsOnAtMiradorStart\": true", text);
        Assert.Contains("\"baseStationsOffAtMiradorQuit\": true", text);
        Assert.Equal(settings, store.Load());
    }
}
