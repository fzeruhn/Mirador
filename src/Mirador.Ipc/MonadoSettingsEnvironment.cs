using System.Globalization;

namespace Mirador.Ipc;

/// <summary>One of Monado's numeric settings' allowed range: its bounds and the step it moves in.</summary>
/// <param name="Min">The smallest value.</param>
/// <param name="Max">The largest value.</param>
/// <param name="Step">The step between values; a value must sit on one.</param>
public readonly record struct SettingRange(int Min, int Max, int Step)
{
    /// <summary>Whether <paramref name="value"/> is in range and on a step boundary measured from <see cref="Min"/>.</summary>
    public bool Contains(int value) => value >= Min && value <= Max && (value - Min) % Step == 0;
}

/// <summary>
/// The one mapping from Mirador's shared Monado settings (Mirador.md section 4.3 item 10) to the environment variables
/// <c>monado-service</c> reads, with their ranges. Both the unelevated path (<c>Mirador.Core</c>'s
/// <c>MonadoEnvironment</c>) and the elevated one (the helper) write them through here, so the names, the formatting
/// and the ranges can't drift between them.
/// </summary>
public static class MonadoSettingsEnvironment
{
    public const string LighthouseWait = "LH_DISCOVER_WAIT_MS";
    public const string RenderScale = "XRT_COMPOSITOR_SCALE_PERCENTAGE";
    public const string CompositorMinTime = "U_PACING_COMP_MIN_TIME_MS";
    public const string ExitOnDisconnect = "IPC_EXIT_ON_DISCONNECT";

    /// <summary>Lighthouse discovery wait, 1000–30000 ms in steps of 1000 (default 3000).</summary>
    public static readonly SettingRange LighthouseWaitRange = new(1000, 30000, 1000);

    /// <summary>Compositor render scale, 50–200 % in steps of 5 (default 100).</summary>
    public static readonly SettingRange RenderScaleRange = new(50, 200, 5);

    /// <summary>Compositor minimum frame time, 1–20 ms in steps of 1 (default 6).</summary>
    public static readonly SettingRange CompositorMinTimeRange = new(1, 20, 1);

    /// <summary>
    /// Writes the settings' variables through <paramref name="set"/> (name, value — the value is never empty here), so
    /// each side keeps its own dictionary rules while the names, formatting and which variables are written stay here.
    /// <see cref="ExitOnDisconnect"/> is written only when <paramref name="exitOnDisconnect"/> is on, the way the setting
    /// works (default off).
    /// </summary>
    public static void Write(Action<string, string> set, int lighthouseWaitMs, int renderScalePercent, int compositorMinTimeMs, bool exitOnDisconnect)
    {
        set(LighthouseWait, Number(lighthouseWaitMs));
        set(RenderScale, Number(renderScalePercent));
        set(CompositorMinTime, Number(compositorMinTimeMs));
        if (exitOnDisconnect)
        {
            set(ExitOnDisconnect, "1");
        }
    }

    /// <summary>The first setting that is out of its range, or null when all three are in range and on a step.</summary>
    public static string? OutOfRange(int lighthouseWaitMs, int renderScalePercent, int compositorMinTimeMs)
    {
        if (!LighthouseWaitRange.Contains(lighthouseWaitMs))
        {
            return LighthouseWait;
        }

        if (!RenderScaleRange.Contains(renderScalePercent))
        {
            return RenderScale;
        }

        if (!CompositorMinTimeRange.Contains(compositorMinTimeMs))
        {
            return CompositorMinTime;
        }

        return null;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
