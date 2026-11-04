using Microsoft.Extensions.Time.Testing;
using Mirador.Core;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.DreamAir.Tests;

public sealed class DreamAirModuleTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDir dir = new();
    private readonly List<string> steps = [];
    private readonly FakeHelper helper;
    private readonly FakeUsbDevices usb = new(DreamAirIds.Board, DreamAirIds.EyeTracker, DreamAirIds.Receiver);
    private readonly FakeProcesses processes = new();
    private readonly TimerWatch time = new(new FakeTimeProvider());
    private readonly FakeConsoleStarter consoles;
    private readonly AppPaths paths;
    private readonly DreamAirStateStore state;
    private readonly string bundledLayer;
    private readonly List<IReadOnlyDictionary<string, string>> saves = [];
    private string? nvidia = "32.0.16.2101";
    private Action? duringChecks;

    /// <summary>What the display check writes and its exit code.</summary>
    private (string[] Lines, int ExitCode) check = (["PASS: 1800 flips, 0 missed"], 0);

    public DreamAirModuleTests()
    {
        helper = new FakeHelper(steps, processes);

        // Some of Pimax's software runs, so a Start asks the helper to stop it, unless a test removes it.
        processes.Add("PimaxClient", 7);
        paths = new AppPaths(Path.Combine(dir.Path, "Mirador"));
        state = new DreamAirStateStore(paths.DreamAirFile);
        state.Save(new DreamAirState(LastGoodDriver: "621.01"));

        var pimax = new PimaxInstall(Path.Combine(dir.Path, "Pimax", "Runtime"), "2.0.4.329");
        Directory.CreateDirectory(Path.GetDirectoryName(pimax.BlobPath("K11_RAWH"))!);
        File.WriteAllText(pimax.TobiiRuntimeExe, "exe");
        File.WriteAllText(pimax.BlobPath("K11_RAWH"), "blob");
        Registry = new FakeRegistry().Set(
            @"HKLM\SYSTEM\CurrentControlSet\Services\PiServiceLauncher", "ImagePath",
            $"\"{Path.Combine(pimax.RuntimeDir, "PiServiceLauncher.exe")}\"");

        dir.Touch("SteamVR", "drivers", "lighthouse", "bin", "win64", "driver_lighthouse.dll");
        bundledLayer = Path.Combine(dir.Path, "app", "layer");
        dir.Touch("app", "layer", DreamAirLayer.ManifestName);
        dir.Touch("app", "layer", DreamAirLayer.DllName);
        dir.Touch("app", "layer", DreamAirLayer.CheckExeName);

        consoles = new FakeConsoleStarter(spec =>
        {
            if (spec.FileName.EndsWith(DreamAirLayer.CheckExeName, StringComparison.Ordinal))
            {
                steps.Add("display check");
                var display = new FakeConsole(check.Lines);
                display.End(check.ExitCode);
                return display;
            }

            steps.Add("upload");
            var runtime = new FakeConsole("Opened device 'XR5DA-1'", "tobii_xr_create successful");
            runtime.End(0);
            return runtime;
        });
    }

    private FakeRegistry Registry { get; }

    public void Dispose() => dir.Dispose();

    private ModuleContext Context => new(paths, helper, processes, usb, time);

    private CalibrationStore? calibration;

    private DreamAirModule Module() => new(new DreamAirDeps(
        Registry,
        consoles.Start,
        new GeometryUploader(consoles.Start, usb, processes, paths, time),
        state,
        bundledLayer,
        () => { duringChecks?.Invoke(); return nvidia; },
        () => Path.Combine(dir.Path, "SteamVR"),
        _ => EyeBlobVariants.Tested,
        calibration));

    private static Profile DreamAirProfile(string? layerFolder = null) => new(
        "p1", "Dream Air", DreamAirModule.ModuleId, BuildSource.Bundled, [],
        layerFolder is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [DreamAirLayer.OptionKey] = layerFolder });

    private Task<ModuleSession> BeforeStart(Profile? profile = null, CancellationToken ct = default) =>
        Module().BeforeStartAsync(Context, profile ?? DreamAirProfile(), new Progress(steps), Save, ct).WaitAsync(Limit);

    private void Save(IReadOnlyDictionary<string, string> data)
    {
        saves.Add(data);
        steps.Add("save " + string.Join(",", data.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Fact]
    public async Task BeforeStart_stops_pimax_and_returns_the_fixed_environment()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Equal(
            new EnvVar[]
            {
                new("STEAMVR_LH_ENABLE", "1"),
                new("DREAMAIR_VK_LAYER", "1"),
                new("DISABLE_RTSS_LAYER", "1"),
                new("VK_LOADER_LAYERS_ALLOW", "VK_LAYER_DREAMAIR_nvapi_direct"),
                new("XRT_COMPOSITOR_FORCE_VK_DISPLAY", "0"),
                new("XRT_COMPOSITOR_USE_PRESENT_WAIT", "1"),
                new("U_PACING_COMP_PRESENT_TO_DISPLAY_OFFSET_MS", "8.9"),
                new("DREAMAIR_LOG", "info"),
            },
            session.Environment);
        Assert.Equal(new Dictionary<string, string> { ["pimaxWasRunning"] = "true" }, session.RecoveryData);
        Assert.Equal(
            new[] { "Checking the computer…", "Stopping Pimax's software…", "StopPimax", "save pimaxWasRunning=true", "save pimaxWasRunning=true", "Uploading the eye geometry…", "upload" },
            steps);
        Assert.Equal(new StatusLine("eye", "Eye tracker", StatusLevel.Ok, "Geometry uploaded"), session.Status.Single(l => l.Key == "eye"));
    }

    [Fact]
    public async Task Pimax_that_wasnt_running_is_saved_as_such()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: false);

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Equal("false", session.RecoveryData["pimaxWasRunning"]);
        Assert.Equal(["save pimaxWasRunning=false"], steps.Where(s => s.StartsWith("save", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task With_nothing_of_pimax_running_start_needs_no_helper()
    {
        processes.Remove("PimaxClient");
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service isn't running.") { NotDelivered = true };

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Empty(helper.Sent);
        Assert.Equal("false", session.RecoveryData["pimaxWasRunning"]);
        Assert.Equal(["save pimaxWasRunning=false"], steps.Where(s => s.StartsWith("save", StringComparison.Ordinal)));
        Assert.Contains("upload", steps);
    }

    [Theory]
    [InlineData("PiServiceLauncher")]
    [InlineData("pi_server")]
    [InlineData("VRSS_GAZE_PROVIDER")]
    public async Task Any_of_pimax_running_asks_the_helper(string name)
    {
        processes.Remove("PimaxClient");
        processes.Add(name);
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service isn't running.") { NotDelivered = true };

        var ex = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("Mirador's helper service isn't running.", ex.Message);
        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
    }

    [Fact]
    public async Task Layer_folder_adds_the_implicit_layer_path()
    {
        dir.Touch("vk-layer", "build", "layer", DreamAirLayer.ManifestName);
        dir.Touch("vk-layer", "build", "layer", DreamAirLayer.DllName);
        dir.Touch("vk-layer", "build", "check", DreamAirLayer.CheckExeName);
        var build = Path.Combine(dir.Path, "vk-layer", "build");

        using var session = (DreamAirSession)await BeforeStart(DreamAirProfile(build));

        Assert.Equal(new EnvVar("VK_ADD_IMPLICIT_LAYER_PATH", Path.Combine(build, "layer")), session.Environment[^1]);
        Assert.Equal(DreamAirModule.FixedEnvironment.Count + 1, session.Environment.Count);
    }

    [Fact]
    public async Task Failed_check_refuses_without_touching_pimax()
    {
        nvidia = null;

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("No NVIDIA GPU found. Mirador's Dream Air support needs a GeForce.", e.Message);
        Assert.Empty(helper.Sent);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task A_missing_layer_refuses_without_touching_pimax()
    {
        File.Delete(Path.Combine(bundledLayer, DreamAirLayer.DllName));

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.StartsWith("The Dream Air layer that comes with Mirador is incomplete", e.Message);
        Assert.Empty(helper.Sent);
    }

    [Fact]
    public async Task Missing_helper_refuses_with_its_message()
    {
        const string NotRunning = "Mirador's helper service isn't running. Reinstall Mirador; for a dev build, run tools\\dev-install-helper.ps1 as administrator.";
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, NotRunning) { NotDelivered = true };
        processes.Add("PiServiceLauncher");

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal(NotRunning, e.Message);
        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
        Assert.Equal(["save pimaxWasRunning=true", "StopPimax", "save "], steps.Where(s => s.StartsWith("save", StringComparison.Ordinal) || s == "StopPimax"));
    }

    [Fact]
    public async Task Pimax_seen_running_is_saved_before_the_helper_is_asked()
    {
        // Mirador may be killed during StopPimax, which can take half a minute.
        processes.Add("PiServiceLauncher");

        using var session = (DreamAirSession)await BeforeStart();

        Assert.True(steps.IndexOf("save pimaxWasRunning=true") < steps.IndexOf("StopPimax"));
    }

    [Fact]
    public async Task A_stop_with_no_answer_starts_pimax_again_when_it_was_seen_running()
    {
        // The helper may still be stopping the service: its reply just didn't come within the client's limit.
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service didn't answer in time.");
        processes.Add("PiServiceLauncher");

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("Mirador's helper service didn't answer in time.", e.Message);
        Assert.Equal([HelperCommand.StopPimax, HelperCommand.StartPimax], helper.Sent);
        Assert.Empty(saves[^1]);
    }

    [Fact]
    public async Task A_stop_with_no_answer_leaves_pimax_alone_when_it_wasnt_seen()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service didn't answer in time.");

        await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal([HelperCommand.StopPimax], helper.Sent);
        Assert.Empty(saves[^1]);
    }

    [Fact]
    public async Task Stop_during_the_checks_doesnt_touch_pimax()
    {
        using var cts = new CancellationTokenSource();
        duringChecks = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BeforeStart(ct: cts.Token));

        Assert.Empty(helper.Sent);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task A_session_file_that_cant_be_written_restarts_pimax_and_lets_the_error_out()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);
        var calls = 0;
        Task<ModuleSession> Failing() => Module().BeforeStartAsync(Context, DreamAirProfile(), new Progress(steps), data =>
        {
            if (++calls == 1)
            {
                throw new IOException("The disk is full.");
            }
        }, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(Failing);

        Assert.Equal([HelperCommand.StopPimax, HelperCommand.StartPimax], helper.Sent);
    }

    [Fact]
    public async Task A_display_check_that_never_finished_isnt_called_a_verdict()
    {
        state.Save(new DreamAirState());
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: false);
        consoles.Refusal = new MiradorException("Couldn't start dreamair-display-check.exe: Access is denied.");

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal(
            "Mirador couldn't check direct mode with the new NVIDIA driver 621.01: Couldn't start dreamair-display-check.exe: Access is denied. Try again.",
            e.Message);
    }

    [Fact]
    public async Task A_pimax_that_wouldnt_stop_is_started_again_before_refusing()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Pimax's service didn't stop within 20 s.", WasRunning: true);

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("Pimax's service didn't stop within 20 s.", e.Message);
        Assert.Equal([HelperCommand.StopPimax, HelperCommand.StartPimax], helper.Sent);
        Assert.Empty(saves[^1]);
    }

    [Fact]
    public async Task New_driver_runs_the_display_check_and_fail_restores_pimax()
    {
        state.Save(new DreamAirState(LastGoodDriver: "620.00"));
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);
        check = (["FAIL: NVAPI call 0x1F2E3D4C is missing in this driver", "what to do: go back to driver 620.00"], 1);

        var e = await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal(
            "Direct mode doesn't work with NVIDIA driver 621.01: NVAPI call 0x1F2E3D4C is missing in this driver. Go back to driver 620.00.",
            e.Message);
        Assert.Equal([HelperCommand.StopPimax, HelperCommand.StartPimax], helper.Sent);
        Assert.True(steps.IndexOf("save pimaxWasRunning=true") < steps.IndexOf("display check"));
        Assert.Empty(saves[^1]);
        Assert.Equal("620.00", state.Load().LastGoodDriver);
        Assert.Contains("NVAPI call 0x1F2E3D4C", File.ReadAllText(DreamAirModule.DisplayCheckFile(paths)));
        Assert.DoesNotContain("upload", steps);
    }

    [Fact]
    public async Task Passed_display_check_remembers_the_driver()
    {
        state.Save(new DreamAirState());

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Contains("display check", steps);
        Assert.Equal("621.01", state.Load().LastGoodDriver);
    }

    [Fact]
    public async Task The_same_driver_isnt_checked_again()
    {
        using var session = (DreamAirSession)await BeforeStart();

        Assert.DoesNotContain("display check", steps);
    }

    [Fact]
    public async Task Failed_geometry_upload_is_a_warning_not_a_refusal()
    {
        usb.Ids.Remove(DreamAirIds.EyeTracker);

        using var session = (DreamAirSession)await BeforeStart();

        Assert.Equal(
            new StatusLine("eye", "Eye tracker", StatusLevel.Warning, "The eye tracker isn't on USB. Is the headset connected and switched on?"),
            session.Status.Single(l => l.Key == "eye"));
    }

    [Fact]
    public async Task Stop_while_starting_restores_pimax_and_lets_the_cancel_out()
    {
        state.Save(new DreamAirState());
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);
        using var cts = new CancellationTokenSource();
        helper.OnSend = command =>
        {
            if (command == HelperCommand.StopPimax)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BeforeStart(ct: cts.Token));

        Assert.Equal([HelperCommand.StopPimax, HelperCommand.StartPimax], helper.Sent);
        Assert.Empty(saves[^1]);
        Assert.DoesNotContain("display check", steps);
    }

    [Fact]
    public async Task A_pimax_that_wont_start_again_keeps_the_recovery_data()
    {
        state.Save(new DreamAirState());
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);
        helper.Replies[HelperCommand.StartPimax] = new HelperReply(false, "Pimax's service is disabled.");
        check = (["FAIL: no display"], 1);

        await Assert.ThrowsAsync<MiradorException>(() => BeforeStart());

        Assert.Equal("true", saves[^1]["pimaxWasRunning"]);
    }

    [Fact]
    public async Task End_starts_pimax_only_if_it_ran()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: false);
        using (var session = (DreamAirSession)await BeforeStart())
        {
            await session.EndAsync(CancellationToken.None);
        }

        Assert.Equal([HelperCommand.StopPimax], helper.Sent);

        processes.Add("PimaxClient", 7); // Pimax runs again
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);
        using (var session = (DreamAirSession)await BeforeStart())
        {
            await session.EndAsync(CancellationToken.None);
        }

        Assert.Equal([HelperCommand.StopPimax, HelperCommand.StopPimax, HelperCommand.StartPimax], helper.Sent);
    }

    [Fact]
    public async Task End_throws_a_failed_pimax_start_so_that_recovery_keeps_its_data()
    {
        // Mirador shows the thrown message as a warning and keeps session.json for RecoverAsync.
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);
        helper.Replies[HelperCommand.StartPimax] = new HelperReply(false, "Pimax's service is disabled.");
        using var session = (DreamAirSession)await BeforeStart();

        var e = await Assert.ThrowsAsync<MiradorException>(() => session.EndAsync(CancellationToken.None));

        Assert.Equal("Mirador couldn't start Pimax's service again: Pimax's service is disabled. Mirador tries again the next time it starts.", e.Message);
    }

    [Fact]
    public async Task Pimax_warning_is_raised_once_per_appearance()
    {
        using var session = (DreamAirSession)await BeforeStart();
        var warnings = new List<SessionWarning>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };
        processes.Add("pi_server");

        // The fake clock runs the session's poll inside Advance.
        Poll(3);
        lock (warnings)
        {
            Assert.Single(warnings);
        }

        processes.Remove("pi_server");
        Poll(1);
        processes.Add("PiServiceLauncher");
        Poll(2);

        lock (warnings)
        {
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Equal(new SessionWarning("pimax", DreamAirSession.PimaxStarted), w));
        }
    }

    [Fact]
    public async Task Steamvr_starting_while_monado_runs_is_a_warning()
    {
        using var session = (DreamAirSession)await BeforeStart();
        var warnings = new List<SessionWarning>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };
        processes.Add("vrserver");

        Poll(2);

        lock (warnings)
        {
            Assert.Equal([new SessionWarning("lighthouse", DreamAirSession.SteamVrStarted)], warnings);
        }
    }

    [Fact]
    public async Task Recover_starts_pimax_when_it_had_run()
    {
        await Module().RecoverAsync(Context, DreamAirSession.Recovery(true), CancellationToken.None);
        await Module().RecoverAsync(Context, DreamAirSession.Recovery(false), CancellationToken.None);
        await Module().RecoverAsync(Context, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal([HelperCommand.StartPimax], helper.Sent);
    }

    [Fact]
    public async Task Recover_that_cant_start_pimax_says_so()
    {
        helper.Replies[HelperCommand.StartPimax] = new HelperReply(false, "Pimax's service is disabled.");

        var e = await Assert.ThrowsAsync<MiradorException>(
            () => Module().RecoverAsync(Context, DreamAirSession.Recovery(true), CancellationToken.None));

        Assert.Equal("Mirador stopped Pimax's service in an earlier session and couldn't start it again: Pimax's service is disabled.", e.Message);
    }

    [Fact]
    public async Task Checks_add_the_layer_to_the_pcs()
    {
        var results = await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.Equal(
            ["NVIDIA GPU", "Pimax Play", "Eye geometry", "SteamVR", "Headset", "Vulkan layer", "Headset variant"],
            results.Select(r => r.Label));
        Assert.All(results.SkipLast(1), r => Assert.Equal(StatusLevel.Ok, r.Level));
        Assert.Equal("Comes with Mirador", results[^2].Text);
        Assert.Equal(StatusLevel.Warning, results[^1].Level); // nothing known about the variant yet
    }

    [Fact]
    public async Task A_known_variant_needs_no_warning()
    {
        state.Save(new DreamAirState(LastGoodDriver: "621.01", BlobVariant: "K11_RAWH", BlobSource: "owner"));

        var results = await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.DoesNotContain(results, r => r.Label == "Headset variant");
    }

    [Fact]
    public async Task Check_display_stops_pimax_around_the_check_and_remembers_a_good_driver()
    {
        state.Save(new DreamAirState());
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);

        processes.Add("PiServiceLauncher");

        var (check, problem) = await Module().CheckDisplayAsync(Context, DreamAirProfile(), Save, CancellationToken.None);

        Assert.True(check.Pass);
        Assert.Null(problem);
        Assert.Equal(
            ["save pimaxWasRunning=true", "StopPimax", "save pimaxWasRunning=true", "display check", "StartPimax", "save "],
            steps);
        Assert.Equal("621.01", state.Load().LastGoodDriver);
    }

    [Fact]
    public async Task Check_display_says_when_pimax_wont_start_again()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(true, WasRunning: true);
        helper.Replies[HelperCommand.StartPimax] = new HelperReply(false, "Pimax's service is disabled.");
        check = (["FAIL: no flips"], 1);

        var (result, problem) = await Module().CheckDisplayAsync(Context, DreamAirProfile(), Save, CancellationToken.None);

        Assert.False(result.Pass);
        Assert.Equal("no flips", result.Message);
        Assert.Equal("Mirador couldn't start Pimax's service again: Pimax's service is disabled. Mirador tries again the next time it starts.", problem);
        Assert.Equal("true", saves[^1]["pimaxWasRunning"]); // left for recovery
        Assert.Equal("621.01", state.Load().LastGoodDriver); // unchanged by a fail
    }

    [Fact]
    public async Task Check_display_without_the_helper_runs_nothing()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service isn't running.") { NotDelivered = true };

        var (result, _) = await Module().CheckDisplayAsync(Context, DreamAirProfile(), Save, CancellationToken.None);

        Assert.Equal(new DisplayCheckResult(false, "Mirador's helper service isn't running.", null, "") { Finished = false }, result);
        Assert.DoesNotContain("display check", steps);
    }

    [Fact]
    public async Task Check_display_with_nothing_of_pimax_running_needs_no_helper()
    {
        processes.Remove("PimaxClient");
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service isn't running.") { NotDelivered = true };

        var (result, problem) = await Module().CheckDisplayAsync(Context, DreamAirProfile(), Save, CancellationToken.None);

        Assert.True(result.Pass);
        Assert.Null(problem);
        Assert.Empty(helper.Sent);
        Assert.Contains("display check", steps);
    }

    [Fact]
    public async Task Check_display_with_no_answer_from_the_helper_starts_pimax_again_when_it_was_seen()
    {
        helper.Replies[HelperCommand.StopPimax] = new HelperReply(false, "Mirador's helper service didn't answer in time.");
        processes.Add("PiServiceLauncher");

        await Module().CheckDisplayAsync(Context, DreamAirProfile(), Save, CancellationToken.None);

        Assert.Equal([HelperCommand.StopPimax, HelperCommand.StartPimax], helper.Sent);
        Assert.Empty(saves[^1]);
    }

    [Fact]
    public async Task Calibrations_are_kept_before_a_start_and_after_the_session()
    {
        var monadoDir = Path.Combine(dir.Path, "monado", "dreamair");
        Directory.CreateDirectory(monadoDir);
        File.WriteAllText(Path.Combine(monadoDir, "eye_calibration_XR5DA-1.bin"), "mine");
        calibration = new CalibrationStore(monadoDir, Path.Combine(dir.Path, "backups"), TimeProvider.System);

        var session = (DreamAirSession)await BeforeStart();
        Assert.Single(calibration.Backups("XR5DA-1"));

        File.WriteAllText(Path.Combine(monadoDir, "eye_calibration_XR5DA-1.bin"), "the tracker's"); // Monado replaced it
        await session.EndAsync(CancellationToken.None);
        session.Dispose();

        Assert.Equal(2, calibration.Backups("XR5DA-1").Count);
    }

    [Fact]
    public async Task Checks_read_the_variant_pimax_runs_with_and_remember_it()
    {
        processes.Add(PimaxNames.EyeRuntimeProcess);
        helper.Replies[HelperCommand.GetPimaxEyeBlob] = new HelperReply(true, Value: "K10_RAYH");

        await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.Equal(("K10_RAYH", "pimax"), (state.Load().BlobVariant, state.Load().BlobSource));
    }

    [Fact]
    public async Task Without_pimaxs_eye_runtime_the_helper_isnt_asked()
    {
        await Module().CheckAsync(Context, DreamAirProfile(), CancellationToken.None);

        Assert.DoesNotContain(HelperCommand.GetPimaxEyeBlob, helper.Sent);
    }

    [Fact]
    public async Task A_different_tracker_clears_the_variant_and_says_so_at_the_first_look()
    {
        state.Save(new DreamAirState(LastGoodDriver: "621.01", BlobVariant: "K10_RAYH", BlobSource: "owner", BlobTrackerSerial: "XR5DA-0"));
        using var session = (DreamAirSession)await BeforeStart();
        var warnings = new List<SessionWarning>();
        session.Warning += w => { lock (warnings) { warnings.Add(w); } };

        Poll(1);

        Assert.Equal((null, "XR5DA-1"), (state.Load().BlobVariant, state.Load().BlobTrackerSerial));
        lock (warnings)
        {
            Assert.Equal([new SessionWarning("geometry", DreamAirModule.DifferentHeadset)], warnings);
        }
    }

    [Fact]
    public void The_headset_is_present_by_its_board_or_its_eye_tracker()
    {
        var module = Module();

        Assert.True(module.IsPresent(new HashSet<UsbId> { DreamAirIds.Board }));
        Assert.True(module.IsPresent(new HashSet<UsbId> { DreamAirIds.EyeTracker }));
        Assert.False(module.IsPresent(new HashSet<UsbId> { DreamAirIds.Receiver }));
        Assert.True(module.UsesSteamVrLighthouse);
        Assert.Equal(("dreamair", "Pimax Dream Air"), (module.Id, module.DisplayName));
    }

    private void Poll(int times)
    {
        for (var i = 0; i < times; i++)
        {
            time.Clock.Advance(DreamAirSession.PollInterval);
        }
    }

    [Fact]
    public async Task Ending_stops_the_watch()
    {
        var session = (DreamAirSession)await BeforeStart();
        var warnings = 0;
        session.Warning += _ => Interlocked.Increment(ref warnings);
        await session.EndAsync(CancellationToken.None);
        processes.Add("pi_server");

        Poll(2);

        Assert.Equal(0, warnings);
    }

    private sealed class Progress(List<string> steps) : IProgress<string>
    {
        public void Report(string value) => steps.Add(value);
    }

    private sealed class FakeHelper(List<string> steps, FakeProcesses processes) : IHelperClient
    {
        public Dictionary<HelperCommand, HelperReply> Replies { get; } = [];

        public List<HelperCommand> Sent { get; } = [];

        public Action<HelperCommand>? OnSend { get; set; }

        public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(command);
            steps.Add(command.ToString());
            OnSend?.Invoke(command);
            var reply = Replies.GetValueOrDefault(command) ?? new HelperReply(true, WasRunning: command == HelperCommand.StopPimax ? true : null);
            if (command == HelperCommand.StopPimax && reply.Ok)
            {
                processes.Remove("PimaxClient"); // the helper ends Pimax's processes
            }

            return Task.FromResult(reply);
        }
    }
}
