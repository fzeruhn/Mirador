using Mirador.Core.Modules;

namespace Mirador.DreamAir.Tests;

public class DreamAirLogParserTests
{
    private static StatusLine Line(DreamAirLogParser parser, string key) => parser.Lines.Single(l => l.Key == key);

    [Fact]
    public void Log_parser_reads_a_real_run()
    {
        // Monado's Dream Air and Tobii drivers and the layer, in the order of a run (prefixes as Monado writes them).
        string[] run =
        [
            "INFO [dreamair_open] Pimax Dream Air opened.",
            "[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, displayId 0x80061086",
            "INFO [tobii_tracker_open] Eye tracker connected.",
            "INFO [load_correction] Gaze correction loaded from C:\\Users\\finn\\AppData\\Local\\monado\\dreamair\\eye_correction_XR5DA-1.json, with a pupil size part.",
            "INFO [sync_calibration] Eye calibration applied (from file).",
            "[dvl] info: 90 presents in 1.0 s (89.5 Hz), 90 flips (89.5 Hz), 0 missed (0.00 %), late 0",
            "[dvl] info: 90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz), 3 missed (3.33 %), late 1",
        ];
        var parser = new DreamAirLogParser(new GeometryResult(true, "XR5DA-1", GeometryUploader.Uploaded, "log"));

        Assert.All(run, line => Assert.True(parser.Feed(line), line));

        Assert.Equal(
            new StatusLine[]
            {
                new("display", "Display", StatusLevel.Ok, "Direct mode, 7104×3840 at 90 Hz, 3 missed of 180 frames"),
                new("board", "Headset board", StatusLevel.Ok, "Connected"),
                new("eye", "Eye tracker", StatusLevel.Ok, "Calibration applied (from file), gaze correction on"),
            },
            parser.Lines);
    }

    [Fact]
    public void Before_monado_says_anything()
    {
        Assert.Equal(
            new StatusLine[]
            {
                new("display", "Display", StatusLevel.Info, "Waiting for the display"),
                new("board", "Headset board", StatusLevel.Info, "Not opened yet"),
                new("eye", "Eye tracker", StatusLevel.Ok, "Geometry uploaded"),
            },
            new DreamAirLogParser(new GeometryResult(true, null, GeometryUploader.Uploaded, "log")).Lines);
    }

    [Fact]
    public void The_display_line_before_any_frames()
    {
        var parser = new DreamAirLogParser();

        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 72.5 Hz, …");

        Assert.Equal(new StatusLine("display", "Display", StatusLevel.Ok, "Direct mode, 7104×3840 at 72.5 Hz"), Line(parser, "display"));
    }

    [Fact]
    public void Frame_totals_use_thousands_separators()
    {
        var parser = new DreamAirLogParser();
        parser.Feed("[dvl] info: display session open: Pimax Dream Air, 7104x3840 at 90.000 Hz, …");

        for (var i = 0; i < 60; i++)
        {
            parser.Feed("[dvl] info: 90 presents in 1.0 s (89.5 Hz), 90 flips (89.5 Hz), 0 missed (0.00 %), …");
        }

        Assert.Equal("Direct mode, 7104×3840 at 90 Hz, 0 missed of 5,400 frames", Line(parser, "display").Text);
    }

    [Fact]
    public void A_lost_board_is_an_error_with_monados_words()
    {
        var parser = new DreamAirLogParser();
        parser.Feed("INFO [dreamair] Pimax Dream Air opened.");

        parser.Feed("ERROR [dreamair_run] Lost the headset, the panels stay dark until Monado restarts.");

        Assert.Equal(
            new StatusLine("board", "Headset board", StatusLevel.Error, "Lost the headset, the panels stay dark until Monado restarts."),
            Line(parser, "board"));
    }

    [Fact]
    public void The_trackers_calibration_and_its_replacement()
    {
        var parser = new DreamAirLogParser();

        parser.Feed("Eye tracker connected.");
        Assert.Equal(new StatusLine("eye", "Eye tracker", StatusLevel.Info, "Connected"), Line(parser, "eye"));

        parser.Feed("Eye calibration applied (from the tracker).");
        Assert.Equal(new StatusLine("eye", "Eye tracker", StatusLevel.Ok, "Calibration applied (from the tracker)"), Line(parser, "eye"));

        parser.Feed("WARN [sync_calibration] Replaced the saved calibration in C:\\x\\eye_calibration_XR5DA-1.bin with the tracker's.");
        Assert.Equal(
            new StatusLine("eye", "Eye tracker", StatusLevel.Warning, "Pimax Play's calibration replaced the one Mirador kept (a backup is in Eye tracking)."),
            Line(parser, "eye"));
    }

    [Fact]
    public void A_lost_eye_tracker_is_a_warning()
    {
        var parser = new DreamAirLogParser();
        parser.Feed("Eye calibration applied (from file).");

        parser.Feed("WARN [tobii] Lost the eye tracker (LIBUSB_ERROR_NO_DEVICE), reconnecting.");

        Assert.Equal(new StatusLine("eye", "Eye tracker", StatusLevel.Warning, "Lost the eye tracker; reconnecting"), Line(parser, "eye"));
    }

    [Fact]
    public void A_failed_upload_starts_the_eye_line_and_stays_on_it()
    {
        var parser = new DreamAirLogParser(new GeometryResult(false, null, "The eye tracker isn't on USB. Is the headset connected and switched on?", "log"));

        Assert.Equal(
            new StatusLine("eye", "Eye tracker", StatusLevel.Warning, "The eye tracker isn't on USB. Is the headset connected and switched on?"),
            Line(parser, "eye"));

        parser.Feed("Eye calibration applied (from file).");

        Assert.Equal(
            new StatusLine("eye", "Eye tracker", StatusLevel.Warning, "Calibration applied (from file); geometry not uploaded, gaze about 10° off"),
            Line(parser, "eye"));
    }

    [Fact]
    public void Other_lines_change_nothing()
    {
        var parser = new DreamAirLogParser();

        Assert.False(parser.Feed("INFO [ipc_server] Client connected: hello_xr"));
        Assert.False(parser.Feed(""));
    }
}
