using System.Collections.Concurrent;
using System.Text;
using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Monado;

public class HelperProcessLauncherTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static readonly ElevatedStart Start = new(3000, 100, 6, false, "dreamair");

    private static readonly ProcessSpec Spec = new(
        @"C:\does-not-matter\monado-service.exe", "", @"C:\does-not-matter", new Dictionary<string, string>());

    private sealed class FakeElevatedClient : IHelperClient
    {
        public ConcurrentQueue<HelperCommand> Commands { get; } = new();
        public ElevatedStart? StartSent { get; private set; }
        public string StartPid { get; set; } = "1234";

        /// <summary>The replies for successive <see cref="HelperCommand.WaitMonadoElevated"/> polls; the last repeats.</summary>
        public ConcurrentQueue<HelperReply> WaitReplies { get; } = new();

        public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct) => SendAsync(new HelperRequest(command), ct);

        /// <summary>
        /// When set, Monado "ends" only after a Stop, and only once this many Wait polls after it have said it still runs
        /// (as the helper's job takes a moment to end the process); <see cref="WaitReplies"/> is then not used.
        /// </summary>
        public int? PollsAfterStop { get; set; }

        /// <summary>Whether the Stop has reached the helper.</summary>
        public bool Stopped { get; private set; }

        private int pollsSinceStop;

        /// <summary>The Clean flag of each Stop sent.</summary>
        public ConcurrentQueue<bool> StopsClean { get; } = new();

        public Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct)
        {
            Commands.Enqueue(request.Command);
            if (request.Command == HelperCommand.StopMonadoElevated)
            {
                StopsClean.Enqueue(request.Clean == true);
            }

            return Task.FromResult(request.Command switch
            {
                HelperCommand.StartMonadoElevated => Started(request.Start),
                HelperCommand.StopMonadoElevated => StopSent(),
                HelperCommand.WaitMonadoElevated when PollsAfterStop is { } polls => AfterStop(polls),
                HelperCommand.WaitMonadoElevated => WaitReplies.TryDequeue(out var r) ? r : new HelperReply(true),
                _ => new HelperReply(true),
            });
        }

        private HelperReply StopSent()
        {
            Stopped = true;
            return new HelperReply(true);
        }

        private HelperReply AfterStop(int polls) =>
            Stopped && Interlocked.Increment(ref pollsSinceStop) > polls
                ? new HelperReply(true, Value: "0")
                : new HelperReply(true);

        /// <summary>What the helper answers a Start with instead of starting Monado; null: it starts it.</summary>
        public HelperReply? StartRefusal { get; set; }

        private HelperReply Started(ElevatedStart? start)
        {
            StartSent = start;
            return StartRefusal ?? new HelperReply(true, Value: StartPid);
        }
    }

    /// <summary>The job launcher, which runs Monado as the user: records what it was asked to start.</summary>
    private sealed class FakeUserLauncher : IProcessLauncher
    {
        public List<ProcessSpec> Specs { get; } = [];

        public StubProcess? Started { get; private set; }

        public IRunningProcess Start(ProcessSpec spec)
        {
            Specs.Add(spec);
            return Started = new StubProcess();
        }
    }

    private sealed class StubProcess : IRunningProcess
    {
        public int Id => 777;

        public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;

        public event Action<string>? Line { add { } remove { } }

        public event Action<Exception>? ListenerFailed { add { } remove { } }

        public void Kill()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static HelperProcessLauncher Launcher(
        FakeElevatedClient client, string logPath, IProcessLauncher? asUser = null, Action<bool, string?>? steamVrChecked = null,
        TimeSpan? stopLimit = null) =>
        new(client, Start, logPath, asUser ?? new FakeUserLauncher())
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            FollowInterval = TimeSpan.FromMilliseconds(20),
            SteamVrChecked = steamVrChecked,
            StopLimit = stopLimit ?? TimeSpan.FromSeconds(5),
        };

    [Fact]
    public async Task Changed_steamvr_runs_as_the_user()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient
        {
            StartRefusal = new HelperReply(
                false,
                "SteamVR's Lighthouse driver changed since you allowed Monado as administrator (ArcturusPerception.dll).",
                Refusal: HelperRefusal.SteamVrChanged),
        };
        var asUser = new FakeUserLauncher();
        var told = new List<(bool Changed, string? Why)>();

        await using var process = Launcher(client, dir.File("log"), asUser, (changed, why) => told.Add((changed, why))).Start(Spec);

        // The same Start, through the job launcher with the spec the session built, instead of failing.
        Assert.Same(asUser.Started, process);
        Assert.Same(Spec, Assert.Single(asUser.Specs));
        Assert.Equal([HelperCommand.StartMonadoElevated], client.Commands); // no wait or stop for a Monado that isn't the helper's

        // Mirador hears why, with the files the helper named, for Home's "Runs as you" line.
        var (changed, why) = Assert.Single(told);
        Assert.True(changed);
        Assert.Contains("ArcturusPerception.dll", why);
    }

    [Fact]
    public void Other_refusals_still_fail_the_start()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient
        {
            StartRefusal = new HelperReply(false, "SteamVR's driver file x.dll couldn't be opened."),
        };
        var asUser = new FakeUserLauncher();
        var told = new List<bool>();

        var e = Assert.Throws<MiradorException>(() => Launcher(client, dir.File("log"), asUser, (changed, _) => told.Add(changed)).Start(Spec));

        Assert.Equal("SteamVR's driver file x.dll couldn't be opened.", e.Message);
        Assert.Empty(asUser.Specs);
        Assert.Empty(told);
    }

    [Fact]
    public async Task An_elevated_start_says_steamvr_is_as_allowed()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient();
        var asUser = new FakeUserLauncher();
        var told = new List<bool>();

        await using var process = Launcher(client, dir.File("log"), asUser, (changed, _) => told.Add(changed)).Start(Spec);

        Assert.Equal(1234, process.Id);
        Assert.Empty(asUser.Specs);
        Assert.Equal([false], told);
    }

    [Fact]
    public void Start_sends_the_settings_and_returns_the_pid()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient { StartPid = "4242" };

        var process = Launcher(client, dir.File("log")).Start(Spec);

        Assert.Equal(4242, process.Id);
        Assert.Equal(Start, client.StartSent);
        Assert.Contains(HelperCommand.StartMonadoElevated, client.Commands);
    }

    [Fact]
    public async Task Lines_follow_the_log()
    {
        using var dir = new TempDir();
        var log = dir.File("monado.log");
        File.WriteAllText(log, "before the handler\n", new UTF8Encoding(false));
        var client = new FakeElevatedClient();

        await using var process = Launcher(client, log).Start(Spec);

        var seen = new ConcurrentQueue<string>();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Line += line =>
        {
            seen.Enqueue(line);
            if (seen.Count >= 2)
            {
                got.TrySetResult();
            }
        };

        // Append a line while Monado "runs".
        await File.AppendAllTextAsync(log, "after the handler\n", new UTF8Encoding(false));

        await got.Task.WaitAsync(Limit);
        Assert.Equal(["before the handler", "after the handler"], seen);
    }

    [Fact]
    public async Task Exit_comes_from_the_wait()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient();
        client.WaitReplies.Enqueue(new HelperReply(true));              // still running
        client.WaitReplies.Enqueue(new HelperReply(true));              // still running
        client.WaitReplies.Enqueue(new HelperReply(true, Value: "7"));  // ended, exit code 7

        await using var process = Launcher(client, dir.File("log")).Start(Spec);

        var code = await process.Exited.WaitAsync(Limit);
        Assert.Equal(7, code);
    }

    [Fact]
    public async Task Kill_sends_stop()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient { PollsAfterStop = 0 };

        await using var process = Launcher(client, dir.File("log")).Start(Spec);
        process.Kill();

        // The stop reaches the helper (the kill returns at once; disposing waits for the exit).
        var deadline = DateTime.UtcNow + Limit;
        while (!client.Commands.Contains(HelperCommand.StopMonadoElevated) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains(HelperCommand.StopMonadoElevated, client.Commands);
        Assert.Equal([false], client.StopsClean);
    }

    [Fact]
    public async Task Ending_cleanly_asks_the_helper_for_the_clean_stop_and_waits_for_the_exit()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient { PollsAfterStop = 0 };

        await using var process = Launcher(client, dir.File("log")).Start(Spec);

        Assert.True(await process.EndCleanlyAsync(TimeSpan.FromSeconds(3)).WaitAsync(Limit));
        Assert.Equal([true], client.StopsClean);
        process.Kill(); // after the clean stop: nothing more is sent
        Assert.Single(client.StopsClean);
    }

    [Fact]
    public async Task Dispose_after_kill_waits_for_the_exit()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient { PollsAfterStop = 3 };

        var process = Launcher(client, dir.File("log")).Start(Spec);
        process.Kill();
        await process.DisposeAsync().AsTask().WaitAsync(Limit);

        // Disposing returned only once the helper reported Monado's real exit (not the "unknown" -1 at once), so the
        // next Start (Restart) finds no elevated Monado still running.
        Assert.True(process.Exited.IsCompleted);
        Assert.Equal(0, await process.Exited);
        var commands = client.Commands.ToList();
        var stopAt = commands.IndexOf(HelperCommand.StopMonadoElevated);
        Assert.True(stopAt >= 0);
        Assert.True(commands.Skip(stopAt + 1).Count(c => c == HelperCommand.WaitMonadoElevated) >= 4);
    }

    [Fact]
    public async Task Dispose_after_kill_gives_up_at_the_limit()
    {
        using var dir = new TempDir();
        var client = new FakeElevatedClient { PollsAfterStop = int.MaxValue }; // never ends

        var process = Launcher(client, dir.File("log"), stopLimit: TimeSpan.FromMilliseconds(300)).Start(Spec);
        process.Kill();

        // The stop goes out on the thread pool, which a busy test run (CI) may start later than the limit: wait for it
        // to reach the helper, so that the limit is what Dispose then waits for Monado's exit.
        var until = DateTime.UtcNow + Limit;
        while (!client.Stopped && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await process.DisposeAsync().AsTask().WaitAsync(Limit);

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(250), $"returned after {clock.Elapsed}");
        Assert.True(client.Stopped);
        Assert.Equal(-1, await process.Exited);
    }
}
