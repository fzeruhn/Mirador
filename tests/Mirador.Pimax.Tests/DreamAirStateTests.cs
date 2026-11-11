namespace Mirador.Pimax.Tests;

public class DreamAirStateTests
{
    [Fact]
    public void Round_trip()
    {
        using var dir = new TempDir();
        var store = new DreamAirStateStore(Path.Combine(dir.Path, "dreamair.json"));
        var state = new DreamAirState("621.01", "K11_RAWH", "pimax", "XR5DA-1");

        store.Save(state);

        Assert.Equal(state, store.Load());
    }

    [Fact]
    public void A_file_with_the_old_gaze_checks_result_still_loads()
    {
        // "Check my calibration" kept its last result here until it was taken out (2026-11-09).
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "dreamair.json");
        File.WriteAllText(path, """{ "lastGoodDriver": "621.01", "blobVariant": "K11_RAWH", "lastCheck": { "when": "2026-11-04T19:03:00+00:00", "medianDeg": 0.89, "worstDotDeg": 1.15, "dots": 9 } }""");

        Assert.Equal(new DreamAirState("621.01", "K11_RAWH"), new DreamAirStateStore(path).Load());
    }

    [Fact]
    public void A_missing_file_is_an_empty_state()
    {
        using var dir = new TempDir();

        Assert.Equal(new DreamAirState(), new DreamAirStateStore(Path.Combine(dir.Path, "dreamair.json")).Load());
    }

    [Fact]
    public void An_unknown_variant_is_dropped()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "dreamair.json");
        File.WriteAllText(path, """{ "lastGoodDriver": "621.01", "blobVariant": "..\\evil", "blobSource": "owner" }""");

        Assert.Equal(new DreamAirState("621.01"), new DreamAirStateStore(path).Load());
    }

    [Fact]
    public void A_broken_file_is_set_aside()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "dreamair.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal(new DreamAirState(), new DreamAirStateStore(path).Load());
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Update_changes_one_field()
    {
        using var dir = new TempDir();
        var store = new DreamAirStateStore(Path.Combine(dir.Path, "dreamair.json"));
        store.Save(new DreamAirState(BlobVariant: "K10_RAYH"));

        store.Update(s => s with { LastGoodDriver = "621.01" });

        Assert.Equal(new DreamAirState("621.01", "K10_RAYH"), store.Load());
    }
}
