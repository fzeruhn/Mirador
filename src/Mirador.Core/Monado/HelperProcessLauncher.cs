using System.Globalization;
using System.Text;
using Mirador.Ipc;

namespace Mirador.Core.Monado;

/// <summary>
/// An <see cref="IProcessLauncher"/> that starts Monado as administrator through the helper, for a user who allowed it.
/// It never passes the spec's program or environment: it sends <see cref="HelperCommand.StartMonadoElevated"/> with the
/// typed settings and module it was built with. Its <see cref="IRunningProcess"/> follows the helper's log file for the
/// session's lines, learns the exit code by polling <see cref="HelperCommand.WaitMonadoElevated"/>, and stops Monado
/// with <see cref="HelperCommand.StopMonadoElevated"/>.
/// </summary>
public sealed class HelperProcessLauncher(IHelperClient client, ElevatedStart start, string logPath) : IProcessLauncher
{
    /// <summary>How often the exit is polled.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the log file is read for new lines.</summary>
    internal TimeSpan FollowInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Asks the helper to start the elevated service and returns a process that follows its log and its exit. The spec
    /// is ignored on purpose: the helper runs only its own bundled service, with the environment it builds itself.
    /// </summary>
    /// <exception cref="MiradorException">The helper refused or couldn't start it; the message is for the user.</exception>
    public IRunningProcess Start(ProcessSpec spec)
    {
        var reply = client.SendAsync(new HelperRequest(HelperCommand.StartMonadoElevated, start), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!reply.Ok)
        {
            throw new MiradorException(reply.Error ?? "Mirador's helper service couldn't start Monado as administrator.");
        }

        var id = int.TryParse(reply.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
        return new ElevatedRunningProcess(client, id, logPath, PollInterval, FollowInterval);
    }
}

/// <summary>
/// A <see cref="HelperCommand.StartMonadoElevated"/> seen as an <see cref="IRunningProcess"/>: its id from the reply,
/// its lines from following the helper's log file, its exit from polling the helper, and <see cref="Kill"/> through
/// <see cref="HelperCommand.StopMonadoElevated"/>.
/// </summary>
internal sealed class ElevatedRunningProcess : IRunningProcess
{
    private const int MaxEarlyLines = 1000;

    /// <summary>The exit code reported when the helper can't be reached (its job ended Monado with it).</summary>
    private const int ExitUnknown = -1;

    private static readonly TimeSpan DisposeLimit = TimeSpan.FromSeconds(5);

    private readonly IHelperClient client;
    private readonly string logPath;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan followInterval;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task follow;

    private readonly object gate = new();
    private Action<string>? listeners;
    private List<string>? early = [];
    private bool disposed;

    public ElevatedRunningProcess(IHelperClient client, int id, string logPath, TimeSpan pollInterval, TimeSpan followInterval)
    {
        this.client = client;
        this.logPath = logPath;
        this.pollInterval = pollInterval;
        this.followInterval = followInterval;
        Id = id;
        Exited = exited.Task;
        follow = Task.Run(() => FollowAsync(stop.Token));
        _ = Task.Run(() => PollExitAsync(stop.Token));
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
        // An unelevated Mirador can't end an elevated process; the helper does, through its job object. Best-effort and
        // returns at once; the exit then shows up through the poll (or the helper ending the job when Mirador exits).
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SendAsync(HelperCommand.StopMonadoElevated, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing more to do: the job ends Monado when this Mirador exits, at the latest.
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        exited.TrySetResult(ExitUnknown);
        try
        {
            await follow.WaitAsync(DisposeLimit).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispose must not throw: the follow loop is cancelled either way.
        }

        stop.Dispose();
    }

    private async Task PollExitAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var reply = await client.SendAsync(HelperCommand.WaitMonadoElevated, ct).ConfigureAwait(false);
                if (reply.Value is { } value && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    exited.TrySetResult(code);
                    return;
                }

                if (reply.NotDelivered)
                {
                    // The helper has gone, so its job ended Monado; stop waiting.
                    exited.TrySetResult(ExitUnknown);
                    return;
                }

                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposing.
        }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        long position = 0;
        var pending = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(logPath))
                    {
                        await using var file = new FileStream(
                            logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (file.Length < position)
                        {
                            // The file was emptied and started again: read it from the top.
                            position = 0;
                            pending.Clear();
                            decoder = Encoding.UTF8.GetDecoder();
                        }

                        file.Position = position;
                        var buffer = new byte[8192];
                        int read;
                        while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            position += read;
                            var chars = new char[decoder.GetCharCount(buffer, 0, read)];
                            var got = decoder.GetChars(buffer, 0, read, chars, 0);
                            for (var i = 0; i < got; i++)
                            {
                                if (chars[i] == '\n')
                                {
                                    Publish(pending.ToString().TrimEnd('\r'));
                                    pending.Clear();
                                }
                                else
                                {
                                    pending.Append(chars[i]);
                                }
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The helper may be writing or recreating the file; try again next tick.
                }

                await Task.Delay(followInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposing.
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
