using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class JobProcessLauncherTests
{
    /// <summary>Long enough for a slow machine, short enough that a hang fails the test instead of the suite.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static Dictionary<string, string> SystemEnv() =>
        new() { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")! };

    private static ProcessSpec Cmd(string command, Dictionary<string, string>? env = null) =>
        new(System32("cmd.exe"), command, Environment.SystemDirectory, env ?? SystemEnv());

    private static ProcessSpec LongPing() =>
        new(System32("ping.exe"), "-n 30 127.0.0.1", Environment.SystemDirectory, SystemEnv());

    private static ConcurrentQueue<string> Listen(IRunningProcess process)
    {
        var lines = new ConcurrentQueue<string>();
        process.Line += lines.Enqueue;
        return lines;
    }

    [Fact]
    public async Task Captures_stdout_and_stderr_lines_and_exit_code()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd("/c \"echo one& echo two 1>&2& exit 3\""));
        var lines = Listen(process);

        var code = await process.Exited.WaitAsync(Limit);

        Assert.Equal(3, code);
        Assert.Contains("one", lines);
        Assert.Contains("two", lines.Select(l => l.TrimEnd())); // cmd leaves a space before the redirection
    }

    [Fact]
    public async Task Lines_come_in_order_and_without_line_breaks()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd("/c \"echo first& echo second& echo third\""));
        var lines = Listen(process);

        await process.Exited.WaitAsync(Limit);

        Assert.Equal(["first", "second", "third"], lines);
    }

    [Fact]
    public async Task Lines_written_before_anyone_listened_are_still_delivered()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd("/c echo early"));
        await process.Exited.WaitAsync(Limit); // the line is long gone by the time we listen

        var lines = Listen(process);

        Assert.Equal(["early"], lines);
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_stop_the_output()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd("/c \"echo one& echo two\""));
        var lines = new ConcurrentQueue<string>();
        process.Line += line =>
        {
            lines.Enqueue(line);
            throw new InvalidOperationException("listener fault");
        };

        await process.Exited.WaitAsync(Limit);

        Assert.Equal(["one", "two"], lines);
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_starve_the_next_listener_and_the_failure_is_reported()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd("/c \"echo one& echo two\""));
        var failures = new ConcurrentQueue<Exception>();
        process.ListenerFailed += failures.Enqueue;
        var bad = new ConcurrentQueue<string>();
        var good = new ConcurrentQueue<string>();
        Action<string> both = line =>
        {
            bad.Enqueue(line);
            throw new InvalidOperationException("listener fault");
        };
        both += good.Enqueue; // one subscription holding both, so neither can miss a line to a timing gap

        process.Line += both;
        await process.Exited.WaitAsync(Limit);

        Assert.Equal(["one", "two"], bad);
        Assert.Equal(["one", "two"], good);
        Assert.Equal(2, failures.Count); // once per failure
        Assert.All(failures, f =>
        {
            Assert.IsType<InvalidOperationException>(f);
            Assert.Equal("listener fault", f.Message);
        });
    }

    [Fact]
    public async Task A_failure_handler_that_throws_is_ignored()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd("/c \"echo one& echo two\""));
        var lines = new ConcurrentQueue<string>();
        var reported = new ConcurrentQueue<Exception>();
        process.ListenerFailed += _ => throw new InvalidOperationException("failure handler fault");
        process.ListenerFailed += reported.Enqueue;

        process.Line += line =>
        {
            lines.Enqueue(line);
            throw new InvalidOperationException("listener fault");
        };
        await process.Exited.WaitAsync(Limit);

        Assert.Equal(["one", "two"], lines); // the reading went on
        Assert.Equal(2, reported.Count);      // and the later failure handler was still told
    }

    [Fact]
    public async Task Passes_only_the_given_environment()
    {
        var env = SystemEnv();
        env["MIRADOR_TEST"] = "xyz";
        using var launcher = new JobProcessLauncher();
        // USERNAME is set in this test process; cmd leaves a variable it doesn't know as typed.
        await using var process = launcher.Start(Cmd("/c echo %MIRADOR_TEST%:%USERNAME%", env));
        var lines = Listen(process);

        await process.Exited.WaitAsync(Limit);

        Assert.Equal(["xyz:%USERNAME%"], lines);
    }

    [Fact]
    public async Task Runs_in_the_working_directory()
    {
        using var dir = new TempDir();
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(new ProcessSpec(System32("cmd.exe"), "/c cd", dir.Path, SystemEnv()));
        var lines = Listen(process);

        await process.Exited.WaitAsync(Limit);

        Assert.Equal([dir.Path], lines);
    }

    [Fact]
    public async Task Arguments_with_spaces_and_non_ascii_reach_the_program()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Zoë Build", "monado build");
        Directory.CreateDirectory(folder);
        using var launcher = new JobProcessLauncher();
        // cd fails (exit code 1) when the folder in the command line did not arrive intact.
        await using var process = launcher.Start(Cmd($"/c cd /d \"{folder}\""));

        Assert.Equal(0, await process.Exited.WaitAsync(Limit));
    }

    [Fact]
    public async Task Output_is_read_as_utf8()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("utf8.txt"), "Zoë — ok" + Environment.NewLine, new System.Text.UTF8Encoding(false));
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd($"/c type \"{dir.File("utf8.txt")}\"")); // type copies the bytes as they are
        var lines = Listen(process);

        await process.Exited.WaitAsync(Limit);

        Assert.Equal(["Zoë — ok"], lines);
    }

    [Fact]
    public async Task Exited_completes_even_when_a_child_keeps_the_output_open()
    {
        using var launcher = new JobProcessLauncher();
        // cmd ends at once; the ping it started keeps the output pipes (and runs for 20 s). Exited waits a couple of seconds for it, then gives up.
        await using var process = launcher.Start(Cmd("/c start /b ping -n 20 127.0.0.1"));

        var code = await process.Exited.WaitAsync(Limit);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Id_is_the_operating_system_process_id()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(LongPing());

        using var found = System.Diagnostics.Process.GetProcessById(process.Id);

        Assert.Equal("PING", found.ProcessName.ToUpperInvariant());
    }

    [Fact]
    public async Task Kill_ends_the_process()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(LongPing());
        await Task.Delay(300);
        Assert.False(process.Exited.IsCompleted);

        process.Kill();

        await process.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Kill_after_the_process_has_exited_does_nothing()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(Cmd("/c exit 0"));
        Assert.Equal(0, await process.Exited.WaitAsync(Limit));

        process.Kill();
        process.Kill();
    }

    [Fact]
    public async Task Disposing_the_process_kills_it_and_a_second_dispose_is_fine()
    {
        using var launcher = new JobProcessLauncher();
        var process = launcher.Start(LongPing());
        var exited = process.Exited;

        await process.DisposeAsync();
        await process.DisposeAsync();

        Assert.True(exited.IsCompleted);
        process.Kill(); // after dispose: nothing to do
    }

    [Fact]
    public async Task Disposing_the_launcher_ends_its_processes()
    {
        var launcher = new JobProcessLauncher();
        await using var first = launcher.Start(LongPing());
        await using var second = launcher.Start(LongPing());
        await Task.Delay(300);
        Assert.False(first.Exited.IsCompleted);
        Assert.False(second.Exited.IsCompleted);

        launcher.Dispose(); // closing the job ends everything in it

        await Task.WhenAll(first.Exited, second.Exited).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_process_outlives_a_launcher_nobody_holds_any_more()
    {
        var process = StartAndForgetTheLauncher();

        // The launcher is garbage now. Its job handle must not be closed by the finalizer while the process is running,
        // or Windows ends the process (kill on close) at some random collection.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(500);
        Assert.False(process.Exited.IsCompleted);

        await process.DisposeAsync(); // disposing the process still ends it
        Assert.True(process.Exited.IsCompleted);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IRunningProcess StartAndForgetTheLauncher() => new JobProcessLauncher().Start(LongPing());

    [Fact]
    public void A_launcher_can_be_disposed_twice_and_then_refuses_to_start()
    {
        var launcher = new JobProcessLauncher();

        launcher.Dispose();
        launcher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => launcher.Start(Cmd("/c exit 0")));
    }

    [Fact]
    public void A_missing_program_is_reported_in_plain_words()
    {
        using var dir = new TempDir();
        using var launcher = new JobProcessLauncher();
        var missing = dir.File("not-there.exe");

        var ex = Assert.Throws<MiradorException>(() =>
            launcher.Start(new ProcessSpec(missing, "", Environment.SystemDirectory, SystemEnv())));

        Assert.Equal($"Couldn't start {missing}: The system cannot find the file specified.", ex.Message);
    }

    [Fact]
    public void A_file_that_is_not_a_program_is_reported_in_plain_words()
    {
        using var dir = new TempDir();
        using var launcher = new JobProcessLauncher();
        var notProgram = dir.File("fake.exe");
        File.WriteAllText(notProgram, "this is text, not a program");

        var ex = Assert.Throws<MiradorException>(() =>
            launcher.Start(new ProcessSpec(notProgram, "", Environment.SystemDirectory, SystemEnv())));

        Assert.StartsWith($"Couldn't start {notProgram}: ", ex.Message);
        Assert.DoesNotContain("%1", ex.Message); // Windows' texts name the file with a placeholder that must be filled in
    }

    [Fact]
    public void A_missing_working_directory_is_reported_in_plain_words()
    {
        using var dir = new TempDir();
        using var launcher = new JobProcessLauncher();

        var ex = Assert.Throws<MiradorException>(() =>
            launcher.Start(new ProcessSpec(System32("cmd.exe"), "/c exit 0", dir.File("no-folder"), SystemEnv())));

        Assert.StartsWith($"Couldn't start {System32("cmd.exe")}: ", ex.Message);
        Assert.Contains(dir.File("no-folder"), ex.Message);
    }
}
