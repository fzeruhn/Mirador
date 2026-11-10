using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class MonadoSettingsStoreTests
{
    [Fact]
    public void Settings_round_trip_and_default_when_missing()
    {
        using var dir = new TempDir();
        var store = new MonadoSettingsStore(dir.File("monado-settings.json"));

        Assert.Equal(new MonadoSettings(), store.Load());
        Assert.Equal(new MonadoSettings(3000, 100, 6, false), store.Load());
        Assert.False(File.Exists(dir.File("monado-settings.json")));

        var custom = new MonadoSettings(LighthouseWaitMs: 8000, RenderScalePercent: 140, CompositorMinTimeMs: 9, StopWhenLastAppCloses: true);
        store.Save(custom);
        Assert.Equal(custom, store.Load());
    }

    [Fact]
    public void Settings_are_clamped_and_rounded_on_load()
    {
        using var dir = new TempDir();
        var path = dir.File("monado-settings.json");
        var store = new MonadoSettingsStore(path);

        File.WriteAllText(path, """{"lighthouseWaitMs":99999,"renderScalePercent":10,"compositorMinTimeMs":0,"stopWhenLastAppCloses":true}""");
        Assert.Equal(new MonadoSettings(30000, 50, 1, true), store.Load());

        File.WriteAllText(path, """{"lighthouseWaitMs":2400,"renderScalePercent":103,"compositorMinTimeMs":99}""");
        Assert.Equal(new MonadoSettings(2000, 105, 20, false), store.Load());
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(3000, 3000)]
    [InlineData(0, 1000)]
    [InlineData(-5000, 1000)]
    [InlineData(1499, 1000)]
    [InlineData(1500, 2000)]
    [InlineData(2400, 2000)]
    [InlineData(2500, 3000)]
    [InlineData(30000, 30000)]
    [InlineData(99999, 30000)]
    [InlineData(int.MaxValue, 30000)]
    [InlineData(int.MinValue, 1000)]
    public void Lighthouse_wait_is_whole_seconds_from_1_to_30(int given, int expected) =>
        Assert.Equal(expected, new MonadoSettings(LighthouseWaitMs: given).Clamped().LighthouseWaitMs);

    [Theory]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(10, 50)]
    [InlineData(102, 100)]
    [InlineData(103, 105)]
    [InlineData(107, 105)]
    [InlineData(108, 110)]
    [InlineData(200, 200)]
    [InlineData(500, 200)]
    [InlineData(int.MaxValue, 200)]
    [InlineData(int.MinValue, 50)]
    public void Render_scale_is_steps_of_5_from_50_to_200(int given, int expected) =>
        Assert.Equal(expected, new MonadoSettings(RenderScalePercent: given).Clamped().RenderScalePercent);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    [InlineData(int.MaxValue, 20)]
    [InlineData(int.MinValue, 1)]
    public void Compositor_minimum_time_is_whole_milliseconds_from_1_to_20(int given, int expected) =>
        Assert.Equal(expected, new MonadoSettings(CompositorMinTimeMs: given).Clamped().CompositorMinTimeMs);

    [Fact]
    public void Clamping_leaves_the_stop_switch_alone()
    {
        Assert.True(new MonadoSettings(StopWhenLastAppCloses: true).Clamped().StopWhenLastAppCloses);
        Assert.False(new MonadoSettings().Clamped().StopWhenLastAppCloses);
    }

    [Fact]
    public void Settings_missing_from_the_file_fall_back_to_their_defaults()
    {
        using var dir = new TempDir();
        var path = dir.File("monado-settings.json");
        File.WriteAllText(path, """{"renderScalePercent":150}""");

        Assert.Equal(new MonadoSettings(RenderScalePercent: 150), new MonadoSettingsStore(path).Load());
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"lighthouseWaitMs":"three seconds"}""")]
    [InlineData("""{"renderScalePercent":1.5}""")]
    public void Corrupt_file_is_kept_as_bad_and_defaults_load(string text)
    {
        using var dir = new TempDir();
        var path = dir.File("monado-settings.json");
        File.WriteAllText(path, text);

        Assert.Equal(new MonadoSettings(), new MonadoSettingsStore(path).Load());

        Assert.False(File.Exists(path));
        Assert.Equal(text, File.ReadAllText(path + ".bad"));
    }
}
