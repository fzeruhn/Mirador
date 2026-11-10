using System.Runtime.CompilerServices;

namespace Mirador.Pimax.Tests;

/// <summary>
/// The tests share one process and run in parallel, and many of them block a pool thread on purpose for a moment: a
/// real pipe connecting, a real process ending, a fake held until the test lets it go. With the pool's minimum (one
/// thread per core: 4 on CI's runner) those blocked threads leave work queued by other tests waiting while the pool
/// adds a thread about once a second, so a burst of unrelated tests ran past their 10 s limits together on CI. The
/// pool starts with enough threads instead; nothing a test checks depends on their number.
/// </summary>
internal static class TestThreadPool
{
    public const int MinWorkerThreads = 64;

#pragma warning disable CA2255 // A test assembly sets up its own process: that is what a module initializer is for here.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, MinWorkerThreads), io);
    }
}
