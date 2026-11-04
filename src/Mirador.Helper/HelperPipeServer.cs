using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mirador.Ipc;

namespace Mirador.Helper;

/// <summary>
/// Listens on the helper's named pipe and hands each request to the <see cref="HelperHandler"/>. Any signed-in user's
/// program can connect, so the server trusts nothing it reads: one request per connection, at most
/// <see cref="LineCodec.MaxLineBytes"/> bytes of it and <see cref="ReadLimit"/> to send them; anything that isn't one of
/// the fixed commands is dropped by closing the connection. What the caller sent is never logged (only that it was
/// refused and why), and nothing from it reaches the registry, a service or a process.
/// </summary>
public sealed class HelperPipeServer : BackgroundService
{
    /// <summary>The pipe's name; the full name is <c>\\.\pipe\mirador-helper</c>.</summary>
    public const string DefaultPipeName = "mirador-helper";

    private const int MaxInstances = 4;

    // Room for a whole request and a whole reply. With no buffer (0) a pipe hands data over only while the other side is
    // reading, so a caller that sent more than the helper read, or never reads its reply, would block both for good.
    private const int BufferBytes = 2 * (LineCodec.MaxLineBytes + 1);

    private readonly HelperHandler handler;
    private readonly ILogger<HelperPipeServer> logger;
    private readonly string pipeName;
    private readonly SemaphoreSlim instances = new(MaxInstances);
    private readonly ConcurrentDictionary<Task, byte> serving = new();
    private NamedPipeServerStream? firstPipe;

    public HelperPipeServer(HelperHandler handler, ILogger<HelperPipeServer> logger, string pipeName = DefaultPipeName)
    {
        this.handler = handler;
        this.logger = logger;
        this.pipeName = pipeName;
    }

    /// <summary>How long a caller has to send its request, and the helper to send the reply.</summary>
    internal TimeSpan ReadLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Authenticated Users may read and write, which is all a request needs; SYSTEM and Administrators have full
    /// control. Nobody who logged on over the network gets in, and no one but them may create another instance
    /// of the pipe or change its permissions.
    /// </summary>
    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Makes the first instance of the pipe here, as a first instance: if a program already holds the name, the
    /// service fails to start instead of listening on a pipe nobody will connect to (or serving behind an imposter).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        instances.Wait(CancellationToken.None);
        firstPipe = CreatePipe(firstInstance: true);
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        firstPipe?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream? pipe = firstPipe ?? throw new InvalidOperationException("The pipe is opened by StartAsync.");
        firstPipe = null;
        logger.LogInformation("The helper is listening on {Pipe}.", pipeName);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    // The caller went away between connecting and being accepted; listen again on a fresh instance.
                    logger.LogDebug(e, "A caller disconnected before it was accepted.");
                    pipe.Dispose();
                    pipe = null;
                    instances.Release();
                    pipe = await NextPipeAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var connected = pipe;
                pipe = null;
                var task = ServeAsync(connected, stoppingToken);
                serving.TryAdd(task, 0);
                _ = task.ContinueWith(t => serving.TryRemove(t, out _), TaskScheduler.Default);

                // The next listener is up before the caller is served, and while all instances are busy the
                // pipe still exists (busy), so callers wait instead of finding the name gone.
                pipe = await NextPipeAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            pipe?.Dispose();
            await Task.WhenAll(serving.Keys).ConfigureAwait(false);
        }
    }

    /// <summary>Takes a free instance slot and makes the next listening instance, trying again if Windows refuses.</summary>
    private async Task<NamedPipeServerStream> NextPipeAsync(CancellationToken ct)
    {
        while (true)
        {
            await instances.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return CreatePipe(firstInstance: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                instances.Release();
                logger.LogError(e, "The helper couldn't open its pipe again; trying again in a second.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: BufferBytes,
            outBufferSize: BufferBytes,
            CreatePipeSecurity());

    /// <summary>Serves one connection, then closes it and frees its slot. Never throws: a bad caller must not stop the service.</summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        try
        {
            HelperRequest? request;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                limit.CancelAfter(ReadLimit);
                try
                {
                    request = await LineCodec.ReadAsync<HelperRequest>(pipe, limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    logger.LogWarning("Dropped a connection: no complete request within {Seconds} seconds.", ReadLimit.TotalSeconds);
                    return;
                }
                catch (InvalidDataException e)
                {
                    // The codec's own wording: it never repeats what was sent.
                    logger.LogWarning("Dropped a connection: {Reason}", e.Message);
                    return;
                }
                catch (IOException e)
                {
                    logger.LogDebug(e, "A caller disconnected while sending its request.");
                    return;
                }
            }

            if (request is null)
            {
                logger.LogWarning("Dropped a connection: it sent no request.");
                return;
            }

            var reply = await handler.HandleAsync(request).ConfigureAwait(false);
            Log(request.Command, reply);

            using var writeLimit = CancellationTokenSource.CreateLinkedTokenSource(stop);
            writeLimit.CancelAfter(ReadLimit);
            await LineCodec.WriteAsync(pipe, reply, writeLimit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping, or the caller took too long to read the reply.
        }
        catch (IOException e)
        {
            logger.LogDebug(e, "A caller disconnected before it read the reply.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "The helper failed to serve a request.");
        }
        finally
        {
            pipe.Dispose();
            instances.Release();
        }
    }

    private void Log(HelperCommand command, HelperReply reply)
    {
        if (command == HelperCommand.Ping)
        {
            logger.LogDebug("Ping answered.");
        }
        else if (reply.Ok)
        {
            logger.LogInformation("{Command} done.", command);
        }
        else
        {
            logger.LogWarning("{Command} failed: {Error}", command, reply.Error);
        }
    }
}
