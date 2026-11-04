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
/// <para>
/// The pipe's name is never without an instance of ours while the service runs: only the first instance is created
/// with <see cref="PipeOptions.FirstPipeInstance"/>, so a program that found the name free could take it over. Every
/// replacement listener is therefore created while the instance it replaces still exists, and at most three callers are
/// served at once, so that a fourth instance is always available for the next listener.
/// </para>
/// <para>
/// Every connection is disconnected before it is closed. Closing alone would keep the instance in being for as long as
/// the caller keeps its end open, so a caller that held four connections would leave no instance to listen with.
/// </para>
/// </summary>
public sealed class HelperPipeServer : BackgroundService
{
    /// <summary>The pipe's name; the full name is <c>\\.\pipe\mirador-helper</c>.</summary>
    public const string DefaultPipeName = "mirador-helper";

    private const int MaxInstances = 4;

    /// <summary>How often a replacement listener is tried before the helper gives up and stops.</summary>
    private const int MaxReplacementAttempts = 3;

    /// <summary><c>ERROR_PIPE_BUSY</c> as the HRESULT of an <see cref="IOException"/>: every instance the name may have exists.</summary>
    private const int PipeBusy = unchecked((int)0x800700E7);

    // Room for a whole request and a whole reply. With no buffer (0) a pipe hands data over only while the other side is
    // reading, so a caller that sent more than the helper read, or never reads its reply, would block both for good.
    private const int BufferBytes = 2 * (LineCodec.MaxLineBytes + 1);

    private readonly HelperHandler handler;
    private readonly ILogger<HelperPipeServer> logger;
    private readonly IHostApplicationLifetime lifetime;
    private readonly string pipeName;

    // One slot for each instance of the pipe that exists, whether it is listening or connected.
    private readonly SemaphoreSlim instances = new(MaxInstances);
    private readonly ConcurrentDictionary<Task, byte> serving = new();
    private NamedPipeServerStream? firstPipe;

    public HelperPipeServer(
        HelperHandler handler, ILogger<HelperPipeServer> logger, IHostApplicationLifetime lifetime,
        string pipeName = DefaultPipeName)
    {
        this.handler = handler;
        this.logger = logger;
        this.lifetime = lifetime;
        this.pipeName = pipeName;
    }

    /// <summary>How long a caller has to send its request, and the helper to send the reply.</summary>
    internal TimeSpan ReadLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The pause between tries when a replacement listener can't be created.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Makes the pipe instances (bool: whether it is the first); a test makes it fail.</summary>
    internal Func<bool, NamedPipeServerStream>? PipeFactory { get; init; }

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

    /// <summary>One instance of the pipe, with its permissions; the first one must be the first of its name.</summary>
    internal static NamedPipeServerStream CreatePipe(string pipeName, bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: BufferBytes,
            outBufferSize: BufferBytes,
            CreatePipeSecurity());

    /// <summary>
    /// Makes the first instance of the pipe here, as a first instance: if a program already holds the name, the
    /// service fails to start instead of listening on a pipe nobody will connect to (or serving behind an imposter).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        instances.Wait(CancellationToken.None);
        firstPipe = NewPipe(firstInstance: true);
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        firstPipe?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream? listening = firstPipe ?? throw new InvalidOperationException("The pipe is opened by StartAsync.");
        firstPipe = null;
        logger.LogInformation("The helper is listening on {Pipe}.", pipeName);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var accepted = true;
                try
                {
                    await listening.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    // The caller went away between connecting and being accepted; this instance is no use any more.
                    logger.LogDebug(e, "A caller disconnected before it was accepted.");
                    accepted = false;
                }

                // The next listener comes first, while `listening` (connected, or dead) still holds the name. When all
                // four instances are in use this waits for a served caller to finish, and the accepted one waits with it.
                var next = await NextListenerAsync(stoppingToken).ConfigureAwait(false);
                if (next is null)
                {
                    break;
                }

                var current = listening;
                listening = next;
                if (accepted)
                {
                    var task = ServeAsync(current, stoppingToken);
                    serving.TryAdd(task, 0);
                    _ = task.ContinueWith(t => serving.TryRemove(t, out _), TaskScheduler.Default);
                }
                else
                {
                    current.Dispose();
                    instances.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            // It may hold a caller that was accepted but never served (stopping, or no replacement listener).
            if (listening is not null)
            {
                Disconnect(listening);
                listening.Dispose();
            }

            await Task.WhenAll(serving.Keys).ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream NewPipe(bool firstInstance) =>
        PipeFactory is { } factory ? factory(firstInstance) : CreatePipe(pipeName, firstInstance);

    /// <summary>
    /// Takes a free instance slot (waiting for one if all are in use) and makes a listening instance in it. When Windows
    /// keeps refusing, logs an error and asks the host to stop, rather than carry on without a listener; returns null then.
    /// </summary>
    private async Task<NamedPipeServerStream?> NextListenerAsync(CancellationToken ct)
    {
        await instances.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var busyReported = false;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return NewPipe(firstInstance: false);
                }
                catch (IOException e) when (e.HResult == PipeBusy)
                {
                    // Every instance exists, so nobody else can take the name meanwhile; one frees up when its caller
                    // has been served or dropped. Not counted as a failure: a caller must not be able to stop the helper.
                    if (!busyReported)
                    {
                        logger.LogWarning("Every instance of the helper's pipe is in use; waiting for one to be free.");
                        busyReported = true;
                    }

                    attempt--;
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt == MaxReplacementAttempts)
                    {
                        logger.LogError(e, "The helper couldn't open its pipe again after {Attempts} tries; stopping the helper service.", attempt);
                        lifetime.StopApplication();
                        instances.Release();
                        return null;
                    }

                    logger.LogWarning(e, "The helper couldn't open its pipe again; trying again.");
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            instances.Release();
            throw;
        }
    }

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
                // Connecting and leaving is not worth a warning, which any signed-in user could otherwise pile up in the event log.
                logger.LogDebug("A caller connected and left without sending a request.");
                return;
            }

            var reply = await handler.HandleAsync(request).ConfigureAwait(false);
            Log(request.Command, reply);

            using var writeLimit = CancellationTokenSource.CreateLinkedTokenSource(stop);
            writeLimit.CancelAfter(ReadLimit);
            await LineCodec.WriteAsync(pipe, reply, writeLimit.Token).ConfigureAwait(false);
            await WaitForHangUpAsync(pipe, stop).ConfigureAwait(false);
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
            Disconnect(pipe);
            pipe.Dispose();
            instances.Release();
        }
    }

    /// <summary>
    /// Waits, at most <see cref="ReadLimit"/>, for the caller to close its end once it has the reply: disconnecting
    /// first would throw away a reply it hasn't read yet. Whatever it sends meanwhile is read and dropped.
    /// </summary>
    private async Task WaitForHangUpAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(ReadLimit);
        var buffer = new byte[256];
        try
        {
            while (await pipe.ReadAsync(buffer, limit.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // It kept its end open; it is disconnected now.
        }
        catch (IOException)
        {
            // It has gone.
        }
    }

    /// <summary>Ends a connection on the helper's side, so that the instance goes when the helper closes it, whatever the caller does.</summary>
    private static void Disconnect(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Disconnect();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Not connected, or the caller has already gone: nothing keeps the instance.
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
