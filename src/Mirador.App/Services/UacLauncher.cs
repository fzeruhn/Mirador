using System.ComponentModel;
using System.Diagnostics;
using Mirador.Core;
using Mirador.Core.Build;

namespace Mirador.App.Services;

/// <summary>
/// Runs a program through Windows' administrator prompt and waits for it: Visual Studio's installer for the build tools
/// (Mirador.md §4.11 item 49). Mirador itself stays unelevated.
/// </summary>
public sealed class UacLauncher : IElevatedLauncher
{
    public async Task<int> RunAsync(CommandSpec spec, CancellationToken ct)
    {
        var info = new ProcessStartInfo(spec.FileName) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (spec.WorkingDirectory is not null)
        {
            info.WorkingDirectory = spec.WorkingDirectory;
        }

        try
        {
            using var process = Process.Start(info) ?? throw new MiradorException($"{Path.GetFileName(spec.FileName)} couldn't be started.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ElevatedRuntime.PromptCancelled)
        {
            throw new ElevationDeclinedException();
        }
        catch (Win32Exception e)
        {
            throw new MiradorException($"{Path.GetFileName(spec.FileName)} couldn't be started: {e.Message.TrimEnd('.')}.");
        }
    }
}
