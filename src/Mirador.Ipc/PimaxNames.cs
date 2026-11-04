namespace Mirador.Ipc;

/// <summary>The fixed names the helper acts on. It never takes a name from a request.</summary>
public static class PimaxNames
{
    /// <summary>Pimax's Windows service.</summary>
    public const string Service = "PiServiceLauncher";

    /// <summary>
    /// The Pimax processes that must be gone before Monado can take the headset, by name without <c>.exe</c>. Pimax's
    /// eye runtime is among them: its service starts it while Pimax Play uses the headset, it outlives the service's
    /// stop and holds the eye tracker (seen 2026-11-04, Pimax Play 2.0.4).
    /// </summary>
    public static readonly string[] Processes =
    [
        "PimaxClient",
        "pi_server",
        "pi_overlay",
        "PiPlayService",
        "PiPlatformService_64",
        "vrss_gaze_provider",
        EyeRuntimeProcess,
    ];

    /// <summary>Pimax's eye tracking runtime, whose command line names the eye geometry file it was started with.</summary>
    public const string EyeRuntimeProcess = "platform_runtime_XR5EYECHIP_WIN10_x64";

    /// <summary>
    /// The eye geometry variants Pimax Play ships (<c>config_XR5PIMAXDA_&lt;variant&gt;.bin</c>): the only names that
    /// ever go into a path or come back from the helper.
    /// </summary>
    public static IReadOnlyList<string> EyeBlobVariants { get; } = ["K10_RAWH", "K10_RAYH", "K11_RAWH", "K11_RAYH"];
}
