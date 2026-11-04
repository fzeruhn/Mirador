using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class HelperClientTests
{
    private static string NewName() => "mirador-test-client-" + Guid.NewGuid().ToString("N");

    /// <summary>A pretend helper: accepts one connection and lets <paramref name="behave"/> deal with it.</summary>
    private static async Task WithFakeHelper(string name, Func<NamedPipeServerStream, CancellationToken, Task> behave, Func<Task> client)
    {
        // Buffers: with none, a pipe passes data only while the other side reads, which a pretend helper may never do.
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192);
        using var done = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(done.Token);
            await behave(server, done.Token);
        });
        try
        {
            await client();
        }
        finally
        {
            await done.CancelAsync();
            server.Dispose();
            try { await serving; }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Client_without_helper_explains()
    {
        var client = new HelperClient(NewName());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var reply = await client.SendAsync(HelperCommand.Ping, CancellationToken.None);

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6)); // gives up after the 2 s connect timeout
        Assert.False(reply.Ok);
        Assert.True(reply.NotDelivered);
        Assert.StartsWith("Mirador's helper service isn't running", reply.Error);
        Assert.Equal(
            "Mirador's helper service isn't running. Reinstall Mirador; for a dev build, run tools\\dev-install-helper.ps1 as administrator.",
            reply.Error);
    }

    [Fact]
    public async Task Client_that_the_pipe_turns_away_says_so()
    {
        // A pipe only SYSTEM may open: the caller (an administrator, but not SYSTEM) is refused.
        var name = NewName();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        await using var server = NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, security);

        var reply = await new HelperClient(name).SendAsync(HelperCommand.Ping, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.True(reply.NotDelivered);
        Assert.Equal("Mirador couldn't talk to its helper service (Access to the path is denied). Reinstall Mirador.", reply.Error);
    }


    [Fact]
    public async Task Client_sends_the_command_as_one_line_and_reads_the_reply()
    {
        var name = NewName();
        string? received = null;
        HelperReply? reply = null;

        await WithFakeHelper(name,
            async (server, token) =>
            {
                var request = await LineCodec.ReadAsync<HelperRequest>(server, token);
                received = request!.Command.ToString();
                await LineCodec.WriteAsync(server, new HelperReply(true, WasRunning: false, Value: "v"), token);
                await Task.Delay(200, token);
            },
            async () => reply = await new HelperClient(name).SendAsync(HelperCommand.StartPimax, CancellationToken.None));

        Assert.Equal("StartPimax", received);
        Assert.Equal(new HelperReply(true, WasRunning: false, Value: "v"), reply);
    }

    [Fact]
    public async Task Client_that_gets_no_answer_says_so_after_the_reply_timeout()
    {
        var name = NewName();
        HelperReply? reply = null;

        await WithFakeHelper(name,
            async (server, token) => await Task.Delay(TimeSpan.FromSeconds(10), token),
            async () => reply = await new HelperClient(name) { ReplyTimeout = TimeSpan.FromMilliseconds(300) }
                .SendAsync(HelperCommand.StopPimax, CancellationToken.None));

        Assert.Equal(new HelperReply(false, "Mirador's helper service didn't answer in time."), reply);
    }

    [Fact]
    public async Task Cancelling_the_call_is_not_reported_as_a_timeout()
    {
        var name = NewName();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await WithFakeHelper(name,
            async (server, token) => await Task.Delay(TimeSpan.FromSeconds(10), token),
            async () => await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new HelperClient(name).SendAsync(HelperCommand.StopPimax, cts.Token)));
    }

    [Fact]
    public async Task Client_that_gets_an_unreadable_answer_says_so()
    {
        var name = NewName();
        HelperReply? reply = null;

        await WithFakeHelper(name,
            async (server, token) =>
            {
                await server.WriteAsync(Encoding.UTF8.GetBytes("this is not json\n"), token);
                await Task.Delay(200, token);
            },
            async () => reply = await new HelperClient(name).SendAsync(HelperCommand.Ping, CancellationToken.None));

        Assert.False(reply!.Ok);
        Assert.False(reply.NotDelivered); // it was delivered: the helper may have acted on it
        Assert.StartsWith("Mirador's helper service sent an answer Mirador couldn't read", reply.Error);
    }

    [Fact]
    public async Task Client_whose_helper_hangs_up_without_answering_says_so()
    {
        var name = NewName();
        HelperReply? reply = null;

        await WithFakeHelper(name,
            (server, _) =>
            {
                server.Dispose();
                return Task.CompletedTask;
            },
            async () => reply = await new HelperClient(name).SendAsync(HelperCommand.Ping, CancellationToken.None));

        Assert.False(reply!.Ok);
        Assert.False(reply.NotDelivered); // it was delivered: the helper may have acted on it
        Assert.StartsWith("Mirador's helper service sent an answer Mirador couldn't read", reply.Error);
    }
}
