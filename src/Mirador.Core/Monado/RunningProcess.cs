using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    private readonly Action<Process> terminate;

    /// <summary>
    /// The launcher's job, held so that it stays open as long as this object is reachable. The launcher alone owning the
    /// handle would let its finalizer close the job, and Windows end the process, when the launcher is no longer used.
    /// </summary>
    private readonly SafeHandle job;

    private readonly object gate = new();
    private readonly object lifecycle = new();
    private Action<string>? listeners;
    private List<string>? early = [];
    private bool disposed;

    /// <param name="started">A process that was started with its standard output and error redirected.</param>
    /// <param name="job">The job the process is in.</param>
    /// <param name="terminate">How to end the process and its children; the default ends the whole tree. Replaced in tests.</param>
    public RunningProcess(Process started, SafeHandle job, Action<Process>? terminate = null)
    {
        this.job = job;
        this.terminate = terminate ?? (p => p.Kill(entireProcessTree: true));
        process = started;
        Id = started.Id;
        var output = Task.WhenAll(PumpAsync(started.StandardOutput), PumpAsync(started.StandardError));
        Exited = AwaitExitAsync(output);
    }

    public int Id { get; }

    public Task<int> Exited { get; }

    public event Action<Exception>? ListenerFailed;

    public event Action<string>? Line
    {
        add
        {
            if (value is null)
            {
                return;
            }

            List<Exception>? failures = null;
            lock (gate)
            {
                listeners += value;
                if (early is { } missed)
                {
                    early = null;
                    foreach (var text in missed)
                    {
                        Deliver(value, text, ref failures);
                    }
                }
            }

            Report(failures);
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
        // Under the lifecycle lock so that DisposeAsync can't release the process while a kill is using it.
        lock (lifecycle)
        {
            if (!disposed)
            {
                KillCore();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        try
        {
            KillCore();
            await Exited.WaitAsync(DisposeLimit).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispose must not throw, whatever went wrong while ending the process (a tree that couldn't be ended in
            // full, a process that wouldn't end in time): release the handles anyway. The job still ends it when the
            // launcher is disposed or Mirador goes away.
        }

        lock (lifecycle)
        {
            process.Dispose(); // after any kill that is still under way
        }

        GC.KeepAlive(job);
    }

    private void KillCore()
    {
        try
        {
            terminate(process);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException or ObjectDisposedException)
        {
            // Windows says "access denied" or "no such process" when the process ended a moment ago, and Process.Kill
            // collects what it could not end in an AggregateException: when the process itself is gone that is a no-op.
            // A process that is still running and can't be ended is a real failure.
            if (!HasEnded())
            {
                throw;
            }
        }
    }

    /// <summary>Whether the process has ended; a process object that can't be asked any more counts as ended.</summary>
    private bool HasEnded()
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return false; // can't tell: report the failure that brought us here
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
        List<Exception>? failures = null;
        lock (gate)
        {
            if (listeners is { } handler)
            {
                Deliver(handler, text, ref failures);
            }
            else if (early is { Count: < MaxEarlyLines })
            {
                early.Add(text);
            }
        }

        Report(failures);
    }

    /// <summary>
    /// Gives the line to each listener on its own: one that throws must neither stop the reading (the process would block
    /// on a full pipe) nor keep the listeners after it from the line. The failures are collected for <see cref="Report"/>.
    /// </summary>
    private static void Deliver(Action<string> handler, string text, ref List<Exception>? failures)
    {
        foreach (var each in handler.GetInvocationList())
        {
            try
            {
                ((Action<string>)each)(text);
            }
            catch (Exception e)
            {
                (failures ??= []).Add(e);
            }
        }
    }

    /// <summary>
    /// Raises <see cref="ListenerFailed"/> once per failure, after the lock for the listeners is released so that a failure
    /// handler can't deadlock with them. A failure handler that throws has nobody left to tell and is ignored.
    /// </summary>
    private void Report(List<Exception>? failures)
    {
        if (failures is null || ListenerFailed is not { } handler)
        {
            return;
        }

        foreach (var failure in failures)
        {
            foreach (var each in handler.GetInvocationList())
            {
                try
                {
                    ((Action<Exception>)each)(failure);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
