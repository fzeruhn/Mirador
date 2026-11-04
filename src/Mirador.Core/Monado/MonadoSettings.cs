namespace Mirador.Core.Monado;

/// <summary>
/// The Monado settings every profile shares. Out-of-range values are allowed to exist (a text box can hold anything);
/// <see cref="Clamped"/> brings them into range.
/// </summary>
/// <param name="LighthouseWaitMs">How long Monado waits for the tracking base stations, 1000 to 30000 in steps of 1000.</param>
/// <param name="RenderScalePercent">Compositor render scale, 50 to 200 in steps of 5.</param>
/// <param name="CompositorMinTimeMs">Compositor minimum frame time, 1 to 20 in steps of 1.</param>
/// <param name="StopWhenLastAppCloses">Whether Monado exits when the last app disconnects.</param>
public sealed record MonadoSettings(
    int LighthouseWaitMs = 3000,
    int RenderScalePercent = 100,
    int CompositorMinTimeMs = 6,
    bool StopWhenLastAppCloses = false)
{
    /// <summary>Each value rounded to the nearest step (halfway rounds up), then held inside its range.</summary>
    public MonadoSettings Clamped() => this with
    {
        LighthouseWaitMs = Snap(LighthouseWaitMs, min: 1000, max: 30000, step: 1000),
        RenderScalePercent = Snap(RenderScalePercent, min: 50, max: 200, step: 5),
        CompositorMinTimeMs = Snap(CompositorMinTimeMs, min: 1, max: 20, step: 1),
    };

    private static int Snap(int value, int min, int max, int step)
    {
        var rounded = (long)Math.Round((double)value / step, MidpointRounding.AwayFromZero) * step;
        return (int)Math.Clamp(rounded, min, max);
    }
}
