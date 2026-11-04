using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Runtime;

/// <summary>Makes a Monado install the active OpenXR runtime: the bundled one through the helper service, a build folder's through an elevated step.</summary>
public static class RuntimeSwitch
{
    /// <summary>The exit code of a process whose elevation prompt (UAC) was cancelled: ERROR_CANCELLED.</summary>
    private const int PromptCancelled = 1223;

    /// <summary>
    /// Sets <paramref name="install"/> as the active runtime. The bundled install goes through the helper service. A build
    /// folder's is run through <paramref name="runElevated"/>, given its manifest path, which starts
    /// <c>Mirador.exe --set-runtime</c> elevated and returns its exit code.
    /// </summary>
    /// <returns>Null when the runtime was set, else what went wrong in plain words.</returns>
    public static async Task<string?> SetAsync(MonadoInstall install, IHelperClient helper, Func<string, Task<int>> runElevated)
    {
        if (install.IsBundled)
        {
            var reply = await helper.SendAsync(HelperCommand.SetRuntimeBundled, CancellationToken.None).ConfigureAwait(false);
            return reply.Ok ? null : reply.Error ?? "Mirador's helper service couldn't set the OpenXR runtime, so the runtime is unchanged.";
        }

        var exitCode = await runElevated(install.Manifest).ConfigureAwait(false);
        return exitCode switch
        {
            SetRuntimeCommand.Done => null,
            PromptCancelled => "You cancelled the prompt; the runtime is unchanged.",
            SetRuntimeCommand.ManifestRefused => "That Monado build's runtime manifest isn't usable, so the runtime is unchanged.",
            _ => $"Setting the OpenXR runtime failed (code {exitCode}); the runtime is unchanged.",
        };
    }
}
