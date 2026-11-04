using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Mirador.Ipc;

/// <summary>
/// Listens on the app pipe (<see cref="AppPipe"/>) and answers each request with what <c>handle</c> returns. Only the
/// signed-in user (elevated or not) can connect, and the pipe is theirs (its owner), so a client can tell it from one
/// another user made. Callers are served side by side, at most <see cref="MaxServed"/> at once, one request each: a
/// line longer than <see cref="AppPipe.MaxLineBytes"/>, or one that doesn't arrive within <see cref="ReadLimit"/>,
/// closes the connection, and a caller that doesn't hang up after its reply is disconnected after <see cref="ReadLimit"/>.
/// The first instance is created as the first of its name, and each next listener is created while the instance it
/// replaces still holds the name, so no other program can take it while Mirador runs.
/// </summary>
/// <param name="name">The pipe's name, as <see cref="AppPipe.CurrentName"/> gives it.</param>
/// <param name="handle">Answers one request line (without its newline) with one reply line; it may take as long as a Start.</param>
public sealed class AppPipeServer(string name, Func<string, CancellationToken, Task<string>> handle) : IAsyncDisposable
{
    /// <summary>Callers served at once; the rest wait to be accepted. Far below Windows' 255 instances of a name.</summary>
    public const int MaxServed = 16;

    // Room for a whole request and a whole reply, so neither side blocks on the other's reading.
    private const int BufferBytes = 2 * (AppPipe.MaxLineBytes + 1);

    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(MaxServed);
    private readonly List<Task> serving = [];
    private NamedPipeServerStream? first;
    private Task? loop;

    /// <summary>How long a caller has to send its request, and to hang up once it has the reply.</summary>
    internal TimeSpan ReadLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The pause before trying again when Windows won't make another instance of the pipe.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Raised, on a background thread, when the server stops listening because it couldn't make another instance.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Raised, on a background thread, when <c>handle</c> threw; the caller got <c>FAIL</c> and the message.</summary>
    public event Action<Exception>? HandlerFailed;

    /// <summary>Creates the pipe and starts listening.</summary>
    /// <exception cref="UnauthorizedAccessException">The name is taken: another Mirador, or another program, listens on it.</exception>
    /// <exception cref="IOException">Windows wouldn't create the pipe.</exception>
    public void Start()
    {
        first = CreatePipe(firstInstance: true);
        loop = Task.Run(() => ListenAsync(first, stopping.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        Task[] left;
        lock (serving)
        {
            left = [.. serving];
        }

        await Task.WhenAll(left).ConfigureAwait(false);
        stopping.Dispose();
    }

    /// <summary>The signed-in user, and only them, may connect, and owns the pipe; nobody over the network.</summary>
    internal static PipeSecurity CreatePipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return OperatingSystem.IsWindows()
            ? NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                options, BufferBytes, BufferBytes, CreatePipeSecurity())
            // Elsewhere only for the tests: .NET's pipes there have no permissions or first instances.
            : new NamedPipeServerStream(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, BufferBytes, BufferBytes);
    }

    /// <summary>Another listener, trying again a few times when Windows says every instance is busy.</summary>
    private async Task<NamedPipeServerStream> NextPipeAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return CreatePipe(firstInstance: false);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ListenAsync(NamedPipeServerStream firstListener, CancellationToken ct)
    {
        NamedPipeServerStream? listening = firstListener;
        try
        {
            while (true)
            {
                // A slot first: while every slot serves a caller, the next one waits on the listening instance.
                await slots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await listening!.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The caller went away before it was accepted; this instance is no use any more.
                    slots.Release();
                    var dead = listening;
                    listening = null;
                    try
                    {
                        listening = await NextPipeAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        await dead.DisposeAsync().ConfigureAwait(false);
                    }

                    continue;
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                // The next listener first, while the connected instance still holds the name.
                var connected = listening;
                listening = null;
                try
                {
                    listening = await NextPipeAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    Track(ServeAsync(connected, ct));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                Failed?.Invoke(e);
            }
            catch (Exception)
            {
                // A listener's mistake must not hide the failure or break the shutdown.
            }
        }
        finally
        {
            if (listening is not null)
            {
                await listening.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void Track(Task task)
    {
        lock (serving)
        {
            serving.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (serving)
                {
                    serving.Remove(t);
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            string? request;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(ReadLimit);
                request = await ReadLineAsync(pipe, read.Token).ConfigureAwait(false);
            }

            if (request is null)
            {
                return;
            }

            var reply = AppPipe.OneLine(await AnswerAsync(request, ct).ConfigureAwait(false));
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(reply + "\n"), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
            await WaitForHangUpAsync(pipe, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The caller went away, sent too slowly, or Mirador is closing: nothing to answer.
        }
        finally
        {
            try
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            slots.Release();
        }
    }

    /// <summary>The handler's reply; a handler that throws is answered with <c>FAIL</c> and its message, and reported.</summary>
    private async Task<string> AnswerAsync(string request, CancellationToken ct)
    {
        try
        {
            return await handle(request, ct).ConfigureAwait(false) ?? AppPipe.FailPrefix + "Mirador had no answer.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            try
            {
                HandlerFailed?.Invoke(e);
            }
            catch (Exception)
            {
            }

            return AppPipe.FailPrefix + "Mirador failed: " + e.Message;
        }
    }

    /// <summary>
    /// Waits, at most <see cref="ReadLimit"/>, for the caller to close its end once it has the reply: disconnecting
    /// first would throw away a reply it hasn't read yet. Whatever it sends meanwhile is read and dropped.
    /// </summary>
    private async Task WaitForHangUpAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ReadLimit);
        var buffer = new byte[64];
        try
        {
            while (await pipe.ReadAsync(buffer, limit.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // It kept its end open; it is disconnected now.
        }
    }

    /// <summary>One line without its newline (a <c>\r</c> before it is dropped); null when it is longer than allowed or doesn't end.</summary>
    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new byte[AppPipe.MaxLineBytes];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                return null;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (count == AppPipe.MaxLineBytes)
            {
                return null;
            }

            line[count++] = one[0];
        }

        if (count > 0 && line[count - 1] == (byte)'\r')
        {
            count--;
        }

        return Encoding.UTF8.GetString(line, 0, count);
    }
}
