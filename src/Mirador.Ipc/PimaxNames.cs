namespace Mirador.Ipc;

/// <summary>The fixed names the helper acts on. It never takes a name from a request.</summary>
public static class PimaxNames
{
    /// <summary>Pimax's Windows service.</summary>
    public const string Service = "PiServiceLauncher";

    /// <summary>The Pimax processes that must be gone before Monado can take the headset, by name without <c>.exe</c>.</summary>
    public static readonly string[] Processes =
    [
        "PimaxClient",
        "pi_server",
        "pi_overlay",
        "PiPlayService",
        "PiPlatformService_64",
        "vrss_gaze_provider",
    ];

    /// <summary>Pimax's eye tracking runtime, whose command line names the eye geometry file it was started with.</summary>
    public const string EyeRuntimeProcess = "platform_runtime_XR5EYECHIP_WIN10_x64";
}
