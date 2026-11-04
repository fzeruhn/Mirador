using System.ComponentModel;
using System.Diagnostics;

namespace Mirador.App.Services;

/// <summary>Runs <c>Mirador.exe --set-runtime "&lt;manifest&gt;"</c> through the UAC prompt: the one step Mirador runs elevated (Mirador.md §7.4).</summary>
public static class ElevatedRuntime
{
    /// <summary>The exit code Windows' "the user cancelled" stands for.</summary>
    public const int PromptCancelled = 1223;

    /// <summary>The elevated step's exit code (<c>SetRuntimeCommand</c>'s), or <see cref="PromptCancelled"/>.</summary>
    public static async Task<int> RunAsync(string manifest)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"))
        {
            Arguments = $"--set-runtime \"{manifest}\"",
            Verb = "runas",
            UseShellExecute = true,
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == PromptCancelled)
        {
            return PromptCancelled;
        }
    }
}
