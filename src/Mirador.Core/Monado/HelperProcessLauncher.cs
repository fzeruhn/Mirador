using System.Globalization;
using System.Text;
using Mirador.Ipc;

namespace Mirador.Core.Monado;

/// <summary>
/// An <see cref="IProcessLauncher"/> that starts Monado as administrator through the helper, for a user who allowed it.
/// It never passes the spec's program or environment: it sends <see cref="HelperCommand.StartMonadoElevated"/> with the
/// typed settings and module it was built with. Its <see cref="IRunningProcess"/> follows the helper's log file for the
/// session's lines, learns the exit code by polling <see cref="HelperCommand.WaitMonadoElevated"/>, and stops Monado
/// with <see cref="HelperCommand.StopMonadoElevated"/>. When the helper refuses because SteamVR's Lighthouse driver
/// changed since the user allowed it (<see cref="HelperRefusal.SteamVrChanged"/>), the same Start runs as the user
/// through <paramref name="asUser"/> instead (Mirador.md section 4.10 item 46).
/// </summary>
/// <param name="asUser">The job launcher, which runs Monado as the user: what every Start that isn't elevated uses.</param>
public sealed class HelperProcessLauncher(IHelperClient client, ElevatedStart start, string logPath, IProcessLauncher asUser) : IProcessLauncher
{
    /// <summary>
    /// Told after each Start the helper answered either way: <c>true</c> with the helper's message (which names the
    /// files) when SteamVR's Lighthouse driver changed since the user allowed Monado as administrator, so this Start ran
    /// as the user; <c>false</c> when Monado started as administrator. Not told when the Start failed.
    /// </summary>
    public Action<bool, string?>? SteamVrChecked { get; init; }

    /// <summary>How often the exit is polled.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the log file is read for new lines.</summary>
    internal TimeSpan FollowInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long disposing a killed process waits for the helper to report Monado's exit.</summary>
    internal TimeSpan StopLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks the helper to start the elevated service and returns a process that follows its log and its exit. The spec
    /// goes only to <c>asUser</c>, when SteamVR changed: the helper runs only its own bundled service, with the
    /// environment it builds itself.
    /// </summary>
    /// <exception cref="MiradorException">The helper refused for any other reason, or couldn't start it (or the
    /// Start as the user failed); the message is for the user.</exception>
    public IRunningProcess Start(ProcessSpec spec)
    {
        var reply = client.SendAsync(new HelperRequest(HelperCommand.StartMonadoElevated, start), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!reply.Ok && reply.Refusal == HelperRefusal.SteamVrChanged)
        {
            // Not a failure: the user allowed Monado as administrator with other copies of SteamVR's unsigned DLLs (an
            // update since). This Start runs as the user, as it would without consent, and Home offers to allow again.
            SteamVrChecked?.Invoke(true, reply.Error);
            return asUser.Start(spec);
        }

        if (!reply.Ok)
        {
            throw new MiradorException(reply.Error ?? "Mirador's helper service couldn't start Monado as administrator.");
        }

        var id = int.TryParse(reply.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
        var process = new ElevatedRunningProcess(client, id, logPath, PollInterval, FollowInterval, StopLimit);
        SteamVrChecked?.Invoke(false, null);
        return process;
    }
}

/// <summary>
/// A <see cref="HelperCommand.StartMonadoElevated"/> seen as an <see cref="IRunningProcess"/>: its id from the reply,
/// its lines from following the helper's log file, its exit from polling the helper, and <see cref="Kill"/> through
/// <see cref="HelperCommand.StopMonadoElevated"/>. Like the job launcher's process, disposing it after a kill waits
/// (at most the stop limit) until Monado has really ended, so a Start right after a Stop isn't refused because the
/// old one still runs, and the headset is free before a module's end takes it back.
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
    private readonly TimeSpan stopLimit;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task follow;

    private readonly object gate = new();
    private Action<string>? listeners;
    private List<string>? early = [];
    private bool disposed;
    private Task? stopSent;

    public ElevatedRunningProcess(
        IHelperClient client, int id, string logPath, TimeSpan pollInterval, TimeSpan followInterval, TimeSpan stopLimit)
    {
        this.client = client;
        this.logPath = logPath;
        this.pollInterval = pollInterval;
        this.followInterval = followInterval;
        this.stopLimit = stopLimit;
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

    public void Kill() => SendStop(clean: false);

    /// <summary>
    /// The helper asks Monado to end by itself, then ends what is left, and answers once it has (within the stop limit);
    /// true once the poll has seen the exit, within <paramref name="limit"/> after that.
    /// </summary>
    public async Task<bool> EndCleanlyAsync(TimeSpan limit)
    {
        if (SendStop(clean: true) is not { } sent)
        {
            return exited.Task.IsCompleted;
        }

        try
        {
            await sent.WaitAsync(stopLimit).ConfigureAwait(false);
            await exited.Task.WaitAsync(limit).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// An unelevated Mirador can't end an elevated process; the helper does, through its job object. Returns at once with
    /// the request on its way (null when one was sent already); the exit then shows up through the poll, which
    /// DisposeAsync waits for (or the helper ends the job when Mirador exits).
    /// </summary>
    private Task? SendStop(bool clean)
    {
        lock (gate)
        {
            if (stopSent is not null || disposed)
            {
                return null;
            }

            stopSent = Task.Run(async () =>
            {
                try
                {
                    await client.SendAsync(new HelperRequest(HelperCommand.StopMonadoElevated, Clean: clean), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Nothing more to do: the job ends Monado when this Mirador exits, at the latest.
                }
            });
            return stopSent;
        }
    }

    /// <summary>
    /// After <see cref="Kill"/>: waits for the stop to reach the helper, then for the poll to see Monado's exit, at most
    /// the stop limit in all (then the exit is reported as unknown). Then stops following the log.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? sent;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sent = stopSent;
        }

        if (sent is not null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await sent.WaitAsync(stopLimit).ConfigureAwait(false);
                var left = stopLimit - clock.Elapsed;
                if (left > TimeSpan.Zero)
                {
                    await exited.Task.WaitAsync(left).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                // Monado didn't end in time (or the helper didn't answer): report the exit as unknown.
            }
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
