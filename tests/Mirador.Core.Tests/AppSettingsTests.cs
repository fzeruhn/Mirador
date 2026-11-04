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
}
