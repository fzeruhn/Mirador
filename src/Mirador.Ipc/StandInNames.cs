namespace Mirador.Ipc;

/// <summary>
/// The names of Mirador's stand-in. Mirador builds Monado to start it under a name of its own (<see cref="FileName"/>),
/// so a runtime that looks next to itself first doesn't find the real service there instead; it is also placed as
/// <see cref="ServiceFileName"/>, the name builds configured as upstream does look for.
/// </summary>
public static class StandInNames
{
    /// <summary>What Mirador configures Monado's runtime to start (<c>XRT_SERVICE_EXECUTABLE</c>, without <c>.exe</c>).</summary>
    public const string ExecutableName = "mirador-standin";

    /// <summary>The stand-in's own name, also its file in Mirador's folder (<c>{app}\standin</c>).</summary>
    public const string FileName = ExecutableName + ".exe";

    /// <summary>The service's own name, which builds configured as upstream does look for.</summary>
    public const string ServiceFileName = "monado-service.exe";

    /// <summary>Every name the stand-in is placed under, in the folder where the runtime looks.</summary>
    public static IReadOnlyList<string> FileNames { get; } = [FileName, ServiceFileName];
}
