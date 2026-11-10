namespace Mirador.Ipc;

/// <summary>
/// The Monado variables that turn the direct-mode layer on for a headset that uses NVIDIA direct mode (Mirador.md §4.11
/// item 52), whichever module it has: the layer itself, RivaTuner's overlay layer kept out of the way, Monado's
/// compositor on the layer's display with its present wait. Kept here so the unelevated path (the session) and the
/// elevated one (the helper) write the same table. The layer's own folder variable (<c>VK_ADD_IMPLICIT_LAYER_PATH</c>)
/// is not here: a profile that needs it runs unelevated, so the elevated environment never carries a path.
/// </summary>
public static class DirectModeEnvironment
{
    public static IReadOnlyList<KeyValuePair<string, string>> Variables { get; } =
    [
        new("DREAMAIR_VK_LAYER", "1"),
        new("DISABLE_RTSS_LAYER", "1"),
        new("VK_LOADER_LAYERS_ALLOW", "VK_LAYER_DREAMAIR_nvapi_direct"),
        new("XRT_COMPOSITOR_FORCE_VK_DISPLAY", "0"),
        new("XRT_COMPOSITOR_USE_PRESENT_WAIT", "1"),
    ];

    /// <summary>
    /// Variables a shell may have left set that would change the layer or Monado's security, taken out of the
    /// environment Monado inherits from the user (the elevated environment inherits nothing): the layer switched off,
    /// any user's processes let in, another direct-mode route, and the layer's test switches.
    /// </summary>
    public static IReadOnlyList<string> Cleared { get; } =
    [
        "DISABLE_DREAMAIR_VK_LAYER",
        "IPC_RELAXED_CONNECTION_SECURITY",
        "XRT_COMPOSITOR_FORCE_NVAPI_DIRECT",
        "DREAMAIR_VK_LAYER_TEST_NO_EDID",
        "DREAMAIR_VK_LAYER_IGNORE_HOLDERS",
    ];
}

/// <summary>
/// The Pimax Dream Air module's fixed Monado environment (Mirador.md section 4.3), kept here so the unelevated path
/// (<c>Mirador.DreamAir</c>) and the elevated one (the helper, which can't reference the module) write the same table.
/// </summary>
public static class DreamAirEnvironment
{
    /// <summary>The module id a request names to get this table (Mirador.md section 4.10 item 44).</summary>
    public const string ModuleId = "dreamair";

    /// <summary>
    /// The Dream Air's own variables: SteamVR's Lighthouse driver, the compositor's pacing for its panels, and the Dream
    /// Air drivers' log at info, whose lines the board and eye tracker status come from (their default, warn, leaves those
    /// out). The session adds <see cref="DirectModeEnvironment"/> for it (the module uses direct mode).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ModuleVariables { get; } =
    [
        new("STEAMVR_LH_ENABLE", "1"),
        new("U_PACING_COMP_PRESENT_TO_DISPLAY_OFFSET_MS", "8.9"),
        new("DREAMAIR_LOG", "info"),
    ];

    /// <summary>Everything a Dream Air Monado gets, as the helper writes it: the module's variables and the layer's.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FixedEnvironment { get; } =
        [.. ModuleVariables, .. DirectModeEnvironment.Variables];
}
