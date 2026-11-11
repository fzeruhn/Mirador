namespace Mirador.Core.Tests;

public class TestThreadPoolTests
{
    [Fact]
    public void The_test_process_starts_with_enough_pool_threads()
    {
        ThreadPool.GetMinThreads(out var workers, out _);
        Assert.True(workers >= TestThreadPool.MinWorkerThreads, $"{workers} worker threads");
    }
}
