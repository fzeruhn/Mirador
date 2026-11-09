using System.IO.Pipes;
using System.Security.Principal;

namespace Mirador.Ipc;

/// <summary>Sends one command to the helper service and returns its reply. Never throws for a missing or broken helper.</summary>
public interface IHelperClient
{
    /// <summary>
    /// The helper's reply. A helper that isn't there, doesn't answer or answers nonsense comes back as
    /// <c>Ok = false</c> with a message for the user. Only cancelling <paramref name="ct"/> throws.
    /// </summary>
    Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct);

    /// <summary>
    /// Sends a request that carries typed values (<see cref="HelperCommand.StartMonadoElevated"/>). The default drops the
    /// values and sends the command alone, which is right for every command that has none; the real client overrides it.
    /// </summary>
    Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct) => SendAsync(request.Command, ct);
}

/// <summary>Talks to the helper over its named pipe: one connection and one line each way per command.</summary>
public sealed class HelperClient(string pipeName = "mirador-helper") : IHelperClient
{
    internal const string NotRunning =
        "Mirador's helper service isn't running. Reinstall Mirador; for a dev build, run tools\\dev-install-helper.ps1 as administrator.";

    internal const string TimedOut = "Mirador's helper service didn't answer in time.";

    private const string Unreadable =
        "Mirador's helper service sent an answer Mirador couldn't read. Reinstall Mirador.";

    /// <summary>How long to wait for the pipe to accept a connection.</summary>
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the helper has to take the request and answer it. Stopping Pimax's service can take 20 s.</summary>
    internal TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long installing Mirador's Monado may take: a download of up to a few hundred MB.</summary>
    internal TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct) =>
        SendAsync(new HelperRequest(command), ct);

    public async Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct)
    {
        // Identification: a program that sits on the pipe name can see who connected but can't act as them.
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new HelperReply(false, NotRunning) { NotDelivered = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HelperReply(false, $"Mirador couldn't talk to its helper service ({e.Message.TrimEnd('.')}). Reinstall Mirador.") { NotDelivered = true };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Command == HelperCommand.InstallMonado ? InstallTimeout : ReplyTimeout);
        try
        {
            await LineCodec.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            return await LineCodec.ReadAsync<HelperReply>(pipe, timeout.Token).ConfigureAwait(false)
                ?? new HelperReply(false, Unreadable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HelperReply(false, TimedOut);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return new HelperReply(false, Unreadable);
        }
    }
}
