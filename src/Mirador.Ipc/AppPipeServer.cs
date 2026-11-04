using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Mirador.Ipc;

/// <summary>
/// Listens on the app pipe (<see cref="AppPipe"/>) and answers each request with what <c>handle</c> returns. Only the
/// signed-in user (elevated or not) can connect. Many callers are served at once, one request each; a line longer than
/// <see cref="AppPipe.MaxLineBytes"/>, or one that doesn't arrive within <see cref="ReadLimit"/>, closes the connection.
/// The first instance of the pipe is created as the first of its name, and each next listener is created before the
/// instance it replaces is handed over, so no other program can take the name while Mirador runs.
/// </summary>
/// <param name="name">The pipe's name, as <see cref="AppPipe.CurrentName"/> gives it.</param>
/// <param name="handle">Answers one request line (without its newline) with one reply line; it may take as long as a Start.</param>
public sealed class AppPipeServer(string name, Func<string, CancellationToken, Task<string>> handle) : IAsyncDisposable
{
    // Room for a whole request and a whole reply, so neither side blocks on the other's reading.
    private const int BufferBytes = 2 * (AppPipe.MaxLineBytes + 1);

    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Task, byte> serving = new();
    private NamedPipeServerStream? first;
    private Task? loop;

    /// <summary>How long a caller has to send its request.</summary>
    internal TimeSpan ReadLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised, on a background thread, when the server stops listening because it couldn't make another instance.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Creates the pipe and starts listening.</summary>
    /// <exception cref="IOException">The name is taken: another program (or another Mirador) listens on it.</exception>
    /// <exception cref="UnauthorizedAccessException">The name is taken by a program of another user.</exception>
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

        await Task.WhenAll(serving.Keys).ConfigureAwait(false);
        stopping.Dispose();
    }

    /// <summary>The signed-in user, and only them, may connect; nobody over the network.</summary>
    internal static PipeSecurity CreatePipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return OperatingSystem.IsWindows()
            ? NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                options, BufferBytes, BufferBytes, CreatePipeSecurity())
            // Elsewhere only for the tests: .NET's pipes there have no permissions to set.
            : new NamedPipeServerStream(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, BufferBytes, BufferBytes);
    }

    private async Task ListenAsync(NamedPipeServerStream listening, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                try
                {
                    await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The caller went away before it was accepted; this instance is no use any more.
                    var dead = listening;
                    listening = CreatePipe(firstInstance: false);
                    await dead.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                // The next listener first, while the connected instance still holds the name.
                var connected = listening;
                listening = CreatePipe(firstInstance: false);
                Track(ServeAsync(connected, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke(e);
        }
        finally
        {
            await listening.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Track(Task task)
    {
        serving.TryAdd(task, 0);
        _ = task.ContinueWith(t => serving.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

            var reply = AppPipe.OneLine(await handle(request, ct).ConfigureAwait(false));
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(reply + "\n"), ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
            {
                pipe.WaitForPipeDrain();
            }
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
