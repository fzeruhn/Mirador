using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Mirador.Ipc;

/// <summary>Sends one line to a running Mirador over the app pipe and reads its one-line reply.</summary>
public static class AppPipeClient
{
    /// <summary>
    /// Connects to <paramref name="name"/>, sends <paramref name="line"/> and returns the reply line. Null when no Mirador
    /// listens, the reply doesn't come within <paramref name="timeout"/> (connecting included), or the connection breaks.
    /// Only cancelling <paramref name="ct"/> throws.
    /// </summary>
    public static async Task<string?> SendAsync(string name, string line, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
            if (!OwnedByThisUser(pipe))
            {
                return null; // another user made a pipe of that name: not this user's Mirador
            }

            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), limit.Token).ConfigureAwait(false);
            await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
            return await AppPipeServer.ReadLineAsync(pipe, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the pipe's owner is the user this process runs as (also when elevated); always true off Windows.</summary>
    private static bool OwnedByThisUser(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == identity.User;
    }
}
