using System.Globalization;

namespace Mirador.Pimax;

/// <summary>How well gaze hit the check's dots.</summary>
/// <param name="Frames">Complete rows read from the gaze file once a dot was placed.</param>
/// <param name="ValidFrames">Those rows with a valid gaze (both eyes' flags set).</param>
/// <param name="FixationFrames">Valid rows within <see cref="GazeAccuracy.FixationDeg"/> of their dot, whatever the dot's count.</param>
/// <param name="MedianDeg">The median angle between gaze and dot over all the fixation rows.</param>
/// <param name="MedianOfDotMediansDeg">The median of the dots' own medians.</param>
/// <param name="WorstDotDeg">The largest of the dots' medians.</param>
/// <param name="Dots">Dots with at least <see cref="GazeAccuracy.MinFramesPerDot"/> fixation rows.</param>
public sealed record GazeAccuracyResult(
    int Frames, int ValidFrames, int FixationFrames, double MedianDeg, double MedianOfDotMediansDeg, double WorstDotDeg, int Dots);

/// <summary>
/// The gaze check's maths, a port of <c>DreamAirWin\tools\gaze_world\gaze_world_accuracy.py</c> kept to its method:
/// each valid row's gaze ray (forward <c>(0, 0, -1)</c> turned by the gaze orientation, from the gaze origin) goes to
/// the dot it points nearest to; rows within 5° of their dot are fixations; a dot needs 20 of them for its own median.
/// Checked against the script on two worn runs of 2026-11-02 (the same medians and worst dot, to the hundredth).
/// </summary>
public static class GazeAccuracy
{
    public const double FixationDeg = 5.0;
    public const int MinFramesPerDot = 20;

    private static readonly string[] GazeColumns = ["placed", "g_flags", "g_px", "g_py", "g_pz", "g_qx", "g_qy", "g_qz", "g_qw"];

    /// <summary>
    /// Computes the result from <c>gaze_world</c>'s gaze rows and its dot positions (CSV with a header; the dots' file
    /// has <c>x</c>, <c>y</c>, <c>z</c> columns in the same space as the gaze origin). Null when no dot has enough
    /// fixation rows, or a file lacks the columns.
    /// </summary>
    public static GazeAccuracyResult? Compute(TextReader appCsv, TextReader targetsCsv)
    {
        var dots = ReadDots(targetsCsv);
        if (dots is null || dots.Count == 0)
        {
            return null;
        }

        var header = Split(appCsv.ReadLine());
        var width = header.Length;
        var at = GazeColumns.Select(name => Array.IndexOf(header, name)).ToArray();
        if (at.Any(i => i < 0))
        {
            return null;
        }

        var frames = 0;
        var valid = 0;
        var perDot = dots.Select(_ => new List<double>()).ToArray();
        while (appCsv.ReadLine() is { } line)
        {
            // The last row is incomplete while gaze_world still writes, and may end in a cut-off number.
            var cells = Split(line);
            if (cells.Length < width || cells[at[0]] != "1")
            {
                continue;
            }

            frames++;
            if (at.Any(i => cells[i].Length == 0))
            {
                continue;
            }

            if (!int.TryParse(cells[at[1]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags)
                || !TryNumbers(cells, at[2..], out var n))
            {
                continue;
            }

            if ((flags & 3) != 3)
            {
                continue;
            }

            valid++;
            var origin = new Vec(n[0], n[1], n[2]);
            var gaze = Rotate(new Vec(0, 0, -1), n[3], n[4], n[5], n[6]);
            var best = -1;
            var bestDeg = double.MaxValue;
            for (var d = 0; d < dots.Count; d++)
            {
                var deg = AngleDeg(gaze, dots[d] - origin);
                if (deg < bestDeg)
                {
                    bestDeg = deg;
                    best = d;
                }
            }

            if (best >= 0 && bestDeg <= FixationDeg)
            {
                perDot[best].Add(bestDeg);
            }
        }

        var counted = perDot.Where(list => list.Count >= MinFramesPerDot).ToList();
        if (counted.Count == 0)
        {
            return null;
        }

        var all = perDot.SelectMany(list => list).ToList();
        var dotMedians = counted.Select(Median).ToList();
        return new GazeAccuracyResult(frames, valid, all.Count, Median(all), Median(dotMedians), dotMedians.Max(), counted.Count);
    }

    /// <summary>The middle value; for an even count, the mean of the middle two (as the script does).</summary>
    public static double Median(IReadOnlyCollection<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary><paramref name="v"/> turned by the unit quaternion (x, y, z, w): v + 2w(q×v) + 2q×(q×v).</summary>
    internal static Vec Rotate(Vec v, double x, double y, double z, double w)
    {
        var q = new Vec(x, y, z);
        var t = Cross(q, v) * 2;
        return v + (t * w) + Cross(q, t);
    }

    internal static double AngleDeg(Vec a, Vec b)
    {
        var cos = Dot(a, b) / (Length(a) * Length(b));
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }

    private static List<Vec>? ReadDots(TextReader csv)
    {
        var header = Split(csv.ReadLine());
        int[] at = [Array.IndexOf(header, "x"), Array.IndexOf(header, "y"), Array.IndexOf(header, "z")];
        if (at.Any(i => i < 0))
        {
            return null;
        }

        var dots = new List<Vec>();
        while (csv.ReadLine() is { } line)
        {
            var cells = Split(line);
            if (TryNumbers(cells, at, out var n))
            {
                dots.Add(new Vec(n[0], n[1], n[2]));
            }
        }

        return dots;
    }

    private static bool TryNumbers(string[] cells, int[] at, out double[] numbers)
    {
        numbers = new double[at.Length];
        for (var i = 0; i < at.Length; i++)
        {
            if (at[i] >= cells.Length
                || !double.TryParse(cells[at[i]], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])
                || !double.IsFinite(numbers[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Split(string? line) => line is null ? [] : line.Split(',').Select(c => c.Trim()).ToArray();

    private static Vec Cross(Vec a, Vec b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static double Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static double Length(Vec a) => Math.Sqrt(Dot(a, a));

    internal readonly record struct Vec(double X, double Y, double Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    }
}
