using System.ComponentModel;
using System.Diagnostics;

namespace Mirador.Core.Monado;

/// <summary>A started <see cref="Process"/> whose standard output and error are read line by line.</summary>
internal sealed class RunningProcess : IRunningProcess
{
    /// <summary>Lines kept for a first listener, so output from before it was added is not lost.</summary>
    private const int MaxEarlyLines = 1000;

    /// <summary>How long after the process ended its output may stay open (held by a child that outlived it) before <see cref="Exited"/> completes anyway.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DisposeLimit = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly object gate = new();
    private Action<string>? listeners;
    private List<string>? early = [];
    private int disposed;

    /// <param name="started">A process that was started with its standard output and error redirected.</param>
    public RunningProcess(Process started)
    {
        process = started;
        Id = started.Id;
        var output = Task.WhenAll(PumpAsync(started.StandardOutput), PumpAsync(started.StandardError));
        Exited = AwaitExitAsync(output);
    }

    public int Id { get; }

    public Task<int> Exited { get; }

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
                if (early is { } missed)
                {
                    early = null;
                    foreach (var text in missed)
                    {
                        Deliver(value, text);
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

    public void Kill()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            KillCore();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            KillCore();
            await Exited.WaitAsync(DisposeLimit).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or TimeoutException)
        {
            // The process would not end; dispose must not throw, so release the handles anyway.
            // The job still ends it when the launcher or Mirador goes away.
        }

        process.Dispose();
    }

    private void KillCore()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Windows says "access denied" or "no such process" when the process ended a moment ago: that is a no-op.
            // A process that is still running and can't be killed is a real failure.
            if (!process.HasExited)
            {
                throw;
            }
        }
    }

    private async Task<int> AwaitExitAsync(Task output)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        var code = process.ExitCode;
        try
        {
            await output.WaitAsync(DrainGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Something the process started still holds its output open; stop waiting for it.
        }

        return code;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } text)
            {
                Publish(text);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The process was disposed while we were still reading.
        }
    }

    private void Publish(string text)
    {
        lock (gate)
        {
            if (listeners is { } handler)
            {
                Deliver(handler, text);
            }
            else if (early is { Count: < MaxEarlyLines })
            {
                early.Add(text);
            }
        }
    }

    /// <summary>A listener's failure is its own: it must not stop the reading, or the process would block on a full pipe.</summary>
    private static void Deliver(Action<string> handler, string text)
    {
        try
        {
            handler(text);
        }
        catch (Exception)
        {
        }
    }
}
