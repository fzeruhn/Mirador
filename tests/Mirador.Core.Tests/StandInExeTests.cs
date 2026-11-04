using System.Diagnostics;
using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Tests;

/// <summary>A test of the built stand-in (<c>artifacts\standin\monado-service.exe</c>, from <c>standin\build.ps1</c>); skipped when it isn't built.</summary>
public sealed class StandInFactAttribute : FactAttribute
{
    public StandInFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "The stand-in is a Windows program.";
        }
        else if (!File.Exists(StandInExeTests.Exe))
        {
            Skip = $"The stand-in isn't built ({StandInExeTests.Exe}): run standin\\build.ps1.";
        }
    }
}

public class StandInExeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static string Exe { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "artifacts", "standin", "monado-service.exe"));

    private static string UniqueName() => "mirador-standin-test-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Runs the stand-in against the test pipe <paramref name="pipe"/> (beside any real Mirador); its exit code, stderr and time taken.</summary>
    private static async Task<(int Code, string Error, TimeSpan Took)> RunAsync(string pipe, string? appPath = null)
    {
        var info = new ProcessStartInfo(Exe) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.Environment["MIRADOR_STANDIN_PIPE"] = pipe;
        if (appPath is not null)
        {
            info.Environment["MIRADOR_APP_PATH"] = appPath;
        }

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(Limit);
        return (process.ExitCode, await error, watch.Elapsed);
    }

    [StandInFact]
    public async Task Ok_exits_0_after_lingering()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, (line, ct) => Task.FromResult(line == AppPipe.Start ? AppPipe.Ok : "FAIL ?"));
        server.Start();

        var (code, _, took) = await RunAsync(name);

        Assert.Equal(0, code);
        Assert.InRange(took, TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(4.0));
    }

    [StandInFact]
    public async Task Fail_exits_1_at_once_with_the_message()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, (_, _) => Task.FromResult("FAIL SteamVR runs"));
        server.Start();

        var (code, error, took) = await RunAsync(name);

        Assert.Equal(1, code);
        Assert.Contains("SteamVR runs", error);
        Assert.True(took < TimeSpan.FromSeconds(1), took.ToString());
    }

    [StandInFact]
    public async Task Starts_mirador_and_waits_for_it()
    {
        // whoami.exe stands in for Mirador: it starts (and ends); the "Mirador" answering comes a second later.
        var name = UniqueName();
        var whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        await using var server = new AppPipeServer(name, (_, _) => Task.FromResult(AppPipe.Ok));
        var run = RunAsync(name, whoami);
        await Task.Delay(1000);

        server.Start();
        var (code, error, _) = await run;

        Assert.True(code == 0, error);
    }

    [StandInFact]
    public async Task Unstartable_app_exits_4()
    {
        var (code, error, _) = await RunAsync(UniqueName(), @"C:\nowhere\Mirador.exe");

        Assert.Equal(4, code);
        Assert.Contains("couldn't be started", error);
    }

    [StandInFact]
    public void The_built_stand_in_is_told_from_monado()
    {
        Assert.True(StandIn.IsStandIn(Exe));
    }
}
