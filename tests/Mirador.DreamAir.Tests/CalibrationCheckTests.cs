namespace Mirador.DreamAir.Tests;

public class CalibrationCheckTests
{
    [Fact]
    public void A_good_result_reads_as_the_plan_says()
    {
        Assert.Equal(("Median 0.89°, worst dot 1.15° (9 dots)", true), CalibrationCheck.Describe(0.894, 1.149, 9));
    }

    [Fact]
    public void A_worst_dot_over_two_and_a_half_degrees_asks_for_a_new_calibration()
    {
        Assert.Equal(("Median 1.20°, worst dot 2.60° (9 dots): calibrate again in Pimax Play.", false), CalibrationCheck.Describe(1.2, 2.6, 9));
        Assert.True(CalibrationCheck.Describe(1.2, 2.5, 9).Good);
    }

    private const string GazeHeader = "now_ns,display_ns,phase,placed,tx,ty,tz,h_flags,h_px,h_py,h_pz,h_qx,h_qy,h_qz,h_qw,g_flags,g_px,g_py,g_pz,g_qx,g_qy,g_qz,g_qw,g_sample_ns,gv_flags,gv_qx,gv_qy,gv_qz,gv_qw,background";

    /// <summary>A gaze_world row (its columns as of 2026-10-29) looking straight ahead from the origin.</summary>
    private const string StraightAhead = "1,2,dots,1,0,0,-2,15,0,0,0,0,0,0,1,3,0,0,0,0,0,0,1,3,3,0,0,0,1,\n";

    [Fact]
    public void The_check_reads_gaze_worlds_two_files()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "gaze_targets.csv"), "index,x,y,z\n0,0,0,-2\n");
        File.WriteAllText(Path.Combine(dir.Path, "gaze_head.csv"), "now_ns,h_flags\n1,15\n");
        File.WriteAllText(Path.Combine(dir.Path, "gaze_app.csv"), GazeHeader + "\n" + string.Concat(Enumerable.Repeat(StraightAhead, 25)) + "1,2,dots,1,0,0");

        Assert.True(CalibrationCheck.HasResults(dir.Path));
        var result = CalibrationCheck.Measure(dir.Path);

        Assert.NotNull(result);
        Assert.Equal((25, 1, 25), (result.Frames, result.Dots, result.FixationFrames));
        Assert.Equal(0.0, result.MedianDeg, 1e-9);
    }

    [Fact]
    public void An_empty_folder_has_no_results()
    {
        using var dir = new TempDir();

        Assert.False(CalibrationCheck.HasResults(dir.Path));
        Assert.False(CalibrationCheck.HasResults(Path.Combine(dir.Path, "gone")));
        Assert.Null(CalibrationCheck.Measure(dir.Path));
    }

    [Fact]
    public void Without_the_gaze_file_there_is_nothing_to_measure()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "gaze_targets.csv"), "index,x,y,z\n0,0,0,-2\n");

        Assert.False(CalibrationCheck.HasResults(dir.Path));
        Assert.Null(CalibrationCheck.Measure(dir.Path));
    }
}
