namespace Mirador.Core.Monado;

/// <summary>A process Mirador started.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    /// <summary>The operating system's process id.</summary>
    int Id { get; }

    /// <summary>
    /// Completes with the exit code once the process has ended and its output has been read to the end (or, if some
    /// other process still holds the output open, shortly after it ended).
    /// </summary>
    Task<int> Exited { get; }

    /// <summary>
    /// Raised on a background thread for each complete line the process writes, standard output and standard error
    /// alike, without a line break. Calls never overlap. Lines written before the first handler was added are
    /// delivered to that handler as it is added. Each handler gets every line: one that throws does not keep the
    /// handlers after it from the line or stop the reading; its exception goes to <see cref="ListenerFailed"/>.
    /// </summary>
    event Action<string>? Line;

    /// <summary>
    /// Raised with the exception, once per failure, when a <see cref="Line"/> handler throws (a log that can't be
    /// written, say), so that it doesn't go unnoticed. Raised on the thread that delivered the line, so calls from the
    /// two output streams may overlap; a handler that throws here is ignored. Add it before <see cref="Line"/> to
    /// hear about failures in the lines replayed to the first <see cref="Line"/> handler.
    /// </summary>
    event Action<Exception>? ListenerFailed;

    /// <summary>
    /// Ends the process and anything it started. <c>monado-service</c> ignores Ctrl+C on Windows. A process Mirador runs
    /// as the user stops at once, without its own teardown (<see cref="EndCleanlyAsync"/> first lets it do that); an
    /// elevated one is stopped by the helper, which asks it to end by itself first. Does nothing when the process has
    /// already ended.
    /// </summary>
    void Kill();

    /// <summary>
    /// Asks the process to end by itself and waits for it at most <paramref name="limit"/>: for <c>monado-service</c>,
    /// a key in its console (<see cref="Mirador.Ipc.ConsoleExitKey"/>), so that it tears its devices down (the Lighthouse
    /// driver's standby on exit). True when it has ended; otherwise <see cref="Kill"/> ends it. Never throws.
    /// </summary>
    Task<bool> EndCleanlyAsync(TimeSpan limit) => Task.FromResult(false);
}

/// <summary>Starts programs for Mirador. The seam that lets the session be tested without real processes.</summary>
public interface IProcessLauncher
{
    /// <exception cref="MiradorException">The program could not be started; the message says why.</exception>
    IRunningProcess Start(ProcessSpec spec);
}
