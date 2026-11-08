namespace Mirador.Ipc;

/// <summary>
/// The Pimax Dream Air module's fixed Monado environment (Mirador.md section 4.3), kept here so the unelevated path
/// (<c>Mirador.DreamAir</c>) and the elevated one (the helper, which can't reference the module) write the same table.
/// The layer's own folder variable (<c>VK_ADD_IMPLICIT_LAYER_PATH</c>) is not here: a profile that needs it runs
/// unelevated, so the elevated environment never carries a path.
/// </summary>
public static class DreamAirEnvironment
{
    /// <summary>The module id a request names to get this table (Mirador.md section 4.10 item 44).</summary>
    public const string ModuleId = "dreamair";

    /// <summary>
    /// The variables the Dream Air needs: the launcher's, and the Dream Air drivers' log at info, whose lines the board
    /// and eye tracker status come from (their default, warn, leaves those out).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FixedEnvironment { get; } =
    [
        new("STEAMVR_LH_ENABLE", "1"),
        new("DREAMAIR_VK_LAYER", "1"),
        new("DISABLE_RTSS_LAYER", "1"),
        new("VK_LOADER_LAYERS_ALLOW", "VK_LAYER_DREAMAIR_nvapi_direct"),
        new("XRT_COMPOSITOR_FORCE_VK_DISPLAY", "0"),
        new("XRT_COMPOSITOR_USE_PRESENT_WAIT", "1"),
        new("U_PACING_COMP_PRESENT_TO_DISPLAY_OFFSET_MS", "8.9"),
        new("DREAMAIR_LOG", "info"),
    ];
}
