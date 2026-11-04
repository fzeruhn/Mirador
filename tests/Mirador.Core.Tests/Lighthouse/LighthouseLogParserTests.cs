using Mirador.Core.Lighthouse;

namespace Mirador.Core.Tests.Lighthouse;

public class LighthouseLogParserTests
{
    private static string Added(string serial) => $" INFO [TrackedDeviceAdded] New device added: {serial}";
    private static string Hmd(string serial) => $" INFO [TrackedDeviceAdded] Found lighthouse HMD: {serial}";

    [Fact]
    public void Reads_the_headset_and_counts_the_other_devices()
    {
        var p = new LighthouseLogParser();

        p.Feed(" INFO [TrackedDeviceAdded] New device added: LHR-2A2E5F48");
        p.Feed(" INFO [TrackedDeviceAdded] Found lighthouse HMD: LHR-2A2E5F48");
        p.Feed(" INFO [TrackedDeviceAdded] New device added: LHR-AAAA0001");

        Assert.Equal("LHR-2A2E5F48", p.HmdSerial);
        Assert.Equal(1, p.OtherDevices);
    }

    [Fact]
    public void Nothing_is_known_at_first()
    {
        var p = new LighthouseLogParser();

        Assert.Null(p.HmdSerial);
        Assert.Equal(0, p.OtherDevices);
    }

    [Fact]
    public void The_headset_is_not_counted_among_the_others_whichever_line_comes_first()
    {
        var hmdFirst = new LighthouseLogParser();
        hmdFirst.Feed(Hmd("LHR-2A2E5F48"));
        hmdFirst.Feed(Added("LHR-2A2E5F48"));

        var addedFirst = new LighthouseLogParser();
        addedFirst.Feed(Added("LHR-2A2E5F48"));
        addedFirst.Feed(Hmd("LHR-2A2E5F48"));

        Assert.Equal(0, hmdFirst.OtherDevices);
        Assert.Equal(0, addedFirst.OtherDevices);
        Assert.Equal("LHR-2A2E5F48", hmdFirst.HmdSerial);
        Assert.Equal("LHR-2A2E5F48", addedFirst.HmdSerial);
    }

    [Fact]
    public void Feed_says_whether_what_it_knows_changed()
    {
        var p = new LighthouseLogParser();

        Assert.True(p.Feed(Added("LHR-2A2E5F48")));  // one other device
        Assert.True(p.Feed(Hmd("LHR-2A2E5F48")));    // it is the headset: headset set, others back to none
        Assert.True(p.Feed(Added("LHR-AAAA0001")));
        Assert.False(p.Feed(Added("LHR-AAAA0001")));  // seen already
        Assert.False(p.Feed(Hmd("LHR-2A2E5F48")));    // same headset again
        Assert.False(p.Feed(Added("LHR-2A2E5F48")));  // the headset again
        Assert.True(p.Feed(Added("LHR-BBBB0002")));
    }

    [Fact]
    public void Other_lines_change_nothing()
    {
        var p = new LighthouseLogParser();

        Assert.False(p.Feed(""));
        Assert.False(p.Feed(" INFO [client_connected] Client 1 connected"));
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] Something else happened"));
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] New device added:")); // no serial
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] Found lighthouse HMD: "));

        Assert.Null(p.HmdSerial);
        Assert.Equal(0, p.OtherDevices);
    }

    [Fact]
    public void Each_other_device_is_counted_once()
    {
        var p = new LighthouseLogParser();

        p.Feed(Added("LHR-AAAA0001"));
        p.Feed(Added("LHR-AAAA0001"));
        p.Feed(Added("LHR-BBBB0002"));
        p.Feed(Added("LHR-AAAA0001"));

        Assert.Equal(2, p.OtherDevices);
    }

    [Fact]
    public void A_different_headset_replaces_the_first_and_the_old_one_counts_as_another_device()
    {
        var p = new LighthouseLogParser();
        p.Feed(Added("LHR-2A2E5F48"));
        p.Feed(Hmd("LHR-2A2E5F48"));

        Assert.True(p.Feed(Hmd("LHR-CCCC0003")));

        Assert.Equal("LHR-CCCC0003", p.HmdSerial);
        Assert.Equal(1, p.OtherDevices);
    }

    [Fact]
    public void A_line_break_left_on_the_line_does_not_end_up_in_the_serial()
    {
        var p = new LighthouseLogParser();

        p.Feed(Hmd("LHR-2A2E5F48") + "\r");

        Assert.Equal("LHR-2A2E5F48", p.HmdSerial);
    }
}
