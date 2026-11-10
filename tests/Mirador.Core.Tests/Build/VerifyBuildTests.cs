using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Build;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Build;

public sealed class VerifyBuildTests : IDisposable
{
    private const string Ok = """XR-CHECK {"api":"d3d11","ok":true,"frames":300,"runtime":"Monado(XRT) 25.1.0","system":"Monado: Simulated HMD","error":""}""";

    private readonly TempDir dir = new();
    private readonly FakeCommandRunner runner = new();
    private readonly FakeServices services = new();
    private readonly List<string> lines = [];
    private readonly MonadoInstall install;
    private readonly VerifyBuild verify;

    public VerifyBuildTests()
    {
        Directory.CreateDirectory(dir.File("install", "bin", "service"));
        File.WriteAllText(dir.File("install", "bin", "service", "monado-service.exe"), "stand-in");
        install = new MonadoInstall(
            dir.File("install", "openxr_monado.json"), dir.File("install", "bin", "openxr_monado.dll"),
            dir.File("install", "bin", "monado-service.exe"), dir.File("install", "bin", "service"), false);
        runner.Answer = _ => ([Ok], 0);

        // The game's runtime starts the stand-in, which asks Mirador: here, while the stand-in step's client runs.
        runner.Effect = async (_, ct) =>
        {
            if (services.Waiting is { WasAsked: false } wait)
            {
                try
                {
                    await wait.StartAsync(ct);
                }
                catch (MiradorException)
                {
                    // The step reports it.
                }
            }
        };
        verify = new VerifyBuild(runner, @"C:\Mirador\tools\xr-check.exe", new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 20, 0, 0, TimeSpan.Zero)));
    }

    public void Dispose() => dir.Dispose();

    private Task<VerifyResult> Run(CancellationToken ct = default) => verify.RunAsync(install, "abc123", services, lines.Add, ct);

    private static string ArgumentAfter(CommandSpec spec, string name) => spec.Arguments[spec.Arguments.ToList().IndexOf(name) + 1];

    [Fact]
    public async Task Every_step_passes_with_a_build_that_works()
    {
        var result = await Run();

        Assert.Equal([VerifyBuild.D3D11Step, VerifyBuild.VulkanStep, VerifyBuild.StandInStep, VerifyBuild.ElevatedStep], result.Steps.Select(s => s.Name));
        Assert.All(result.Steps, s => Assert.Equal(VerifyOutcome.Passed, s.Outcome));
        Assert.True(result.HasElevationFix);
        Assert.Equal("abc123", result.Key);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 20, 0, 0, TimeSpan.Zero), result.At);

        Assert.Equal(4, runner.Commands.Count);
        Assert.All(runner.Commands, c =>
        {
            Assert.Equal(@"C:\Mirador\tools\xr-check.exe", c.FileName);
            Assert.Equal(install.Manifest, c.Environment!["XR_RUNTIME_JSON"]);
        });
        Assert.Equal(["d3d11", "vulkan", "d3d11", "d3d11"], runner.Commands.Select(c => ArgumentAfter(c, "--api")));
        Assert.Equal("300", ArgumentAfter(runner.Commands[0], "--frames"));
        Assert.Equal(
            ["headless started", "headless stopped", "stand-in expected", "stand-in started", "stand-in done", "stand-in stopped", "elevated started", "elevated stopped"],
            services.Events);
    }

    [Fact]
    public async Task A_headless_monado_that_doesnt_start_fails_both_sessions_and_the_rest_still_runs()
    {
        services.HeadlessFails = "Monado stopped while starting (exit code 1).";

        var result = await Run();

        Assert.Equal(VerifyOutcome.Failed, result.Steps[0].Outcome);
        Assert.Equal(VerifyOutcome.Failed, result.Steps[1].Outcome);
        Assert.Contains("exit code 1", result.Steps[0].Detail);
        Assert.Equal(VerifyOutcome.Passed, result.Steps[2].Outcome);
        Assert.Equal(VerifyOutcome.Passed, result.Steps[3].Outcome);
    }

    [Fact]
    public async Task A_failing_client_gives_its_own_error()
    {
        runner.Answer = spec => ArgumentAfter(spec, "--api") == "vulkan"
            ? (["""XR-CHECK {"api":"vulkan","ok":false,"frames":12,"runtime":"Monado","system":"","error":"xrEndFrame failed: XR_ERROR_SESSION_LOST"}"""], 1)
            : ([Ok], 0);

        var result = await Run();

        var vulkan = result.Steps.Single(s => s.Name == VerifyBuild.VulkanStep);
        Assert.Equal(VerifyOutcome.Failed, vulkan.Outcome);
        Assert.Contains("XR_ERROR_SESSION_LOST", vulkan.Detail);
        Assert.Contains("12", vulkan.Detail);
    }

    [Fact]
    public async Task A_client_without_a_result_line_fails_with_its_exit_code()
    {
        runner.Answer = _ => (["something else"], 7);

        var result = await Run();

        Assert.All(result.Steps, s => Assert.Equal(VerifyOutcome.Failed, s.Outcome));
        Assert.Contains("exit code 7", result.Steps[0].Detail);
    }

    [Fact]
    public async Task Without_a_stand_in_below_the_runtime_that_step_fails_without_asking_mirador()
    {
        File.Delete(dir.File("install", "bin", "service", "monado-service.exe"));

        var result = await Run();

        var standIn = result.Steps.Single(s => s.Name == VerifyBuild.StandInStep);
        Assert.Equal(VerifyOutcome.Failed, standIn.Outcome);
        Assert.Contains("stand-in", standIn.Detail);
        Assert.DoesNotContain("stand-in expected", services.Events);
        Assert.Equal(3, runner.Commands.Count);
    }

    [Fact]
    public async Task A_game_that_connected_without_asking_mirador_doesnt_pass_the_stand_in_step()
    {
        runner.Effect = null;

        var result = await Run();

        var standIn = result.Steps.Single(s => s.Name == VerifyBuild.StandInStep);
        Assert.Equal(VerifyOutcome.Failed, standIn.Outcome);
        Assert.Contains("another Monado", standIn.Detail);
    }

    [Fact]
    public async Task A_stand_in_start_that_failed_gives_its_reason()
    {
        services.StandInFails = "Monado stopped while starting (exit code 3).";
        runner.Answer = spec => services.Waiting is { WasAsked: true }
            ? (["""XR-CHECK {"api":"d3d11","ok":false,"frames":0,"runtime":"","system":"","error":"xrCreateInstance failed: XR_ERROR_RUNTIME_UNAVAILABLE"}"""], 1)
            : ([Ok], 0);

        var result = await Run();

        var standIn = result.Steps.Single(s => s.Name == VerifyBuild.StandInStep);
        Assert.Equal(VerifyOutcome.Failed, standIn.Outcome);
        Assert.Contains("exit code 3", standIn.Detail);
    }

    [Fact]
    public async Task The_elevated_step_cant_tell_about_the_fix_when_the_game_fails_as_you_too()
    {
        runner.Answer = spec => ArgumentAfter(spec, "--api") == "d3d11"
            ? (["""XR-CHECK {"api":"d3d11","ok":false,"frames":0,"runtime":"","system":"","error":"D3D11CreateDevice failed"}"""], 1)
            : ([Ok], 0);

        var result = await Run();

        var elevated = result.Steps.Single(s => s.Name == VerifyBuild.ElevatedStep);
        Assert.Equal(VerifyOutcome.Failed, elevated.Outcome);
        Assert.Contains("couldn't tell", elevated.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.HasElevationFix);
    }

    [Fact]
    public async Task The_summary_says_what_was_skipped()
    {
        services.ElevatedWhyNot = "Turn on Run as administrator for this profile first.";

        var result = await Run();

        Assert.Equal("Every check that ran passed; 1 skipped.", result.Summary());
    }

    [Fact]
    public async Task The_clients_result_lines_stay_out_of_the_shown_lines()
    {
        await Run();

        Assert.DoesNotContain(lines, l => l.StartsWith(XrCheckReport.Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_monado_cant_run_as_administrator_that_step_is_skipped_with_the_reason()
    {
        services.ElevatedWhyNot = "Turn on Run as administrator for this profile first.";

        var result = await Run();

        var elevated = result.Steps.Single(s => s.Name == VerifyBuild.ElevatedStep);
        Assert.Equal(VerifyOutcome.Skipped, elevated.Outcome);
        Assert.Equal("Turn on Run as administrator for this profile first.", elevated.Detail);
        Assert.Null(result.HasElevationFix);
    }

    [Fact]
    public async Task An_elevated_start_that_was_tried_and_failed_fails_the_step()
    {
        services.ElevatedFailed = "Mirador's helper didn't start Monado as administrator: the helper isn't running.";

        var result = await Run();

        var elevated = result.Steps.Single(s => s.Name == VerifyBuild.ElevatedStep);
        Assert.Equal(VerifyOutcome.Failed, elevated.Outcome);
        Assert.Contains("the helper isn't running", elevated.Detail);
        Assert.Null(result.HasElevationFix);
        Assert.StartsWith("Failed:", result.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_game_that_cant_connect_to_an_elevated_monado_means_no_elevation_fix()
    {
        var calls = 0;
        runner.Answer = _ => ++calls == 4
            ? (["""XR-CHECK {"api":"d3d11","ok":false,"frames":0,"runtime":"","system":"","error":"xrCreateInstance failed: XR_ERROR_RUNTIME_FAILURE"}"""], 1)
            : ([Ok], 0);

        var result = await Run();

        var elevated = result.Steps.Single(s => s.Name == VerifyBuild.ElevatedStep);
        Assert.Equal(VerifyOutcome.Failed, elevated.Outcome);
        Assert.Contains("elevation fix", elevated.Detail);
        Assert.False(result.HasElevationFix);
    }

    [Fact]
    public async Task Cancelling_stops_what_runs_and_says_so()
    {
        using var cts = new CancellationTokenSource();
        runner.Effect = (_, _) =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(cts.Token));

        Assert.Equal(["headless started", "headless stopped"], services.Events);
    }

    [Fact]
    public async Task A_result_is_kept_by_its_key_and_read_back()
    {
        var result = await Run();
        var file = dir.File("checks", "verify-p1.json");

        result.Save(file);

        var loaded = VerifyResult.Load(file);
        Assert.NotNull(loaded);
        Assert.Equal("abc123", loaded.Key);
        Assert.Equal(result.Steps, loaded.Steps);
        Assert.Null(VerifyResult.Load(dir.File("checks", "none.json")));
    }

    [Fact]
    public void The_key_is_the_builds_commit_when_it_says_one()
    {
        File.WriteAllText(dir.File("install", BuildInfo.FileName), """{"commit":"4f5025c0123456789abcdef0123456789abcdef0"}""");

        Assert.Equal("4f5025c0123456789abcdef0123456789abcdef0", VerifyBuild.KeyFor(install));
    }

    [Fact]
    public void Without_a_commit_the_key_is_the_services_hash()
    {
        File.WriteAllText(install.ServiceExe, "service");

        var key = VerifyBuild.KeyFor(install);

        Assert.StartsWith("sha256:", key, StringComparison.Ordinal);
        File.WriteAllText(install.ServiceExe, "another service");
        Assert.NotEqual(key, VerifyBuild.KeyFor(install));
    }

    [Fact]
    public void The_report_line_is_read_from_the_clients_output()
    {
        var report = XrCheckReport.Parse(["log line", Ok, "after"]);

        Assert.NotNull(report);
        Assert.True(report.Ok);
        Assert.Equal(300, report.Frames);
        Assert.Equal("Monado: Simulated HMD", report.System);
        Assert.Null(XrCheckReport.Parse(["XR-CHECK not json"]));
    }

    private sealed class FakeServices : IVerifyServices
    {
        public List<string> Events { get; } = [];

        public string? HeadlessFails { get; set; }

        public string? ElevatedWhyNot { get; set; }

        public string? StandInFails { get; set; }

        public string? ElevatedFailed { get; set; }

        public StandInWait? Waiting { get; private set; }

        public Task<IAsyncDisposable> StartHeadlessAsync(MonadoInstall install, CancellationToken ct)
        {
            if (HeadlessFails is { } why)
            {
                throw new MiradorException(why);
            }

            Events.Add("headless started");
            return Task.FromResult<IAsyncDisposable>(new Done(() => Events.Add("headless stopped")));
        }

        public StandInWait ExpectStandIn(MonadoInstall install)
        {
            Events.Add("stand-in expected");
            Waiting = new StandInWait(install, _ =>
            {
                if (StandInFails is { } why)
                {
                    throw new MiradorException(why);
                }

                Events.Add("stand-in started");
                return Task.FromResult<IAsyncDisposable>(new Done(() => Events.Add("stand-in stopped")));
            })
            {
                Ended = () => Events.Add("stand-in done"),
            };
            return Waiting;
        }

        public Task<ElevatedVerify> StartElevatedHeadlessAsync(MonadoInstall install, CancellationToken ct)
        {
            if (ElevatedWhyNot is { } why)
            {
                return Task.FromResult(new ElevatedVerify(null, why));
            }

            if (ElevatedFailed is { } failed)
            {
                return Task.FromResult(new ElevatedVerify(null, failed, Failed: true));
            }

            Events.Add("elevated started");
            return Task.FromResult(new ElevatedVerify(new Done(() => Events.Add("elevated stopped")), null));
        }

        private sealed class Done(Action action) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                action();
                return ValueTask.CompletedTask;
            }
        }
    }
}
