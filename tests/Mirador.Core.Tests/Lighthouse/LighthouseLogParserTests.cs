using Mirador.Core.Lighthouse;

namespace Mirador.Core.Tests.Lighthouse;

public class LighthouseLogParserTests
{
    private static string Controller(string serial) => $" INFO [TrackedDeviceAdded] Found lighthouse controller: {serial}";
    private static string Tracker(string serial) => $" INFO [TrackedDeviceAdded] Found lighthouse tracker: {serial}";
    private static string Hmd(string serial) => $" INFO [TrackedDeviceAdded] Found lighthouse HMD: {serial}";

    [Fact]
    public void Nothing_is_known_at_first()
    {
        var p = new LighthouseLogParser();

        Assert.Null(p.HmdSerial);
        Assert.Empty(p.Devices);
    }

    [Fact]
    public void Reads_the_headset()
    {
        var p = new LighthouseLogParser();

        Assert.True(p.Feed("12:00:01.234 [I] steamvr_lh: Found lighthouse HMD: LHR-2A2E5F48"));

        Assert.Equal("LHR-2A2E5F48", p.HmdSerial);
        Assert.Empty(p.Devices);
    }

    [Fact]
    public void Controllers_and_trackers_are_listed()
    {
        var p = new LighthouseLogParser();

        p.Feed(Hmd("LHR-2A2E5F48"));
        p.Feed(Controller("LHR-AAAA0001"));
        p.Feed(Tracker("LHR-BBBB0002"));
        p.Feed(Controller("LHR-CCCC0003"));

        Assert.Equal(
            new[]
            {
                new TrackedDevice(TrackedKind.Controller, "LHR-AAAA0001"),
                new TrackedDevice(TrackedKind.Tracker, "LHR-BBBB0002"),
                new TrackedDevice(TrackedKind.Controller, "LHR-CCCC0003"),
            },
            p.Devices);
    }

    [Fact]
    public void A_serial_counts_once()
    {
        var p = new LighthouseLogParser();

        Assert.True(p.Feed(Controller("LHR-AAAA0001")));
        Assert.False(p.Feed(Controller("LHR-AAAA0001")));
        Assert.False(p.Feed(Tracker("LHR-AAAA0001")));
        Assert.True(p.Feed(Tracker("LHR-BBBB0002")));
        Assert.False(p.Feed(Tracker("LHR-BBBB0002")));

        Assert.Equal(2, p.Devices.Count);
    }

    [Fact]
    public void Base_station_lines_are_ignored()
    {
        var p = new LighthouseLogParser();

        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] Found lighthouse base station: LHB-1234ABCD"));
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] New device added: LHR-AAAA0001"));

        Assert.Null(p.HmdSerial);
        Assert.Empty(p.Devices);
    }

    [Fact]
    public void Feed_says_whether_what_it_knows_changed()
    {
        var p = new LighthouseLogParser();

        Assert.True(p.Feed(Hmd("LHR-2A2E5F48")));
        Assert.False(p.Feed(Hmd("LHR-2A2E5F48")));  // same headset again
        Assert.True(p.Feed(Hmd("LHR-CCCC0003")));   // a different one
        Assert.True(p.Feed(Controller("LHR-AAAA0001")));
        Assert.False(p.Feed(Controller("LHR-AAAA0001")));
    }

    [Fact]
    public void Other_lines_change_nothing()
    {
        var p = new LighthouseLogParser();

        Assert.False(p.Feed(""));
        Assert.False(p.Feed(" INFO [client_connected] Client 1 connected"));
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] Something else happened"));
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] Found lighthouse controller:")); // no serial
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] Found lighthouse tracker: "));
        Assert.False(p.Feed(" INFO [TrackedDeviceAdded] Found lighthouse HMD: "));

        Assert.Null(p.HmdSerial);
        Assert.Empty(p.Devices);
    }

    [Fact]
    public void Devices_is_a_snapshot()
    {
        var p = new LighthouseLogParser();
        p.Feed(Controller("LHR-AAAA0001"));
        var before = p.Devices;

        p.Feed(Tracker("LHR-BBBB0002"));

        Assert.Single(before);
        Assert.Equal(2, p.Devices.Count);
    }

    [Fact]
    public void A_line_break_left_on_the_line_does_not_end_up_in_the_serial()
    {
        var p = new LighthouseLogParser();

        p.Feed(Hmd("LHR-2A2E5F48") + "\r");
        p.Feed(Controller("LHR-AAAA0001") + "\r");

        Assert.Equal("LHR-2A2E5F48", p.HmdSerial);
        Assert.Equal("LHR-AAAA0001", p.Devices[0].Serial);
    }
}
