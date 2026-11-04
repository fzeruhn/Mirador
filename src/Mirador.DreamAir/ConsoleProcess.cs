using System.ComponentModel;
using System.Diagnostics;
using Mirador.Core;
using Mirador.Core.Monado;

namespace Mirador.DreamAir;

/// <summary>
/// A console program Mirador runs for a moment and talks to through its standard input: Tobii's eye tracking runtime
/// for the geometry upload, the layer's display check. The seam that lets those be tested without the programs.
/// </summary>
public interface IConsoleProcess : IDisposable
{
    /// <summary>
    /// Raised on a background thread for each line the program writes, standard output and error alike. Lines
    /// written before the first handler was added are delivered to it as it is added.
    /// </summary>
    event Action<string>? Line;

    bool HasExited { get; }

    /// <summary>Completes with the exit code once the program has ended and its output has been read.</summary>
    Task<int> Exited { get; }

    /// <summary>Writes a line to the program's standard input.</summary>
    void WriteLine(string s);

    /// <summary>Closes the program's standard input.</summary>
    void CloseInput();

    /// <summary>Waits up to <paramref name="t"/> for the program to end; whether it did.</summary>
    bool WaitForExit(TimeSpan t);

    /// <summary>Ends the program and anything it started. Does nothing when it has ended already.</summary>
    void Kill();
}

/// <summary>A console program run through <see cref="Process"/>, with its input and output redirected and no window.</summary>
public sealed class ConsoleProcess : IConsoleProcess
{
    private readonly Process process;
    private readonly object gate = new();
    private Action<string>? listeners;
    private List<string>? early = [];

    private ConsoleProcess(Process process)
    {
        this.process = process;
        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Exited = WaitAsync();
    }

    public event Action<string>? Line
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (gate)
            {
                listeners += value;
                if (early is { } replay)
                {
                    early = null;
                    foreach (var line in replay)
                    {
                        value(line);
                    }
                }
            }
        }

        remove
        {
            lock (gate)
            {
                listeners -= value;
            }
        }
    }

    public bool HasExited => process.HasExited;

    public Task<int> Exited { get; }

    /// <summary>
    /// Starts <paramref name="spec"/>'s program with exactly its environment, standard input, output and error
    /// redirected, and no window. Matches <c>Func&lt;ProcessSpec, IConsoleProcess&gt;</c>.
    /// </summary>
    /// <exception cref="MiradorException">The program couldn't be started; the message says which and why.</exception>
    public static IConsoleProcess Start(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            info.Environment[name] = value;
        }

        try
        {
            var process = Process.Start(info)
                ?? throw new MiradorException($"Couldn't start {spec.FileName}.");
            return new ConsoleProcess(process);
        }
        catch (Win32Exception e)
        {
            throw new MiradorException($"Couldn't start {spec.FileName}: {e.Message.Trim().TrimEnd('.')}.");
        }
    }

    /// <summary>Mirador's own environment, for a <see cref="ProcessSpec"/> that should get what Mirador has.</summary>
    public static IReadOnlyDictionary<string, string> CurrentEnvironment()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                found[name] = value;
            }
        }

        return found;
    }

    public void WriteLine(string s)
    {
        try
        {
            process.StandardInput.WriteLine(s);
            process.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The program has ended or closed its input; there is no one left to tell.
        }
    }

    public void CloseInput()
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
        }
    }

    public bool WaitForExit(TimeSpan t) => process.WaitForExit(t);

    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It ended on its own in the meantime, or Windows wouldn't let it be ended; nothing more to do.
        }
    }

    public void Dispose() => process.Dispose();

    private async Task<int> WaitAsync()
    {
        // Waits for the redirected output to be read to the end too.
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private void OnData(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (gate)
        {
            if (early is not null)
            {
                early.Add(line);
                return;
            }

            listeners?.Invoke(line);
        }
    }
}
