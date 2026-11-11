using System.Diagnostics;

namespace Mirador.Core.Build;

/// <summary>A command Mirador runs to build or check Monado: a program, its arguments, and where.</summary>
/// <param name="FileName">The program: a full path, or a name found on <c>PATH</c>.</param>
/// <param name="Arguments">Its arguments, each passed as one (no shell, no quoting to get wrong).</param>
/// <param name="WorkingDirectory">Where it runs; null for Mirador's own.</param>
/// <param name="Environment">Variables to set (a null value removes one), on top of Mirador's own environment.</param>
public sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>Runs build and check commands; the seam that lets the builder and the checks be tested without them.</summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="spec"/> to its end, passing each line it writes (output and errors alike) to
    /// <paramref name="onLine"/>, and returns its exit code. Cancelling ends it and everything it started.
    /// </summary>
    /// <exception cref="MiradorException">The program couldn't be started.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the program has been ended.</exception>
    Task<int> RunAsync(CommandSpec spec, Action<string> onLine, CancellationToken ct);
}

/// <summary>Runs commands through <see cref="Process"/>, with no window (<c>CREATE_NO_WINDOW</c>) and their output read.</summary>
public sealed class CommandRunner : ICommandRunner
{
    public async Task<int> RunAsync(CommandSpec spec, Action<string> onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (spec.WorkingDirectory is not null)
        {
            info.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var (name, value) in spec.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Deliver(e.Data);
        process.ErrorDataReceived += (_, e) => Deliver(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new MiradorException($"{Path.GetFileName(spec.FileName)} couldn't be started: {e.Message.TrimEnd('.')}.");
        }

        // Nothing is ever typed into a build: a command that asks for input gets end of input instead of waiting.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }

            throw;
        }

        // WaitForExitAsync returns once the output is read to its end too.
        return process.ExitCode;

        void Deliver(string? line)
        {
            if (line is not null)
            {
                onLine(line);
            }
        }
    }
}
