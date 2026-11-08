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

        public Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct)
        {
            Commands.Enqueue(request.Command);
            return Task.FromResult(request.Command switch
            {
                HelperCommand.StartMonadoElevated => Started(request.Start),
                HelperCommand.WaitMonadoElevated => WaitReplies.TryDequeue(out var r) ? r : new HelperReply(true),
                _ => new HelperReply(true),
            });
        }

        private HelperReply Started(ElevatedStart? start)
        {
            StartSent = start;
            return new HelperReply(true, Value: StartPid);
        }
    }

    private static HelperProcessLauncher Launcher(FakeElevatedClient client, string logPath) =>
        new(client, Start, logPath)
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            FollowInterval = TimeSpan.FromMilliseconds(20),
        };

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
        var client = new FakeElevatedClient();

        await using var process = Launcher(client, dir.File("log")).Start(Spec);
        process.Kill();

        // The stop reaches the helper (the kill is best-effort and returns at once).
        var deadline = DateTime.UtcNow + Limit;
        while (!client.Commands.Contains(HelperCommand.StopMonadoElevated) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains(HelperCommand.StopMonadoElevated, client.Commands);
    }
}
