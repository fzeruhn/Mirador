using Mirador.Core.Build;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Build;

public class StandInWaitTests
{
    private static readonly MonadoInstall Install = new(@"C:\m\openxr_monado.json", @"C:\m\bin\openxr_monado.dll", @"C:\m\bin\monado-service.exe", @"C:\m\bin\service", false);

    private sealed class Service : IAsyncDisposable
    {
        public int Stopped { get; private set; }

        public ValueTask DisposeAsync()
        {
            Stopped++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Two_asks_at_once_start_monado_once()
    {
        var starts = 0;
        var release = new TaskCompletionSource();
        var service = new Service();
        await using var wait = new StandInWait(Install, async _ =>
        {
            starts++;
            await release.Task;
            return service;
        });

        var first = wait.StartAsync(CancellationToken.None);
        var second = wait.StartAsync(CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, starts);
        Assert.True(wait.WasAsked);
    }

    [Fact]
    public async Task Ending_while_monado_starts_waits_for_it_and_stops_it()
    {
        var release = new TaskCompletionSource();
        var service = new Service();
        var wait = new StandInWait(Install, async _ =>
        {
            await release.Task;
            return service;
        });

        var asking = wait.StartAsync(CancellationToken.None);
        var ending = wait.DisposeAsync().AsTask();
        Assert.False(ending.IsCompleted);
        release.SetResult();
        await asking;
        await ending;

        Assert.Equal(1, service.Stopped);
    }

    [Fact]
    public async Task An_ask_after_the_end_is_refused_and_starts_nothing()
    {
        var starts = 0;
        var wait = new StandInWait(Install, _ =>
        {
            starts++;
            return Task.FromResult<IAsyncDisposable>(new Service());
        });
        await wait.DisposeAsync();

        await Assert.ThrowsAsync<MiradorException>(() => wait.StartAsync(CancellationToken.None));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task A_failed_start_is_kept_for_the_step()
    {
        await using var wait = new StandInWait(Install, _ => throw new MiradorException("Monado didn't come up within 45 s."));

        await Assert.ThrowsAsync<MiradorException>(() => wait.StartAsync(CancellationToken.None));

        Assert.True(wait.WasAsked);
        Assert.Equal("Monado didn't come up within 45 s.", wait.Failure);
    }
}
