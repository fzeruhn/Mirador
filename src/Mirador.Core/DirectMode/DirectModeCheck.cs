using Mirador.Core.Monado;

namespace Mirador.Core.DirectMode;

/// <summary>
/// The re-check after an NVIDIA driver update (Mirador.md §4.11 item 52): when the driver isn't the last one direct mode
/// passed with, the layer's display check runs before Monado starts, and a fail refuses the Start with what the check
/// says to do. The headset's module calls it once it has freed the headset (its own software stopped), and keeps which
/// driver passed.
/// </summary>
/// <param name="driverNow">The NVIDIA driver now (<see cref="NvidiaDriver"/>), or null when there is none.</param>
/// <param name="startConsole">Starts the display check (<see cref="ConsoleProcess.Start"/>).</param>
public sealed class DirectModeCheck(Func<string?> driverNow, Func<ProcessSpec, IConsoleProcess> startConsole)
{
    /// <summary>
    /// Runs the display check when the driver changed since <paramref name="lastGoodDriver"/>; on a pass hands the
    /// driver to <paramref name="remember"/>. The check's output, pass or fail, goes to <paramref name="saveOutput"/>.
    /// </summary>
    /// <exception cref="MiradorException">Direct mode doesn't work with this driver, or couldn't be checked.</exception>
    public async Task EnsureAsync(
        LayerFiles layer,
        string? lastGoodDriver,
        Action<string?> remember,
        Action<DisplayCheckResult>? saveOutput,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var driver = driverNow();
        if (driver == lastGoodDriver)
        {
            return;
        }

        progress.Report("Checking direct mode with the new NVIDIA driver…");
        var check = await DisplayCheck.RunAsync(layer.CheckExe, startConsole, ct).ConfigureAwait(false);
        saveOutput?.Invoke(check);
        if (!check.Pass)
        {
            throw new MiradorException(check.Finished
                ? $"Direct mode doesn't work with NVIDIA driver {driver}: {Sentence(check.Message)}{(check.WhatToDo is null ? "" : " " + Sentence(Capitalized(check.WhatToDo)))}"
                : $"Mirador couldn't check direct mode with the new NVIDIA driver {driver}: {Sentence(check.Message)} Try again.");
        }

        remember(driver);
    }

    internal static string Sentence(string s) => s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? s : s + ".";

    internal static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
