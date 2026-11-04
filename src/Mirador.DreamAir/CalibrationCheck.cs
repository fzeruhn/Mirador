using System.Globalization;
using Mirador.Core;
using Mirador.Core.Monado;
using Mirador.Core.Session;

namespace Mirador.DreamAir;

/// <summary>
/// "Check my calibration" (Mirador.md §4.5 item 16): <c>gaze_world</c> shows nine dots in the headset for a minute
/// while the wearer looks at each in turn, then <see cref="GazeAccuracy"/> says how far gaze was from them.
/// </summary>
/// <param name="start">Starts <c>gaze_world</c> (<see cref="ConsoleProcess.Start"/>).</param>
/// <param name="state">Where the last result is kept.</param>
/// <param name="time">The clock for the time limit.</param>
public sealed class CalibrationCheck(Func<ProcessSpec, IConsoleProcess> start, DreamAirStateStore state, TimeProvider time)
{
    /// <summary>How long <c>gaze_world</c> may take (it shows the dots for 60 s).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long the dots may take to appear: <c>gaze_world</c> places them about a second after the headset is tracked
    /// (it says "placed …"), and its 60 s start only then.
    /// </summary>
    public static readonly TimeSpan PlaceLimit = TimeSpan.FromSeconds(30);

    internal const string NotTracked =
        "The dots never appeared because the headset isn't tracked. Check that the base stations are on and can see the headset, then try again.";

    /// <summary>The worst dot, in degrees, that still counts as a good calibration.</summary>
    public const double GoodWorstDotDeg = 2.5;

    /// <summary>
    /// The start of <c>gaze_world</c>'s file names (its last argument): it writes <c>gaze_app.csv</c> (a row per frame),
    /// <c>gaze_targets.csv</c> (the dots) and <c>gaze_head.csv</c>. Relative to the check's folder, which is its working
    /// folder, so that the user's profile path never goes through its narrow-character <c>fopen</c>.
    /// </summary>
    public const string Prefix = "gaze";

    /// <summary>
    /// Runs the check: starts Monado when it doesn't run (and leaves it running: a game or the user may have joined that
    /// Start meanwhile), runs <c>gaze_world</c> against this profile's Monado only (<c>XR_RUNTIME_JSON</c>, so it never
    /// reaches Pimax's runtime) in <c>&lt;ChecksDir&gt;\&lt;yyyyMMdd-HHmmss&gt;</c>, measures its files there, and keeps the result.
    /// </summary>
    /// <exception cref="MiradorException">The check couldn't run or gave nothing to measure; the message says why.</exception>
    public async Task<GazeAccuracyResult> RunAsync(
        SessionController session, string gazeWorldExe, AppPaths paths, IProgress<string> progress, CancellationToken ct)
    {
        if (!File.Exists(gazeWorldExe))
        {
            throw new MiradorException($"The gaze check program is missing ({gazeWorldExe}). Reinstall Mirador.");
        }

        if (session.State != SessionState.Running)
        {
            progress.Report("Starting Monado…");
            var outcome = await session.StartAsync(StartReason.Check, ct).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                throw new MiradorException(outcome.Message);
            }
        }

        {
            var manifest = session.CurrentInstall?.Manifest
                ?? throw new MiradorException("Monado stopped before the check could start. Start it again.");
            var outDir = Path.Combine(paths.ChecksDir, time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(outDir);

            progress.Report("Put the headset on and look at each dot in turn…");
            var lines = await RunGazeWorldAsync(gazeWorldExe, manifest, outDir, ct).ConfigureAwait(false);

            if (!HasResults(outDir))
            {
                throw new MiradorException($"The gaze check left no results in {outDir}.{Tail(lines)}");
            }

            var result = Measure(outDir)
                ?? throw new MiradorException(
                    $"The check had too little to measure: no dot was looked at long enough. Try again, looking at each dot until it moves on.{Tail(lines)}");
            try
            {
                state.Update(s => s with { LastCheck = new LastCheck(time.GetUtcNow(), result.MedianDeg, result.WorstDotDeg, result.Dots) });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The result is shown anyway; only the page's "last check" line misses it.
            }

            return result;
        }
    }

    /// <summary>"Median 0.89°, worst dot 1.15° (9 dots)", and whether that is good enough.</summary>
    public static (string Text, bool Good) Describe(double medianDeg, double worstDotDeg, int dots)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"Median {medianDeg:0.00}°, worst dot {worstDotDeg:0.00}° ({dots} dots)");
        var good = worstDotDeg <= GoodWorstDotDeg;
        return good ? (text, true) : ($"{text}: calibrate again in Pimax Play.", false);
    }

    /// <summary>Measures the check's files in <paramref name="outDir"/>; null when one is missing or they give nothing to measure.</summary>
    public static GazeAccuracyResult? Measure(string outDir)
    {
        if (!HasResults(outDir))
        {
            return null;
        }

        using var gaze = File.OpenText(GazeFile(outDir));
        using var dots = File.OpenText(DotsFile(outDir));
        return GazeAccuracy.Compute(gaze, dots);
    }

    /// <summary>Whether <c>gaze_world</c>'s gaze and dots files are in <paramref name="outDir"/>.</summary>
    public static bool HasResults(string outDir) => File.Exists(GazeFile(outDir)) && File.Exists(DotsFile(outDir));

    private static string GazeFile(string outDir) => Path.Combine(outDir, Prefix + "_app.csv");

    private static string DotsFile(string outDir) => Path.Combine(outDir, Prefix + "_targets.csv");

    internal async Task<List<string>> RunGazeWorldAsync(string exe, string manifest, string outDir, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(ConsoleProcess.CurrentEnvironment(), StringComparer.OrdinalIgnoreCase)
        {
            ["XR_RUNTIME_JSON"] = manifest,
        };
        var lines = new List<string>();
        var placed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = start(new ProcessSpec(
            exe, $"--cursor --grid --bright 64 --seconds 60 {Prefix}", outDir, environment));
        process.Line += line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }

            if (line.StartsWith("placed ", StringComparison.Ordinal))
            {
                placed.TrySetResult();
            }
        };
        process.CloseInput();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limit = Task.Delay(Limit, time, stop.Token);
        var placeLimit = Task.Delay(PlaceLimit, time, stop.Token);
        var first = await Task.WhenAny(process.Exited, placed.Task, placeLimit).ConfigureAwait(false);
        if (first == placeLimit)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            process.Kill();
            ct.ThrowIfCancellationRequested();
            throw new MiradorException(NotTracked);
        }

        first = await Task.WhenAny(process.Exited, limit).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        if (first != process.Exited)
        {
            process.Kill();
            ct.ThrowIfCancellationRequested();
            throw new MiradorException($"The gaze check didn't finish within {Limit.TotalSeconds:0} s.{Tail(lines)}");
        }

        var code = await process.Exited.ConfigureAwait(false);
        if (code != 0)
        {
            throw new MiradorException($"The gaze check stopped with exit code {code}.{Tail(lines)}");
        }

        lock (lines)
        {
            return [.. lines];
        }
    }

    /// <summary>The program's last lines, for a failure message; empty when it said nothing.</summary>
    private static string Tail(List<string> lines)
    {
        lock (lines)
        {
            return lines.Count == 0 ? "" : " Its last words: " + string.Join(" / ", lines.TakeLast(3));
        }
    }
}
