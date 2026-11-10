using Microsoft.Extensions.Time.Testing;

namespace Mirador.Pimax.Tests;

/// <summary>
/// The fake clock, telling the test when a timer has been set, so that it moves the clock only once the code under
/// test waits on it (a timer set after the clock moved would be due later than the test means).
/// </summary>
internal sealed class TimerWatch(FakeTimeProvider clock) : TimeProvider
{
    private readonly List<TimeSpan> created = [];
    private readonly List<(TimeSpan Due, int Count, TaskCompletionSource Done)> waiting = [];

    public FakeTimeProvider Clock => clock;

    public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

    public override long TimestampFrequency => clock.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override long GetTimestamp() => clock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = clock.CreateTimer(callback, state, dueTime, period);
        lock (created)
        {
            created.Add(dueTime);
            foreach (var w in waiting.Where(w => Count(w.Due) >= w.Count).ToList())
            {
                waiting.Remove(w);
                w.Done.TrySetResult();
            }
        }

        return timer;
    }

    /// <summary>Completes once <paramref name="count"/> timers due after <paramref name="dueTime"/> have been set.</summary>
    public Task Set(TimeSpan dueTime, int count = 1)
    {
        lock (created)
        {
            if (Count(dueTime) >= count)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Add((dueTime, count, done));
            return done.Task;
        }
    }

    private int Count(TimeSpan dueTime) => created.Count(d => d == dueTime);
}
