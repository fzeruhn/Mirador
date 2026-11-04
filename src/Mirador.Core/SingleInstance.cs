namespace Mirador.Core;

/// <summary>
/// Makes sure one Mirador runs per user and Windows session: the first holds a named mutex for as long as it runs; a
/// second finds it taken and hands its request to the first over the app pipe instead.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;

    private SingleInstance(Mutex mutex) => this.mutex = mutex;

    /// <summary>
    /// Takes the mutex when no other Mirador of this session holds it; null when one does. Mirador ending, however it
    /// ends, frees it.
    /// </summary>
    /// <exception cref="MiradorException">Another user's program, or something that isn't a mutex, holds the name.</exception>
    public static SingleInstance? TryAcquire(string mutexName = @"Local\Mirador")
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out createdNew);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            throw new MiradorException(
                "Another program in this Windows session holds Mirador's name, so Mirador can't tell whether it already runs. Sign out and back in, or restart Windows.");
        }

        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Lets the mutex go (it is never waited on, so it needs no release from the thread that made it).</summary>
    public void Dispose() => mutex.Dispose();
}
