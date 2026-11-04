using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public sealed class HelperPipeServerTests : IAsyncLifetime
{
    private readonly string pipeName = "mirador-test-" + Guid.NewGuid().ToString("N");
    private readonly TempDir temp = new();
    private readonly FakeServiceControl services = new() { Running = true };
    private readonly FakeProcessControl processes = new();
    private readonly FakeRuntimeRegistry registry = new();
    private readonly ListLogger<HelperPipeServer> logger = new();
    private HelperPipeServer server = null!;

    public async Task InitializeAsync()
    {
        server = NewServer(pipeName);
        await server.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await server.StopAsync(CancellationToken.None);
        server.Dispose();
        temp.Dispose();
    }

    private HelperHandler NewHandler()
    {
        var manifest = temp.File(@"monado\runtime\openxr_monado.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "{}");
        return new HelperHandler(services, processes, registry, manifest, "9.9.9");
    }

    private HelperPipeServer NewServer(string name, TimeSpan? readLimit = null) =>
        new(NewHandler(), logger, name) { ReadLimit = readLimit ?? TimeSpan.FromSeconds(5) };

    /// <summary>Connects with a bare pipe, writes <paramref name="request"/> as given and reads until the helper closes.</summary>
    private async Task<string> RawAsync(byte[] request)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(2000, timeout.Token);
        try
        {
            await client.WriteAsync(request, timeout.Token);
            await client.FlushAsync(timeout.Token);
        }
        catch (IOException)
        {
            // The helper may close on a bad request before it has read all of it; what matters is what comes back.
        }

        using var answer = new MemoryStream();
        try
        {
            await client.CopyToAsync(answer, timeout.Token);
        }
        catch (IOException)
        {
        }

        return Encoding.UTF8.GetString(answer.ToArray());
    }

    private Task<string> RawAsync(string request) => RawAsync(Encoding.UTF8.GetBytes(request));

    private void AssertNothingHappened()
    {
        Assert.Empty(services.Stopped);
        Assert.Empty(services.Started);
        Assert.Empty(processes.Killed);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task Ping_over_the_pipe_returns_the_version()
    {
        var reply = await new HelperClient(pipeName).SendAsync(HelperCommand.Ping, CancellationToken.None);

        Assert.Equal(new HelperReply(true, Value: "9.9.9"), reply);
    }

    [Fact]
    public async Task A_command_over_the_pipe_reaches_the_handler()
    {
        var reply = await new HelperClient(pipeName).SendAsync(HelperCommand.StopPimax, CancellationToken.None);

        Assert.Equal(new HelperReply(true, WasRunning: true), reply);
        Assert.Equal(["PiServiceLauncher"], services.Stopped);
        Assert.Equal(PimaxNames.Processes, processes.Killed);
    }

    [Fact]
    public async Task A_failing_command_comes_back_as_an_error_reply()
    {
        services.StopFails = new InvalidOperationException("Pimax's service won't stop.");

        var reply = await new HelperClient(pipeName).SendAsync(HelperCommand.StopPimax, CancellationToken.None);

        Assert.Equal(new HelperReply(false, "Pimax's service won't stop."), reply);
    }

    [Fact]
    public async Task The_raw_reply_is_one_json_line()
    {
        var text = await RawAsync("{\"command\":\"Ping\"}\n");

        Assert.Equal("{\"ok\":true,\"value\":\"9.9.9\"}\n", text);
    }

    [Fact]
    public async Task More_clients_at_once_than_pipe_instances_are_all_served()
    {
        // A pipe client blocks a pool thread while it connects. Here the clients and the server share one process, so
        // give the pool the threads they would each have in a process of their own.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), io);

        var replies = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => new HelperClient(pipeName).SendAsync(HelperCommand.Ping, CancellationToken.None)));

        Assert.All(replies, r => Assert.Equal(new HelperReply(true, Value: "9.9.9"), r));
    }

    [Fact]
    public async Task The_helper_keeps_serving_one_connection_after_another()
    {
        for (var i = 0; i < 10; i++)
        {
            var reply = await new HelperClient(pipeName).SendAsync(HelperCommand.Ping, CancellationToken.None);
            Assert.True(reply.Ok);
        }
    }

    [Fact]
    public async Task Only_the_first_request_of_a_connection_is_handled()
    {
        var text = await RawAsync("{\"command\":\"Ping\"}\n{\"command\":\"StopPimax\"}\n");

        Assert.Equal("{\"ok\":true,\"value\":\"9.9.9\"}\n", text);
        AssertNothingHappened();
    }

    [Fact]
    public async Task A_line_over_the_limit_is_dropped_without_a_reply_and_without_effect()
    {
        var text = await RawAsync(new string('a', LineCodec.MaxLineBytes + 100) + "\n");

        Assert.Equal("", text);
        AssertNothingHappened();
        Assert.True((await new HelperClient(pipeName).SendAsync(HelperCommand.Ping, CancellationToken.None)).Ok);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"command\":\"Reboot\"}")]
    [InlineData("{\"command\":1}")]
    [InlineData("{}")]
    [InlineData("[\"StopPimax\"]")]
    [InlineData("{\"command\":\"Ping\"")]
    public async Task A_request_that_is_not_one_of_the_commands_is_dropped_without_a_reply_and_without_effect(string line)
    {
        var text = await RawAsync(line + "\n");

        Assert.Equal("", text);
        AssertNothingHappened();
        Assert.True((await new HelperClient(pipeName).SendAsync(HelperCommand.Ping, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task A_caller_that_hangs_up_before_the_end_of_its_request_gets_nothing_done()
    {
        await using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(2000);
            await client.WriteAsync("{\"command\":\"StopPimax\"}"u8.ToArray());
        }

        await Task.Delay(300);

        AssertNothingHappened();
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("ended before its newline", entry.Message);
    }

    [Fact]
    public async Task A_client_that_connects_and_says_nothing_is_dropped_after_the_read_limit()
    {
        var name = pipeName + "-quick";
        using var quick = NewServer(name, TimeSpan.FromMilliseconds(300));
        await quick.StartAsync(CancellationToken.None);
        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);
            await client.WriteAsync("{\"command\":\"Sto"u8.ToArray());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var read = await client.ReadAsync(new byte[16], timeout.Token);

            Assert.Equal(0, read);
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
            AssertNothingHappened();
        }
        finally
        {
            await quick.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task What_a_caller_sends_is_never_written_to_the_log()
    {
        await RawAsync("SECRET-PAYLOAD-12345 C:\\evil.json\n");
        await RawAsync("{\"command\":\"Reboot\",\"path\":\"SECRET-PATH-67890\"}\n");
        await RawAsync(new string('Z', LineCodec.MaxLineBytes + 10) + "\n");
        await Task.Delay(200);

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("SECRET", e.Message);
            Assert.DoesNotContain("evil", e.Message);
            Assert.DoesNotContain("ZZZZ", e.Message);
        });
    }

    [Fact]
    public async Task Dropped_connections_are_logged_as_warnings_with_the_reason()
    {
        await RawAsync(new string('a', LineCodec.MaxLineBytes + 1) + "\n");
        await Task.Delay(200);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("longer than 4096 bytes", entry.Message);
    }

    [Fact]
    public async Task A_second_server_on_the_same_name_does_not_start()
    {
        using var second = NewServer(pipeName);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => second.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_program_that_already_holds_the_pipe_name_keeps_the_server_from_starting()
    {
        var name = pipeName + "-squatted";
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var service = NewServer(name);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stopping_the_server_closes_the_pipe()
    {
        var name = pipeName + "-stopping";
        using var service = NewServer(name);
        await service.StartAsync(CancellationToken.None);
        Assert.True((await new HelperClient(name).SendAsync(HelperCommand.Ping, CancellationToken.None)).Ok);

        await service.StopAsync(CancellationToken.None);

        var reply = await new HelperClient(name) { ConnectTimeout = TimeSpan.FromMilliseconds(300) }.SendAsync(HelperCommand.Ping, CancellationToken.None);
        Assert.False(reply.Ok);
        Assert.StartsWith("Mirador's helper service isn't running", reply.Error);
    }

    // The pipe's permissions

    private static (SecurityIdentifier Who, PipeAccessRights Rights, AccessControlType Type)[] Rules()
    {
        var security = HelperPipeServer.CreatePipeSecurity();
        return security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference, r.PipeAccessRights, r.AccessControlType))
            .ToArray();
    }

    [Fact]
    public void Authenticated_users_may_read_and_write_and_nothing_more()
    {
        var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        var rule = Assert.Single(Rules(), r => r.Who == users && r.Type == AccessControlType.Allow);

        Assert.True(rule.Rights.HasFlag(PipeAccessRights.ReadData));
        Assert.True(rule.Rights.HasFlag(PipeAccessRights.WriteData));
        Assert.False(rule.Rights.HasFlag(PipeAccessRights.CreateNewInstance), "creating an instance would let a user take over the pipe");
        Assert.False(rule.Rights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(rule.Rights.HasFlag(PipeAccessRights.TakeOwnership));
        Assert.False(rule.Rights.HasFlag(PipeAccessRights.Delete));
    }

    [Theory]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    public void System_and_administrators_have_full_control(WellKnownSidType sid)
    {
        var who = new SecurityIdentifier(sid, null);

        var rule = Assert.Single(Rules(), r => r.Who == who && r.Type == AccessControlType.Allow);

        Assert.Equal(PipeAccessRights.FullControl, rule.Rights);
    }

    [Fact]
    public void Nobody_else_is_let_in_and_network_logons_are_turned_away()
    {
        var allowed = new[]
        {
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

        var rules = Rules();

        Assert.All(rules.Where(r => r.Type == AccessControlType.Allow), r => Assert.Contains(r.Who, allowed));
        Assert.Contains(rules, r => r.Who == network && r.Type == AccessControlType.Deny);
    }
}
