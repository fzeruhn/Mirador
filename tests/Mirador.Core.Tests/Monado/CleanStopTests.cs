using System.Collections;
using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Monado;

public class CleanStopTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static ProcessSpec LongPing() => new(
        Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1", Environment.SystemDirectory,
        new Dictionary<string, string> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")! });

    [Fact]
    public async Task Without_an_exit_key_a_process_is_never_ended_cleanly()
    {
        using var launcher = new JobProcessLauncher();
        await using var process = launcher.Start(LongPing());

        Assert.False(await process.EndCleanlyAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(Limit));
        Assert.False(process.Exited.IsCompleted);
    }

    [Fact]
    public async Task A_key_that_could_not_be_written_or_a_process_that_does_not_end_is_left_to_the_kill()
    {
        var asked = new List<int>();
        using var refused = new JobProcessLauncher(pid =>
        {
            asked.Add(pid);
            return false;
        });
        await using (var process = refused.Start(LongPing()))
        {
            Assert.False(await process.EndCleanlyAsync(TimeSpan.FromSeconds(1)).WaitAsync(Limit));
            Assert.Equal([process.Id], asked);
        }

        // A sender that says it wrote the key, to a process that doesn't end: the wait runs out.
        using var ignored = new JobProcessLauncher(_ => true);
        await using var stubborn = ignored.Start(LongPing());
        Assert.False(await stubborn.EndCleanlyAsync(TimeSpan.FromMilliseconds(300)).WaitAsync(Limit));
        stubborn.Kill();
        await stubborn.Exited.WaitAsync(Limit);
    }

    [Theory]
    [InlineData("--send-exit-key", "1234", 1234)]
    [InlineData("--send-exit-key", "0", null)]
    [InlineData("--send-exit-key", "-5", null)]
    [InlineData("--send-exit-key", "12a", null)]
    [InlineData("--other", "1234", null)]
    public void The_sender_mode_takes_one_process_id(string flag, string pid, int? expected) =>
        Assert.Equal(expected, ConsoleExitKey.Requested([flag, pid]));

    /// <summary>
    /// A real Monado, headless, started as Mirador starts it, then ended through Mirador.Helper.exe --send-exit-key: it
    /// ends by itself with exit code 0. Runs when <c>MIRADOR_TEST_MONADO_INSTALL</c> names an install (Monado's layout)
    /// and the helper is built; skipped otherwise.
    /// </summary>
    [RealMonadoFact]
    public async Task A_real_monado_ends_by_itself_on_the_exit_key()
    {
        var service = RealMonadoFactAttribute.Service!;
        var helper = RealMonadoFactAttribute.Helper;

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = (string?)e.Value ?? "";
        }

        env["XRT_COMPOSITOR_NULL"] = "1";
        env["SIMULATED_ENABLE"] = "1";
        env.Remove("LH_STANDBY_ON_EXIT");

        using var launcher = new JobProcessLauncher(pid => ConsoleExitKey.RunSender(helper, pid));
        await using var process = launcher.Start(new ProcessSpec(service, "", Path.GetDirectoryName(service)!, env));
        var up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Line += line =>
        {
            if (line.Contains("Supported formats", StringComparison.Ordinal))
            {
                up.TrySetResult();
            }
        };
        await up.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.True(await process.EndCleanlyAsync(TimeSpan.FromSeconds(3)).WaitAsync(Limit));
        Assert.Equal(0, await process.Exited);
    }
}

/// <summary>
/// Needs a real Monado install (<c>MIRADOR_TEST_MONADO_INSTALL</c>, Monado's layout) and a built Mirador.Helper.exe;
/// skipped otherwise.
/// </summary>
public sealed class RealMonadoFactAttribute : FactAttribute
{
    public static string? Service =>
        Environment.GetEnvironmentVariable("MIRADOR_TEST_MONADO_INSTALL") is { Length: > 0 } install
            ? Path.Combine(install, "bin", "monado-service.exe")
            : null;

    public static string Helper => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "Mirador.Helper", "bin", "x64", "Release", "net10.0-windows", "Mirador.Helper.exe"));

    public RealMonadoFactAttribute()
    {
        if (Service is not { } service || !File.Exists(service) || !File.Exists(Helper))
        {
            Skip = $"Needs MIRADOR_TEST_MONADO_INSTALL (a Monado install) and {Helper} (a Release build).";
        }
    }
}
