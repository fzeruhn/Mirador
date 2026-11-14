using System.Diagnostics;
using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Tests;

/// <summary>A test of the built stand-in (<c>artifacts\standin\mirador-standin.exe</c>, from <c>standin\build.ps1</c>); skipped when it isn't built.</summary>
public class StandInFactAttribute : FactAttribute
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

/// <summary>A stand-in test on a test pipe: an elevated stand-in ignores <c>MIRADOR_STANDIN_PIPE</c> by design, so these run unelevated only.</summary>
public sealed class UnelevatedStandInFactAttribute : StandInFactAttribute
{
    public UnelevatedStandInFactAttribute()
    {
        if (Skip is null && Environment.IsPrivilegedProcess)
        {
            Skip = "An elevated stand-in ignores its test pipe: run this test unelevated.";
        }
    }
}

/// <summary>The theory form of <see cref="UnelevatedStandInFactAttribute"/>.</summary>
public sealed class UnelevatedStandInTheoryAttribute : TheoryAttribute
{
    public UnelevatedStandInTheoryAttribute() => Skip = new UnelevatedStandInFactAttribute().Skip;
}

public class StandInExeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static string Exe { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "artifacts", "standin", "mirador-standin.exe"));

    private static string UniqueName() => "mirador-standin-test-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Runs the stand-in against the test pipe <paramref name="pipe"/> (beside any real Mirador); its exit code, stderr and time taken.</summary>
    private static async Task<(int Code, string Error, TimeSpan Took)> RunAsync(string pipe, string? appPath = null, string? exe = null)
    {
        var info = new ProcessStartInfo(exe ?? Exe) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.Environment["MIRADOR_STANDIN_PIPE"] = pipe;
        if (appPath is not null)
        {
            info.Environment["MIRADOR_APP_PATH"] = appPath;
        }

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(Limit);
        }
        catch (TimeoutException)
        {
            process.Kill();
            throw;
        }

        return (process.ExitCode, await error, watch.Elapsed);
    }

    [UnelevatedStandInFact]
    public async Task Ok_exits_0_after_lingering()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, (line, ct) => Task.FromResult(line == AppPipe.Start ? AppPipe.Ok : "FAIL ?"));
        server.Start();

        var (code, _, took) = await RunAsync(name);

        Assert.Equal(0, code);
        Assert.InRange(took, TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(4.0));
    }

    [UnelevatedStandInFact]
    public async Task Fail_exits_1_at_once_with_the_message()
    {
        var name = UniqueName();
        await using var server = new AppPipeServer(name, (_, _) => Task.FromResult("FAIL SteamVR runs"));
        server.Start();

        var (code, error, took) = await RunAsync(name);

        Assert.Equal(1, code);
        Assert.Contains("SteamVR runs", error);
        Assert.True(took < TimeSpan.FromSeconds(2), took.ToString()); // no linger: an OK takes 2 s at least
    }

    [UnelevatedStandInFact]
    public async Task Starts_mirador_and_waits_for_it()
    {
        // attrib.exe stands in for a Mirador that hands over to another and ends well: it takes the stand-in's
        // "--background" as a file name to list, only reads, and exits 0. The "Mirador" answering comes a second later.
        var name = UniqueName();
        var handsOver = Path.Combine(Environment.SystemDirectory, "attrib.exe");
        await using var server = new AppPipeServer(name, (_, _) => Task.FromResult(AppPipe.Ok));
        var run = RunAsync(name, handsOver);
        await Task.Delay(1000);

        server.Start();
        var (code, error, _) = await run;

        Assert.True(code == 0, error);
    }

    [UnelevatedStandInFact]
    public async Task Unstartable_app_exits_4()
    {
        var (code, error, _) = await RunAsync(UniqueName(), @"C:\nowhere\Mirador.exe");

        Assert.Equal(4, code);
        Assert.Contains("couldn't be started", error);
    }

    [UnelevatedStandInTheory]
    [InlineData("monado", "monado-2026.11.10-1a2b3c4d5e", "bin", "service")]
    [InlineData("builds", "S-1-5-21-1-2-3-1001", "p1", "abc1234", "bin", "service")]
    public async Task An_installed_stand_in_starts_the_mirador_it_came_with(params string[] below)
    {
        using var dir = new TempDir();
        var app = dir.File("Mirador App");
        var service = Path.Combine([app, .. below]);
        Directory.CreateDirectory(service);
        var standIn = Path.Combine(service, "monado-service.exe");
        File.Copy(Exe, standIn);

        // whoami.exe as that Mirador: it turns "--background" down (exit 1), which the stand-in reports, so it was the
        // one beside the stand-in that started, not the Mirador in the registry.
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(app, "Mirador.exe"));

        var (code, error, _) = await RunAsync(UniqueName(), exe: standIn);

        Assert.Equal(4, code);
        Assert.Contains("closed at once (exit code 1)", error);
    }

    [StandInFact]
    public void The_built_stand_in_is_told_from_monado()
    {
        Assert.True(StandIn.IsStandIn(Exe));
    }
}
