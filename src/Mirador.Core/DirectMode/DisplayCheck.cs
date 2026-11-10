using System.Text;
using Mirador.Core.Monado;

namespace Mirador.Core.DirectMode;

/// <summary>What the layer's display check found.</summary>
/// <param name="Pass">Whether direct mode works (the check ended with exit code 0).</param>
/// <param name="Message">What it found, from its last <c>PASS: …</c> or <c>FAIL: …</c> line.</param>
/// <param name="WhatToDo">Its advice, from its <c>what to do: …</c> line; null when it gave none.</param>
/// <param name="Output">Everything it wrote, for the diagnostics.</param>
public sealed record DisplayCheckResult(bool Pass, string Message, string? WhatToDo, string Output)
{
    /// <summary>Whether the check ran to its end; false when it couldn't start or ran out of time (no verdict).</summary>
    public bool Finished { get; init; } = true;
}

/// <summary>
/// Runs the layer's display check (<c>dreamair-display-check.exe</c>), which drives the headset's panels through
/// NVIDIA's private direct-mode interface for about 20 s without Monado. NVIDIA may move that interface with any driver,
/// so Mirador runs it when the driver changed. The headset's own software and Monado must not run: the check refuses
/// while they hold the display (the headset's module frees it first).
/// </summary>
public static class DisplayCheck
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>Runs the check at <paramref name="exe"/> and reads its verdict. Never throws for a failed check.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the check was ended first.</exception>
    public static async Task<DisplayCheckResult> RunAsync(string exe, Func<ProcessSpec, IConsoleProcess> start, CancellationToken ct)
    {
        var lines = new List<string>();
        int exitCode;
        try
        {
            using var process = start(new ProcessSpec(
                exe, "", Path.GetDirectoryName(exe) ?? ".", ConsoleProcess.CurrentEnvironment()));
            process.Line += line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            };
            process.CloseInput();
            try
            {
                exitCode = await process.Exited.WaitAsync(Limit, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await KillAsync(process).ConfigureAwait(false);
                return Result(false, $"The display check didn't finish within {Limit.TotalSeconds:0} s.", null, lines) with { Finished = false };
            }
            catch (OperationCanceledException)
            {
                await KillAsync(process).ConfigureAwait(false);
                throw;
            }
        }
        catch (Core.MiradorException e)
        {
            return Result(false, e.Message, null, lines) with { Finished = false };
        }

        List<string> output;
        lock (lines)
        {
            output = [.. lines];
        }

        var verdict = output.LastOrDefault(l => After(l, "PASS:") is not null || After(l, "FAIL:") is not null);
        var message = verdict is null ? null : After(verdict, "PASS:") ?? After(verdict, "FAIL:");
        var whatToDo = output.Select(l => After(l, "what to do:")).LastOrDefault(t => t is not null);
        var pass = exitCode == 0;
        message ??= pass ? "Direct mode works." : $"The display check failed (exit code {exitCode}).";
        return Result(pass, message, whatToDo, output);
    }

    /// <summary>Ends the check and waits a moment for it to let go of the display.</summary>
    private static async Task KillAsync(IConsoleProcess process)
    {
        process.Kill();
        await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    private static DisplayCheckResult Result(bool pass, string message, string? whatToDo, List<string> lines)
    {
        var output = new StringBuilder();
        lock (lines)
        {
            foreach (var line in lines)
            {
                output.Append(line).Append('\n');
            }
        }

        return new DisplayCheckResult(pass, message, whatToDo, output.ToString());
    }

    /// <summary>The text after <paramref name="marker"/> in <paramref name="line"/>, trimmed; null when the line doesn't hold it.</summary>
    /// <remarks>The marker must start a word: <c>BYPASS:</c> isn't <c>PASS:</c>.</remarks>
    private static string? After(string line, string marker)
    {
        for (var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = line.IndexOf(marker, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (at == 0 || !char.IsLetterOrDigit(line[at - 1]))
            {
                var text = line[(at + marker.Length)..].Trim();
                return text.Length == 0 ? null : text;
            }
        }

        return null;
    }
}
