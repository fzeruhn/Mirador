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
        Assert.Equal(OffMode.Sleep, settings.BaseStationsOff);
    }

    [Fact]
    public void Off_mode_is_saved_as_text()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("settings.json"));
        var settings = new AppSettings(BaseStationsOnAtStart: false, BaseStationsOff: OffMode.Standby);

        store.Save(settings);

        Assert.Contains("\"baseStationsOff\": \"Standby\"", File.ReadAllText(dir.File("settings.json")));
        Assert.Equal(settings, store.Load());
    }
}
