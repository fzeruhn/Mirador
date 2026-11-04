using System.Globalization;
using System.Text;

namespace Mirador.DreamAir.Tests;

public class GazeAccuracyTests
{
    private const string GazeHeader = "t,placed,g_flags,g_px,g_py,g_pz,g_qx,g_qy,g_qz,g_qw";

    /// <summary>Turning by <paramref name="yawDeg"/> about +Y, then <paramref name="pitchDeg"/> about the turned +X, as (x, y, z, w).</summary>
    private static (double X, double Y, double Z, double W) Orientation(double yawDeg, double pitchDeg)
    {
        double hy = yawDeg * Math.PI / 360, hp = pitchDeg * Math.PI / 360;
        (double X, double Y, double Z, double W) yaw = (0, Math.Sin(hy), 0, Math.Cos(hy));
        (double X, double Y, double Z, double W) pitch = (Math.Sin(hp), 0, 0, Math.Cos(hp));
        return Multiply(yaw, pitch);
    }

    private static (double X, double Y, double Z, double W) Multiply((double X, double Y, double Z, double W) a, (double X, double Y, double Z, double W) b) =>
    (
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    private static readonly (double Yaw, double Pitch)[] Grid =
        [.. from pitch in new[] { 10.0, 0, -10 } from yaw in new[] { -15.0, 0, 15 } select (yaw, pitch)];

    /// <summary>The nine dots, 2 m from the eye, 15° apart in yaw and 10° in pitch.</summary>
    private static string Targets()
    {
        var csv = new StringBuilder("dot,x,y,z\n");
        for (var i = 0; i < Grid.Length; i++)
        {
            var q = Orientation(Grid[i].Yaw, Grid[i].Pitch);
            var p = GazeAccuracy.Rotate(new GazeAccuracy.Vec(0, 0, -2), q.X, q.Y, q.Z, q.W);
            csv.Append(CultureInfo.InvariantCulture, $"{i},{p.X:R},{p.Y:R},{p.Z:R}\n");
        }

        return csv.ToString();
    }

    /// <summary>A gaze row at a dot, off by <paramref name="offYawDeg"/> in the dot's own yaw.</summary>
    private static string Row(int dot, double offYawDeg, int flags = 3, string placed = "1")
    {
        var q = Multiply(Orientation(Grid[dot].Yaw, Grid[dot].Pitch), Orientation(offYawDeg, 0));
        return string.Create(CultureInfo.InvariantCulture, $"0.1,{placed},{flags},0,0,0,{q.X:R},{q.Y:R},{q.Z:R},{q.W:R}\n");
    }

    private static GazeAccuracyResult? Compute(string gaze) =>
        GazeAccuracy.Compute(new StringReader(GazeHeader + "\n" + gaze), new StringReader(Targets()));

    private static string ThirtyAtEveryDot(double offYawDeg)
    {
        var rows = new StringBuilder();
        for (var dot = 0; dot < 9; dot++)
        {
            for (var i = 0; i < 30; i++)
            {
                rows.Append(Row(dot, offYawDeg));
            }
        }

        return rows.ToString();
    }

    [Fact]
    public void Nine_dots_one_degree_off()
    {
        var result = Compute(ThirtyAtEveryDot(1.0));

        Assert.NotNull(result);
        Assert.Equal(1.0, result.MedianDeg, 1e-6);
        Assert.Equal(1.0, result.MedianOfDotMediansDeg, 1e-6);
        Assert.Equal(1.0, result.WorstDotDeg, 1e-6);
        Assert.Equal(9, result.Dots);
        Assert.Equal((270, 270, 270), (result.Frames, result.ValidFrames, result.FixationFrames));
    }

    [Fact]
    public void Frames_seven_degrees_off_are_no_fixations()
    {
        var result = Compute(ThirtyAtEveryDot(1.0) + string.Concat(Enumerable.Repeat(Row(4, 7.0), 50)));

        Assert.NotNull(result);
        Assert.Equal(1.0, result.MedianDeg, 1e-6);
        Assert.Equal(320, result.ValidFrames);
        Assert.Equal(270, result.FixationFrames);
    }

    [Fact]
    public void Frames_with_one_eye_or_no_dot_placed_arent_valid()
    {
        var result = Compute(ThirtyAtEveryDot(1.0) + string.Concat(Enumerable.Repeat(Row(4, 3.0, flags: 1), 50)) + Row(4, 3.0, placed: "0"));

        Assert.NotNull(result);
        Assert.Equal(321, result.Frames);
        Assert.Equal(270, result.ValidFrames);
        Assert.Equal(1.0, result.WorstDotDeg, 1e-6);
    }

    [Fact]
    public void An_incomplete_last_row_is_ignored()
    {
        var result = Compute(ThirtyAtEveryDot(1.0) + "0.1,1,3,0,0,0,0.1,0.2");

        Assert.NotNull(result);
        Assert.Equal(271, result.Frames);
        Assert.Equal(270, result.ValidFrames);
    }

    [Fact]
    public void A_dot_with_19_frames_is_not_counted()
    {
        var nineteen = new StringBuilder();
        for (var dot = 1; dot < 9; dot++)
        {
            for (var i = 0; i < 30; i++)
            {
                nineteen.Append(Row(dot, 1.0));
            }
        }

        for (var i = 0; i < 19; i++)
        {
            nineteen.Append(Row(0, 2.0));
        }

        var result = Compute(nineteen.ToString());

        Assert.NotNull(result);
        Assert.Equal(8, result.Dots);
        Assert.Equal(1.0, result.WorstDotDeg, 1e-6);
    }

    [Fact]
    public void Worst_dot_is_the_largest_dot_median()
    {
        var rows = new StringBuilder();
        for (var dot = 0; dot < 9; dot++)
        {
            for (var i = 0; i < 30; i++)
            {
                rows.Append(Row(dot, dot == 8 ? 2.5 : 0.5));
            }
        }

        var result = Compute(rows.ToString());

        Assert.NotNull(result);
        Assert.Equal(2.5, result.WorstDotDeg, 1e-6);
        Assert.Equal(0.5, result.MedianDeg, 1e-6);
        Assert.Equal(0.5, result.MedianOfDotMediansDeg, 1e-6);
    }

    [Fact]
    public void No_dot_with_enough_frames_is_null()
    {
        Assert.Null(Compute(string.Concat(Enumerable.Repeat(Row(4, 1.0), 19))));
    }

    [Fact]
    public void Files_without_the_columns_are_null()
    {
        Assert.Null(GazeAccuracy.Compute(new StringReader("t,x\n1,2\n"), new StringReader(Targets())));
        Assert.Null(GazeAccuracy.Compute(new StringReader(GazeHeader + "\n" + ThirtyAtEveryDot(1)), new StringReader("a,b\n1,2\n")));
    }

    [Fact]
    public void The_median_of_an_even_count_is_the_mean_of_the_middle_two()
    {
        Assert.Equal(2.5, GazeAccuracy.Median([4.0, 1, 3, 2]));
        Assert.Equal(3.0, GazeAccuracy.Median([5.0, 1, 3]));
    }
}
