using System.Diagnostics;

namespace Mirador.Core.Tests;

public class ProcessesTests
{
    [Fact]
    public void The_snapshot_lists_the_running_processes_by_name_without_exe()
    {
        using var self = Process.GetCurrentProcess();

        var snapshot = new SystemProcessList().Snapshot();

        Assert.Contains(new ProcInfo(self.ProcessName, self.Id), snapshot);
        Assert.DoesNotContain(snapshot, p => p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_new_snapshot_is_taken_each_time()
    {
        var list = new SystemProcessList();
        var before = list.Snapshot();
        using var child = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var during = list.Snapshot();

            Assert.DoesNotContain(before, p => p.Id == child.Id);
            Assert.Contains(during, p => p.Id == child.Id && p.Name.Equals("PING", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }
}
