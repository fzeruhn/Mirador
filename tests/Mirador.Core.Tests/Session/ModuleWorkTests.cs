using Mirador.Core.Session;

namespace Mirador.Core.Tests.Session;

/// <summary><see cref="SessionController.RunWhileStoppedAsync"/>: a module's own work while Monado is stopped (the display check).</summary>
public class ModuleWorkTests
{
    private static readonly TimeSpan Limit = Harness.Limit;

    private static IReadOnlyDictionary<string, string> Data(string key, string value) => new Dictionary<string, string> { [key] = value };

    [Fact]
    public async Task A_start_waits_for_the_work_and_its_saves_reach_the_session_file()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = h.Controller.RunWhileStoppedAsync("fake", async (save, ct) =>
        {
            save(Data("pimaxWasRunning", "true"));
            saved.SetResult();
            await release.Task;
            save(new Dictionary<string, string>());
            return 42;
        }, CancellationToken.None);
        await saved.Task.WaitAsync(Limit);

        h.Pipe.Up = true;
        var start = h.Controller.StartAsync(StartReason.Game, CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(0, h.Launcher.Starts);
        var left = new SessionFile(h.Paths.SessionFile).Read()!.Value;
        Assert.Equal(("p1", "fake"), (left.ProfileId, left.ModuleId));
        Assert.Equal(Data("pimaxWasRunning", "true"), left.Data);

        release.SetResult();

        Assert.Equal(42, await work.WaitAsync(Limit));
        Assert.True((await start.WaitAsync(Limit)).Ok);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task Work_that_saves_nothing_left_deletes_the_session_file()
    {
        await using var h = new Harness();

        await h.Controller.RunWhileStoppedAsync("fake", (save, ct) =>
        {
            save(Data("a", "1"));
            save(new Dictionary<string, string>());
            return Task.FromResult(0);
        }, CancellationToken.None).WaitAsync(Limit);

        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task Work_whose_undo_failed_leaves_its_data_for_recovery()
    {
        await using var h = new Harness();

        await Assert.ThrowsAsync<MiradorException>(() => h.Controller.RunWhileStoppedAsync<int>("fake", (save, ct) =>
        {
            save(Data("pimaxWasRunning", "true"));
            throw new MiradorException("Pimax's service is disabled.");
        }, CancellationToken.None).WaitAsync(Limit));

        Assert.Equal(Data("pimaxWasRunning", "true"), new SessionFile(h.Paths.SessionFile).Read()!.Value.Data);
    }

    [Fact]
    public async Task Work_is_refused_while_monado_runs()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();

        var e = await Assert.ThrowsAsync<MiradorException>(() =>
            h.Controller.RunWhileStoppedAsync("fake", (save, ct) => Task.FromResult(0), CancellationToken.None).WaitAsync(Limit));

        Assert.Equal("Stop Monado first.", e.Message);
    }

    [Fact]
    public async Task Works_run_one_after_another()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var first = h.Controller.RunWhileStoppedAsync("fake", async (save, ct) =>
        {
            lock (order) { order.Add("first in"); }
            await release.Task;
            lock (order) { order.Add("first out"); }
            return 1;
        }, CancellationToken.None);
        var second = h.Controller.RunWhileStoppedAsync("fake", (save, ct) =>
        {
            lock (order) { order.Add("second"); }
            return Task.FromResult(2);
        }, CancellationToken.None);
        await Task.Delay(100);
        release.SetResult();

        await Task.WhenAll(first, second).WaitAsync(Limit);

        Assert.Equal(["first in", "first out", "second"], order);
        Assert.True(h.Controller.WhenSettledAsync().IsCompleted);
    }

    [Fact]
    public async Task A_session_left_behind_is_undone_before_the_work()
    {
        await using var h = new Harness();
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake", Data("wasRunning", "true"));

        await h.Controller.RunWhileStoppedAsync("fake", (save, ct) => Task.FromResult(0), CancellationToken.None).WaitAsync(Limit);

        Assert.Contains("recover", h.Steps.All);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }
}
