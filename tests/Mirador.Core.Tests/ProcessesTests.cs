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
    public void The_program_file_of_a_process_can_be_read()
    {
        var list = new SystemProcessList();
        using var child = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Assert.Equal(Environment.ProcessPath, list.ImagePathOf(Environment.ProcessId), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "PING.EXE"), list.ImagePathOf(child.Id), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    [Fact]
    public void A_process_that_cannot_be_opened_has_no_program_file()
    {
        var list = new SystemProcessList();

        Assert.Null(list.ImagePathOf(0)); // the idle process: never opened
        Assert.Null(list.ImagePathOf(0x7FFFFFF0)); // no such process
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
