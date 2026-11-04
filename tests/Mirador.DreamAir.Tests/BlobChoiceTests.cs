namespace Mirador.DreamAir.Tests;

public class BlobChoiceTests
{
    [Fact]
    public void Detected_wins_and_is_remembered()
    {
        var d = BlobChoice.Resolve(new DreamAirState(BlobVariant: "K10_RAWH", BlobSource: "owner"), "K11_RAYH");

        Assert.Equal(new BlobDecision("K11_RAYH", false, new DreamAirState(BlobVariant: "K11_RAYH", BlobSource: "pimax")), d);
    }

    [Fact]
    public void Without_pimax_the_remembered_one()
    {
        var s = new DreamAirState(BlobVariant: "K10_RAWH", BlobSource: "owner");

        Assert.Equal(new BlobDecision("K10_RAWH", false, s), BlobChoice.Resolve(s, null));
    }

    [Fact]
    public void Nothing_known_is_the_tested_one_and_asks_the_owner()
    {
        var s = new DreamAirState();

        Assert.Equal(new BlobDecision("K11_RAWH", true, s), BlobChoice.Resolve(s, null));
    }

    [Theory]
    [InlineData("K12_RAWH")]
    [InlineData("..\\evil")]
    public void An_unknown_detected_or_remembered_variant_doesnt_count(string bad)
    {
        Assert.Equal("K11_RAWH", BlobChoice.Resolve(new DreamAirState(BlobVariant: bad), bad).Variant);
    }

    [Fact]
    public void The_first_serial_is_stored()
    {
        var s = BlobChoice.AfterUpload(new DreamAirState(BlobVariant: "K11_RAWH", BlobSource: "pimax"), "XR5DA-1");

        Assert.Equal(new DreamAirState(BlobVariant: "K11_RAWH", BlobSource: "pimax", BlobTrackerSerial: "XR5DA-1"), s);
    }

    [Fact]
    public void The_same_serial_changes_nothing()
    {
        var s = new DreamAirState(BlobVariant: "K11_RAWH", BlobSource: "pimax", BlobTrackerSerial: "XR5DA-1");

        Assert.Same(s, BlobChoice.AfterUpload(s, "XR5DA-1"));
        Assert.Same(s, BlobChoice.AfterUpload(s, null));
    }

    [Fact]
    public void A_different_serial_clears_the_remembered_variant()
    {
        var before = new DreamAirState(LastGoodDriver: "621.01", BlobVariant: "K11_RAWH", BlobSource: "owner", BlobTrackerSerial: "XR5DA-1");

        var after = BlobChoice.AfterUpload(before, "XR5DA-2");

        Assert.Equal(new DreamAirState(LastGoodDriver: "621.01", BlobTrackerSerial: "XR5DA-2"), after);
        Assert.True(BlobChoice.Cleared(before, after));
    }
}
