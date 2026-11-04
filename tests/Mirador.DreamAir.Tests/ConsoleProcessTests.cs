using System.Diagnostics;
using Mirador.Core;
using Mirador.Core.Monado;

namespace Mirador.DreamAir.Tests;

public class ConsoleProcessTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task A_program_runs_and_its_lines_and_exit_code_come_back()
    {
        using var process = ConsoleProcess.Start(new ProcessSpec(Cmd, "/c echo hello& exit 3", Environment.SystemDirectory, ConsoleProcess.CurrentEnvironment()));
        var lines = new List<string>();
        process.Line += line => { lock (lines) { lines.Add(line); } };

        var code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, code);
        lock (lines)
        {
            Assert.Contains("hello", lines);
        }
    }

    [Fact]
    public void A_file_that_is_not_a_program_fails_at_once_without_a_message_box()
    {
        using var dir = new TempDir();
        var notProgram = Path.Combine(dir.Path, "fake.exe");
        File.WriteAllText(notProgram, "this is text, not a program");
        var watch = Stopwatch.StartNew();

        // With Windows' message box this call would wait until someone clicked OK on "Unsupported 16-Bit Application".
        var ex = Assert.Throws<MiradorException>(() =>
            ConsoleProcess.Start(new ProcessSpec(notProgram, "", Environment.SystemDirectory, ConsoleProcess.CurrentEnvironment())));

        Assert.StartsWith($"Couldn't start {notProgram}: ", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }
}
