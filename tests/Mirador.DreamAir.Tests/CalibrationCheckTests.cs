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

    [Fact]
    public void The_two_files_are_told_apart_by_their_columns()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "a.csv"), "dot,x,y,z\n0,0,0,-2\n");
        File.WriteAllText(
            Path.Combine(dir.Path, "b.csv"),
            "t,placed,g_flags,g_px,g_py,g_pz,g_qx,g_qy,g_qz,g_qw\n" + string.Concat(Enumerable.Repeat("0.1,1,3,0,0,0,0,0,0,1\n", 25)));

        var result = CalibrationCheck.Measure(dir.Path);

        Assert.NotNull(result);
        Assert.Equal((1, 25), (result.Dots, result.FixationFrames));
        Assert.Equal(0.0, result.MedianDeg, 1e-9);
    }

    [Fact]
    public void Without_both_files_there_is_nothing_to_measure()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "a.csv"), "dot,x,y,z\n0,0,0,-2\n");

        Assert.Null(CalibrationCheck.Measure(dir.Path));
    }
}
