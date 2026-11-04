using Mirador.Core.Monado;

namespace Mirador.Core.Runtime;

/// <summary>
/// <c>Mirador.exe --set-runtime "&lt;manifest&gt;"</c>: the elevated step that makes a build folder's Monado the active
/// OpenXR runtime. The active runtime decides which DLL every OpenXR app loads, so a manifest that isn't a usable
/// runtime is refused before anything is written.
/// </summary>
public static class SetRuntimeCommand
{
    public const string Flag = "--set-runtime";

    internal const int Done = 0;
    internal const int ManifestRefused = 1;
    internal const int WriteFailed = 2;

    /// <summary>
    /// Checks the manifest and, if it is a usable runtime manifest, hands its full path to <paramref name="writeActiveRuntime"/>.
    /// Returns 0 after the write, 1 when the manifest is refused (nothing is written), 2 when the write fails.
    /// </summary>
    public static int Run(string manifest, Action<string> writeActiveRuntime) => Run(manifest, writeActiveRuntime, Console.Error);

    internal static int Run(string manifest, Action<string> writeActiveRuntime, TextWriter error)
    {
        var problem = RuntimeManifest.Validate(manifest);
        if (problem is not null)
        {
            error.WriteLine(problem);
            return ManifestRefused;
        }

        try
        {
            writeActiveRuntime(Path.GetFullPath(manifest));
            return Done;
        }
        catch (Exception e)
        {
            error.WriteLine($"Mirador couldn't set the OpenXR runtime ({e.Message.TrimEnd('.')}); the runtime is unchanged.");
            return WriteFailed;
        }
    }
}
