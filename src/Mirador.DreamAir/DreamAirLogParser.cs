using System.Globalization;
using System.Text.RegularExpressions;
using Mirador.Core.Modules;

namespace Mirador.DreamAir;

/// <summary>
/// Turns Monado's output into the Dream Air's status lines on Home: the display (from the layer's lines), the headset
/// board and the eye tracker (from Monado's Dream Air and Tobii drivers). Lines are matched by their message, whatever
/// prefix the log puts in front. Fed one line at a time; <see cref="Lines"/> may be read from any thread.
/// </summary>
public sealed partial class DreamAirLogParser
{
    public const string DisplayKey = "display";
    public const string BoardKey = "board";
    public const string EyeKey = "eye";

    private const string LostHeadset = "Lost the headset, the panels stay dark until Monado restarts.";

    private const string Replaced = "Pimax Play's calibration replaced the one Mirador kept (a backup is in Eye tracking).";

    private readonly object gate = new();
    private readonly bool geometryMissing;

    private string? mode;
    private long presents;
    private long missed;
    private bool sawStats;

    private StatusLine board = new(BoardKey, "Headset board", StatusLevel.Info, "Not opened yet");
    private StatusLine eye;
    private string? eyeState;
    private bool correction;
    private bool replaced;

    /// <param name="upload">How the geometry upload before this run went; null when there was none.</param>
    public DreamAirLogParser(GeometryResult? upload = null)
    {
        geometryMissing = upload is { Ok: false };
        eye = upload switch
        {
            null => new StatusLine(EyeKey, "Eye tracker", StatusLevel.Info, "Not connected yet"),
            { Ok: true } => new StatusLine(EyeKey, "Eye tracker", StatusLevel.Ok, GeometryUploader.Uploaded),
            _ => new StatusLine(EyeKey, "Eye tracker", StatusLevel.Warning, upload.Message),
        };
    }

    /// <summary>The display, board and eye tracker lines, in that order.</summary>
    public IReadOnlyList<StatusLine> Lines
    {
        get
        {
            lock (gate)
            {
                return [Display(), board, eye];
            }
        }
    }

    /// <summary>Reads one line of Monado's output; whether <see cref="Lines"/> changed.</summary>
    public bool Feed(string line)
    {
        lock (gate)
        {
            var before = (Display(), board, eye);
            Read(line);
            return before != (Display(), board, eye);
        }
    }

    private void Read(string line)
    {
        if (DisplayOpen().Match(line) is { Success: true } open)
        {
            var hz = double.Parse(open.Groups["hz"].Value, CultureInfo.InvariantCulture);
            mode = $"Direct mode, {open.Groups["w"].Value}×{open.Groups["h"].Value} at {hz.ToString("0.##", CultureInfo.InvariantCulture)} Hz";
            return;
        }

        if (Stats().Match(line) is { Success: true } stats)
        {
            presents += long.Parse(stats.Groups["presents"].Value, CultureInfo.InvariantCulture);
            missed += long.Parse(stats.Groups["missed"].Value, CultureInfo.InvariantCulture);
            sawStats = true;
            return;
        }

        if (line.Contains("Pimax Dream Air opened.", StringComparison.Ordinal))
        {
            board = board with { Level = StatusLevel.Ok, Text = "Connected" };
        }
        else if (line.Contains(LostHeadset, StringComparison.Ordinal))
        {
            board = board with { Level = StatusLevel.Error, Text = LostHeadset };
        }
        else if (line.Contains("Eye tracker connected.", StringComparison.Ordinal))
        {
            SetEye("Connected", StatusLevel.Info);
        }
        else if (line.Contains("Eye calibration applied (from the tracker).", StringComparison.Ordinal))
        {
            SetEye("Calibration applied (from the tracker)", StatusLevel.Ok);
        }
        else if (line.Contains("Eye calibration applied (from file).", StringComparison.Ordinal))
        {
            SetEye("Calibration applied (from file)", StatusLevel.Ok);
        }
        else if (line.Contains("Gaze correction loaded from ", StringComparison.Ordinal))
        {
            correction = true;
            if (eyeState is not null)
            {
                SetEye(eyeState, eye.Level);
            }
        }
        else if (line.Contains("Replaced the saved calibration in ", StringComparison.Ordinal))
        {
            replaced = true;
            eye = eye with { Level = StatusLevel.Warning, Text = Replaced };
        }
        else if (line.Contains("Lost the eye tracker (", StringComparison.Ordinal) && line.Contains("reconnecting", StringComparison.Ordinal))
        {
            eyeState = null;
            eye = eye with { Level = StatusLevel.Warning, Text = "Lost the eye tracker; reconnecting" };
        }
    }

    /// <summary>
    /// The eye line for <paramref name="state"/>, with the gaze correction when one was loaded; a geometry that wasn't
    /// uploaded stays on the line, since gaze is off without it.
    /// </summary>
    private void SetEye(string state, StatusLevel level)
    {
        eyeState = state;
        if (replaced)
        {
            // Whatever the driver says after it (or before it, in another order), the owner must see this one.
            eye = eye with { Level = StatusLevel.Warning, Text = Replaced };
            return;
        }

        var text = correction ? state + ", gaze correction on" : state;
        eye = geometryMissing
            ? eye with { Level = StatusLevel.Warning, Text = text + "; geometry not uploaded, gaze about 10° off" }
            : eye with { Level = level, Text = text };
    }

    private StatusLine Display()
    {
        if (mode is null)
        {
            return new StatusLine(DisplayKey, "Display", StatusLevel.Info, "Waiting for the display");
        }

        var text = sawStats
            ? $"{mode}, {missed.ToString("N0", CultureInfo.InvariantCulture)} missed of {presents.ToString("N0", CultureInfo.InvariantCulture)} frames"
            : mode;
        return new StatusLine(DisplayKey, "Display", StatusLevel.Ok, text);
    }

    [GeneratedRegex(@"display session open: .*?, (?<w>\d+)x(?<h>\d+) at (?<hz>\d+(?:\.\d+)?) Hz", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayOpen();

    [GeneratedRegex(@"(?<presents>\d+) presents in [\d.]+ s .*?(?<missed>\d+) missed \(", RegexOptions.CultureInvariant)]
    private static partial Regex Stats();
}
