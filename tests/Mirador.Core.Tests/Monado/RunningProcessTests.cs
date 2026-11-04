using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

/// <summary>
/// The failures of stopping a process that Windows rarely produces on demand (a tree that can't be ended in full, a
/// process object that is gone while a kill is under way). The kill is replaced, so these happen when the test says.
/// </summary>
public class RunningProcessTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static Process StartPing() =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        })!;

    private static RunningProcess Wrap(Process started, Action<Process> terminate) =>
        new(started, new SafeFileHandle(IntPtr.Zero, ownsHandle: false), terminate);

    /// <summary>Ends the process for real, then fails the way Process.Kill does when part of the tree stays alive.</summary>
    private static void KillThenFailForTheRestOfTheTree(Process p)
    {
        p.Kill();
        p.WaitForExit();
        throw new AggregateException(new Win32Exception(5));
    }

    [Fact]
    public async Task Kill_is_a_no_op_when_the_process_is_gone_even_if_part_of_its_tree_could_not_be_ended()
    {
        var process = Wrap(StartPing(), KillThenFailForTheRestOfTheTree);

        process.Kill();

        await process.Exited.WaitAsync(Limit);
        await process.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_does_not_throw_when_ending_the_tree_fails_and_still_releases_the_process()
    {
        var calls = 0;
        var process = Wrap(StartPing(), p =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new AggregateException(new Win32Exception(5)); // still running and not ended: a real failure
            }

            KillThenFailForTheRestOfTheTree(p);
        });

        // Kill() reports a process that is still running.
        Assert.Throws<AggregateException>(() => process.Kill());

        await process.DisposeAsync(); // the second attempt ends it and fails for the tree; dispose swallows that

        Assert.True(process.Exited.IsCompleted);
    }

    [Fact]
    public async Task Dispose_waits_for_a_kill_that_is_under_way_instead_of_pulling_the_process_out_from_under_it()
    {
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var calls = 0;
        var process = Wrap(StartPing(), p =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(Limit));
            }

            p.Kill(entireProcessTree: true);
        });
        var killing = Task.Run(process.Kill);
        Assert.True(entered.Wait(Limit));

        var disposing = Task.Run(async () => await process.DisposeAsync());
        await Task.Delay(500);
        Assert.False(disposing.IsCompleted); // the process object is still in use by the kill

        release.Set();

        await killing.WaitAsync(Limit);
        await disposing.WaitAsync(Limit);
    }

    [Fact]
    public async Task Kill_after_dispose_does_nothing()
    {
        var process = Wrap(StartPing(), p => p.Kill(entireProcessTree: true));
        await process.DisposeAsync();

        process.Kill();
    }
}
