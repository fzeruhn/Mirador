using System.IO.Pipes;
using System.Text;
using Mirador.Ipc;

namespace Mirador.Core.Tests;

public class AppPipeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static string UniqueName() => "mirador-app-test-" + Guid.NewGuid().ToString("N");

    private static Task<string> Answer(string line, CancellationToken ct) =>
        Task.FromResult(line == AppPipe.Start ? AppPipe.Ok : AppPipe.FailPrefix + "unknown request");

    [Fact]
    public async Task Start_gets_the_handlers_ok()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, Answer);
        server.Start();

        Assert.Equal("OK", await AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None));
    }

    [Fact]
    public async Task The_handler_learns_which_process_asked()
    {
        var name = UniqueName();
        int? seen = null;
        await using var server = new AppPipeServer(name, (line, caller, ct) =>
        {
            seen = caller;
            return Task.FromResult(AppPipe.Ok);
        });
        server.Start();

        Assert.Equal("OK", await AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None));
        Assert.Equal(Environment.ProcessId, seen);
    }

    [Fact]
    public async Task An_unknown_line_is_up_to_the_handler()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, Answer);
        server.Start();

        Assert.Equal("FAIL unknown request", await AppPipeClient.SendAsync(name, "DANCE", Limit, CancellationToken.None));
    }

    [Fact]
    public async Task Two_clients_at_once_are_both_answered()
    {
        var name = UniqueName();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        await using var server = new AppPipeServer(name, async (line, ct) =>
        {
            // Neither answer comes before both requests are in: they are served side by side.
            if (Interlocked.Increment(ref waiting) == 2)
            {
                both.TrySetResult();
            }

            await both.Task.WaitAsync(ct);
            return AppPipe.Ok;
        });
        server.Start();

        var replies = await Task.WhenAll(
            AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None),
            AppPipeClient.SendAsync(name, AppPipe.Show, Limit, CancellationToken.None));

        Assert.Equal(new string?[] { "OK", "OK" }, replies);
    }

    [Fact]
    public async Task A_line_too_long_gets_no_answer_and_the_server_keeps_serving()
    {
        var name = UniqueName();
        var asked = 0;
        await using var server = new AppPipeServer(name, (line, ct) =>
        {
            Interlocked.Increment(ref asked);
            return Answer(line, ct);
        });
        server.Start();

        Assert.Null(await AppPipeClient.SendAsync(name, new string('A', 300), Limit, CancellationToken.None));
        Assert.Equal("OK", await AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_line_of_exactly_the_limit_is_read()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, (line, ct) => Task.FromResult(line.Length.ToString()));
        server.Start();

        Assert.Equal("256", await AppPipeClient.SendAsync(name, new string('A', 256), Limit, CancellationToken.None));
    }

    [Fact]
    public async Task A_caller_that_never_sends_is_dropped()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, Answer) { ReadLimit = TimeSpan.FromMilliseconds(200) };
        server.Start();
        await using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await silent.ConnectAsync((int)Limit.TotalMilliseconds);

        var read = await silent.ReadAsync(new byte[16]).AsTask().WaitAsync(Limit);

        Assert.Equal(0, read);
        Assert.Equal("OK", await AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None));
    }

    [Fact]
    public async Task Replies_are_one_line_of_ascii()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, (line, ct) => Task.FromResult("FAIL Monado stopped.\r\nLast lines: Zoë"));
        server.Start();

        Assert.Equal("FAIL Monado stopped. Last lines: Zo?", await AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None));
    }

    [Fact]
    public async Task No_server_gives_null_within_the_timeout()
    {
        var reply = await AppPipeClient.SendAsync(UniqueName(), AppPipe.Show, TimeSpan.FromMilliseconds(300), CancellationToken.None).WaitAsync(Limit);

        Assert.Null(reply);
    }

    [Fact]
    public async Task A_second_server_on_the_same_name_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // first pipe instances are Windows' own
        }

        var name = UniqueName();
        await using var server = new AppPipeServer(name, Answer);
        server.Start();
        await using var second = new AppPipeServer(name, Answer);

        Assert.Throws<UnauthorizedAccessException>(second.Start);
    }

    [Fact]
    public async Task A_handler_that_throws_answers_fail_and_says_so()
    {
        var name = UniqueName();
        var reported = new List<Exception>();
        await using var server = new AppPipeServer(name, (_, _) => throw new InvalidOperationException("The session is broken."));
        server.HandlerFailed += e => { lock (reported) { reported.Add(e); } };
        server.Start();

        var reply = await AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None);

        Assert.Equal("FAIL Mirador failed: The session is broken.", reply);
        lock (reported)
        {
            Assert.IsType<InvalidOperationException>(Assert.Single(reported));
        }
    }

    [Fact]
    public async Task A_caller_that_never_reads_its_reply_doesnt_hold_up_closing()
    {
        var name = UniqueName();
        var server = new AppPipeServer(name, Answer) { ReadLimit = TimeSpan.FromMilliseconds(300) };
        server.Start();
        await using var rude = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await rude.ConnectAsync((int)Limit.TotalMilliseconds);
        await rude.WriteAsync(Encoding.ASCII.GetBytes("SHOW\n"));
        await Task.Delay(100);

        await server.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    [Fact]
    public async Task Closing_while_a_start_runs_cancels_it_and_waits_for_it()
    {
        var name = UniqueName();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new AppPipeServer(name, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return AppPipe.Ok;
        });
        server.Start();
        var reply = AppPipeClient.SendAsync(name, AppPipe.Start, Limit, CancellationToken.None);
        await started.Task.WaitAsync(Limit);

        await server.DisposeAsync().AsTask().WaitAsync(Limit);

        Assert.Null(await reply.WaitAsync(Limit));
    }

    [Fact]
    public async Task More_callers_than_slots_are_all_answered()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, async (line, ct) =>
        {
            await Task.Delay(50, ct);
            return AppPipe.Ok;
        });
        server.Start();

        var replies = await Task.WhenAll(Enumerable.Range(0, AppPipeServer.MaxServed + 8)
            .Select(_ => AppPipeClient.SendAsync(name, AppPipe.Show, Limit, CancellationToken.None)));

        Assert.All(replies, r => Assert.Equal("OK", r));
    }

    [Fact]
    public void The_name_is_per_windows_session()
    {
        Assert.Equal("mirador-app-3", AppPipe.NameFor(3));
        Assert.StartsWith("mirador-app-", AppPipe.CurrentName());
    }

    [Fact]
    public void Long_messages_are_cut_to_fit()
    {
        var line = AppPipe.OneLine(AppPipe.FailPrefix + new string('x', 400));

        Assert.Equal(AppPipe.MaxLineBytes, Encoding.ASCII.GetByteCount(line));
        Assert.EndsWith("...", line);
    }
}
