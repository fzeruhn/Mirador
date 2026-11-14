using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Core.Session;

namespace Mirador.Core.Tests.Session;

public class SessionControllerTests
{
    private static readonly TimeSpan Limit = Harness.Limit;

    /// <summary>How often the controller looks for Monado's pipe.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private static readonly IReadOnlyDictionary<string, string> WasRunning = new Dictionary<string, string> { ["wasRunning"] = "true" };

    private static readonly IReadOnlyDictionary<string, string> WasNotRunning = new Dictionary<string, string> { ["wasRunning"] = "false" };

    private static Task<StartOutcome> Start(Harness h) => h.Controller.StartAsync(StartReason.Button, CancellationToken.None);

    private static StatusLine MonadoLine(StatusLevel level, string text) => new("monado", "Monado", level, text);

    /// <summary>The lines of a log that Mirador still has open for writing.</summary>
    private static string[] ReadWhileWritten(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Consent_runs_elevated_only_from_the_installed_copy(bool installedCopy)
    {
        await using var h = new Harness(installedCopy: installedCopy);
        h.UseProfile(Harness.DreamAir with { RunAsAdministrator = true });

        var outcome = await h.StartRunningAsync();

        // A copy the helper would refuse (a portable or dev bundle) runs as the user instead of failing.
        Assert.True(outcome.Ok);
        Assert.Equal(installedCopy ? 1 : 0, h.ElevatedStarts.Count);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_elevated_start_says_whether_verify_found_the_elevation_fix(bool found)
    {
        await using var h = new Harness(installedCopy: true, elevationFix: (_, _) => found);
        h.UseProfile(Harness.DreamAir with { RunAsAdministrator = true });

        await h.StartRunningAsync();

        Assert.True(h.ElevatedStarts.TryDequeue(out var start));
        Assert.Equal(found, start.ElevationFix);
    }

    [Fact]
    public async Task A_stop_lets_monado_end_by_itself_before_any_kill()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();
        h.Launcher.Last!.EndsCleanly = true;

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.False(h.Launcher.Last.Killed);
        Assert.Contains("end cleanly", h.Steps.All);
    }

    [Fact]
    public async Task A_restart_kills_monado_at_once_so_its_controllers_stay_on()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();
        h.Launcher.Last!.EndsCleanly = true;

        await h.Controller.StopAsync(CancellationToken.None, restart: true).WaitAsync(Limit);

        Assert.True(h.Launcher.Last.Killed);
        Assert.DoesNotContain("end cleanly", h.Steps.All);
    }

    [Fact]
    public async Task A_monado_that_does_not_end_by_itself_is_killed()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.True(h.Launcher.Last!.Killed);
        var steps = h.Steps.All.ToList();
        Assert.Contains("end cleanly", steps);
        Assert.True(steps.IndexOf("end cleanly") < steps.IndexOf("kill"));
    }

    [Fact]
    public async Task A_profile_without_run_as_administrator_runs_as_you_whatever_the_consent()
    {
        await using var h = new Harness(installedCopy: true);

        var outcome = await h.StartRunningAsync();

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Empty(h.ElevatedStarts);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task A_direct_mode_headset_gets_the_layer_turned_on_by_the_session()
    {
        await using var h = new Harness();
        h.Module.UsesNvidiaDirectMode = true;

        var outcome = await h.StartRunningAsync();

        Assert.True(outcome.Ok, outcome.Message);
        var env = h.Launcher.Spec.Environment;
        Assert.Equal("1", env["MIRADOR_DIRECT_MODE"]);
        Assert.Equal("VK_LAYER_MIRADOR_direct_mode", env["VK_LOADER_LAYERS_ALLOW"]);
        Assert.Equal("1", env["FAKE_HEADSET"]);
        Assert.False(env.ContainsKey("VK_ADD_IMPLICIT_LAYER_PATH"));
    }

    [Fact]
    public async Task A_layer_folder_left_in_a_profile_is_ignored_for_the_layer_that_comes_with_mirador()
    {
        await using var h = new Harness();
        h.Module.UsesNvidiaDirectMode = true;
        h.UseProfile(Harness.DreamAir with { ModuleOptions = new Dictionary<string, string> { ["layerFolder"] = @"C:\gone\layer-build" } });

        var outcome = await h.StartRunningAsync();

        Assert.True(outcome.Ok, outcome.Message);
        var env = h.Launcher.Spec.Environment;
        Assert.Equal("1", env["MIRADOR_DIRECT_MODE"]);
        Assert.False(env.ContainsKey("VK_ADD_IMPLICIT_LAYER_PATH"));
    }

    [Fact]
    public async Task A_headset_without_direct_mode_gets_no_layer()
    {
        await using var h = new Harness();

        await h.StartRunningAsync();

        Assert.False(h.Launcher.Spec.Environment.ContainsKey("MIRADOR_DIRECT_MODE"));
    }

    [Fact]
    public async Task A_missing_layer_fails_the_start_before_monado_launches()
    {
        await using var h = new Harness();
        h.Module.UsesNvidiaDirectMode = true;
        Directory.Delete(h.BundledLayer, recursive: true);

        var outcome = await Start(h).WaitAsync(Harness.Limit);

        Assert.False(outcome.Ok);
        Assert.Contains("direct-mode layer", outcome.Message);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
    }

    [Fact]
    public async Task Start_runs_the_steps_in_order()
    {
        await using var h = new Harness();
        h.Module.UsesSteamVrLighthouse = true;

        var outcome = await h.StartRunningAsync();

        Assert.Equal(new StartOutcome(true, "Monado is running."), outcome);
        Assert.Equal(SessionState.Running, h.Controller.State);
        Assert.Equal(new[] { "processes", "before-start", "launch", "pipe" }, h.Steps.All);

        var spec = h.Launcher.Spec;
        Assert.Equal(h.BundledServer, spec.FileName);
        Assert.Equal(Path.GetDirectoryName(h.BundledServer), spec.WorkingDirectory);
        Assert.Equal("", spec.Arguments);
        Assert.Equal("1", spec.Environment["FAKE_HEADSET"]);
        Assert.Equal("100", spec.Environment["XRT_COMPOSITOR_SCALE_PERCENTAGE"]);
        Assert.Equal(@"C:\Users\Zoë", spec.Environment["USERPROFILE"]);
        Assert.Equal(new[] { h.Pipe.NameFor(spec.Environment) }, h.Pipe.Asked);

        Assert.True(h.Launcher.SessionFileExistedAtLaunch);
        var saved = new SessionFile(h.Paths.SessionFile).Read();
        IReadOnlyDictionary<string, string> recovery = new Dictionary<string, string> { ["wasRunning"] = "true" };
        Assert.Equal("p1", saved?.ProfileId);
        Assert.Equal("fake", saved?.ModuleId);
        Assert.Equal(recovery, saved?.Data);

        // What Mirador set itself: the settings and the module's variables, nothing inherited.
        var mine = h.Controller.LastMiradorEnvironment;
        Assert.NotNull(mine);
        Assert.Equal("1", mine["FAKE_HEADSET"]);
        Assert.Equal("100", mine["XRT_COMPOSITOR_SCALE_PERCENTAGE"]);
        Assert.False(mine.ContainsKey("USERPROFILE"));
        Assert.False(mine.ContainsKey("TEMP"));

        Assert.Equal(h.BundledManifest, h.Controller.CurrentInstall?.Manifest);
        Assert.Null(h.Controller.RuntimeNotice);
        Assert.Contains(SessionState.Starting, h.Changes);
        Assert.Equal(SessionState.Running, h.Changes.Last());

        // The bundled Monado's stand-in is the installer's business.
        Assert.Equal(Path.Combine(h.RuntimeDir, "service"), h.Controller.CurrentInstall?.StandInDir);
        Assert.False(Directory.Exists(h.Controller.CurrentInstall!.StandInDir));

        var again = await h.Controller.StartAsync(StartReason.Tray, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(new StartOutcome(true, "Monado is already running."), again);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task Start_refuses_while_steamvr_runs()
    {
        await using var h = new Harness();
        h.Module.UsesSteamVrLighthouse = true;
        h.Processes.Running.Add(new ProcInfo("vrserver", 42));

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "Close vrserver first: it uses the Lighthouse receiver."), outcome);
        Assert.Equal(0, h.Module.BeforeStartCalls);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task A_profile_without_a_module_starts_while_steamvr_runs()
    {
        await using var h = new Harness();
        h.UseProfile(Harness.DreamAir with { Name = "Custom", ModuleId = null });
        h.Processes.Running.Add(new ProcInfo("vrserver", 42));

        await h.StartRunningAsync();

        Assert.Equal(new[] { "processes", "launch", "pipe" }, h.Steps.All);
        Assert.False(h.Launcher.Spec.Environment.ContainsKey("FAKE_HEADSET"));
        Assert.Null(new SessionFile(h.Paths.SessionFile).Read()?.ModuleId);
        Assert.Equal(
            new[] { MonadoLine(StatusLevel.Ok, "Running (Custom)"), new StatusLine("apps", "Apps", StatusLevel.Info, "No apps") },
            h.Controller.Status);
    }

    [Fact]
    public async Task Hook_starts_after_the_refusals_and_before_the_module()
    {
        var hook = new FakeHook();
        await using var h = new Harness(hook);
        h.Module.UsesSteamVrLighthouse = true;
        var moduleCallsAtStart = -1;
        hook.OnStarted = () => moduleCallsAtStart = h.Module.BeforeStartCalls;

        // Refused by SteamVR running, or by a Monado outside Mirador: the hook is never told.
        h.Processes.Running.Add(new ProcInfo("vrserver", 42));
        Assert.False((await Start(h).WaitAsync(Limit)).Ok);
        h.Processes.Running.Clear();
        h.Processes.Add("monado-service", 1234, Harness.OtherMonado);
        Assert.False((await Start(h).WaitAsync(Limit)).Ok);
        Assert.Empty(hook.Calls);
        h.Processes.Running.Clear();

        await h.StartRunningAsync();

        Assert.Equal(new[] { "started:p1:fake" }, hook.Calls);
        Assert.Equal(0, moduleCallsAtStart);
        Assert.Equal(1, h.Module.BeforeStartCalls);
    }

    [Fact]
    public async Task No_hook_for_a_game_start_with_the_headset_off()
    {
        var hook = new FakeHook();
        await using var h = new Harness(hook);
        h.Pipe.Up = true;
        h.Usb.Devices.Clear();
        var standIn = StandInBelow(h.RuntimeDir);

        var outcome = await h.Controller.StartAsync(StartReason.Game, standIn, CancellationToken.None).WaitAsync(Limit);

        Assert.False(outcome.Ok);
        Assert.Empty(hook.Calls);
    }

    [Fact]
    public async Task No_hook_when_the_headset_is_unplugged_for_a_button_start()
    {
        var hook = new FakeHook();
        await using var h = new Harness(hook);
        h.Usb.Devices.Clear();

        await h.StartRunningAsync(); // goes on as before: the module's steps run
        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(1, h.Module.BeforeStartCalls);
        Assert.Empty(hook.Calls);
    }

    [Fact]
    public async Task Hook_ends_after_stop()
    {
        var hook = new FakeHook();
        await using var h = new Harness(hook);
        var endsAtEnded = -1;
        var killedAtEnded = false;
        hook.OnEnded = () =>
        {
            endsAtEnded = h.Module.Session!.Ends;
            killedAtEnded = h.Launcher.Last!.Killed;
        };
        await h.StartRunningAsync();
        Assert.Equal(new[] { "started:p1:fake" }, hook.Calls);

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new[] { "started:p1:fake", "ended:p1:fake" }, hook.Calls);
        Assert.Equal(1, endsAtEnded); // last of all: after the module's session ended
        Assert.True(killedAtEnded);
    }

    [Fact]
    public async Task Current_profile_is_the_sessions_until_it_has_ended()
    {
        var hook = new FakeHook();
        await using var h = new Harness(hook);
        SessionProfile? atStarted = null, atEnded = null;
        hook.OnStarted = () => atStarted = h.Controller.CurrentProfile;
        hook.OnEnded = () => atEnded = h.Controller.CurrentProfile;
        Assert.Null(h.Controller.CurrentProfile);

        await h.StartRunningAsync();

        Assert.Equal(Harness.DreamAir.Id, atStarted?.Profile.Id);
        Assert.Same(h.Module, atStarted?.Module);
        Assert.Equal(Harness.DreamAir.Id, h.Controller.CurrentProfile?.Profile.Id);
        Assert.Same(h.Module, h.Controller.CurrentProfile?.Module);

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        // While it stops, until the very end.
        Assert.Equal(Harness.DreamAir.Id, atEnded?.Profile.Id);
        Assert.Same(h.Module, atEnded?.Module);
        Assert.Null(h.Controller.CurrentProfile);
    }

    [Fact]
    public async Task Hook_ends_after_a_failed_start()
    {
        var hook = new FakeHook();
        await using var h = new Harness(hook);
        h.Module.Refusal = new MiradorException("The headset refused.");

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.False(outcome.Ok);
        Assert.Equal(new[] { "started:p1:fake", "ended:p1:fake" }, hook.Calls);
    }

    [Fact]
    public async Task Hook_ends_when_monado_stops_by_itself()
    {
        var hook = new FakeHook();
        await using var h = new Harness(hook);
        await h.StartRunningAsync();

        h.Launcher.Last!.Exit(5);
        await h.HasEnded.Task.WaitAsync(Limit);

        Assert.Equal(new[] { "started:p1:fake", "ended:p1:fake" }, hook.Calls);
    }

    [Fact]
    public async Task Throwing_hook_is_a_warning()
    {
        var hook = new FakeHook { Failure = new InvalidOperationException("Bluetooth is off.") };
        await using var h = new Harness(hook);

        var outcome = await h.StartRunningAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(SessionState.Running, h.Controller.State);
        Assert.Equal(new[] { new SessionWarning("basestations", "Mirador couldn't switch the base stations: Bluetooth is off.") }, h.Warnings);

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.Equal(new[] { "started:p1:fake", "ended:p1:fake" }, hook.Calls);
        Assert.Equal(2, h.Warnings.Count);
        Assert.All(h.Warnings, w => Assert.Equal("basestations", w.Key));
        Assert.Equal(1, h.Module.Session!.Ends);
    }

    [Fact]
    public async Task Devices_null_without_lighthouse()
    {
        await using var h = new Harness();
        Assert.Null(h.Controller.Devices);

        await h.StartRunningAsync();
        h.Launcher.Last!.Emit(" INFO [TrackedDeviceAdded] Found lighthouse controller: LHR-AAAA0001");

        Assert.Null(h.Controller.Devices);
    }

    [Fact]
    public async Task Devices_follow_the_log()
    {
        await using var h = new Harness();
        h.Module.UsesSteamVrLighthouse = true;
        Assert.Null(h.Controller.Devices);

        await h.StartRunningAsync();
        Assert.Empty(h.Controller.Devices!);

        var changes = 0;
        h.Controller.StatusChanged += () => changes++;
        h.Launcher.Last!.Emit(
            " INFO [TrackedDeviceAdded] Found lighthouse HMD: LHR-2A2E5F48",
            " INFO [TrackedDeviceAdded] Found lighthouse controller: LHR-AAAA0001",
            " INFO [TrackedDeviceAdded] Found lighthouse base station: LHB-1234ABCD",
            " INFO [TrackedDeviceAdded] Found lighthouse tracker: LHR-BBBB0002");

        Assert.Equal(
            new[] { new TrackedDevice(TrackedKind.Controller, "LHR-AAAA0001"), new TrackedDevice(TrackedKind.Tracker, "LHR-BBBB0002") },
            h.Controller.Devices);
        Assert.True(changes >= 3, $"{changes} status changes");

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        Assert.Null(h.Controller.Devices);
    }

    [Fact]
    public async Task Start_refuses_when_monado_service_already_runs()
    {
        await using var h = new Harness();
        h.Processes.Add("explorer", 7, @"C:\Windows\explorer.exe");
        h.Processes.Add("monado-service", 1234, Harness.OtherMonado);

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(
            new StartOutcome(false, "Monado is already running outside Mirador (monado-service, process 1234). Close it, then press Start."),
            outcome);
        Assert.Equal(0, h.Module.BeforeStartCalls);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)] // two games, each with its stand-in waiting
    public async Task Stand_ins_are_not_a_monado(int standIns)
    {
        await using var h = new Harness();
        for (var n = 1; n <= standIns; n++)
        {
            h.Processes.Add("monado-service", 500 + n, Harness.StandInImages + $@"game {n}\monado-service.exe");
        }

        await h.StartRunningAsync();

        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task A_stand_in_next_to_a_real_monado_still_refuses_naming_the_real_one()
    {
        await using var h = new Harness();
        h.Processes.Add("monado-service", 501, Harness.StandInImages + "monado-service.exe");
        h.Processes.Add("monado-service", 1234, Harness.OtherMonado);

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(
            new StartOutcome(false, "Monado is already running outside Mirador (monado-service, process 1234). Close it, then press Start."),
            outcome);
        Assert.Equal(0, h.Launcher.Starts);
    }

    [Fact]
    public async Task A_monado_service_whose_file_cannot_be_read_counts_as_a_monado()
    {
        await using var h = new Harness();
        h.Processes.Add("monado-service", 1234); // ended a moment ago, or not this user's to ask

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(
            new StartOutcome(false, "Monado is already running outside Mirador (monado-service, process 1234). Close it, then press Start."),
            outcome);
        Assert.Equal(0, h.Launcher.Starts);
    }

    [Fact]
    public async Task Start_refuses_a_module_it_does_not_know()
    {
        await using var h = new Harness();
        h.UseProfile(Harness.DreamAir with { ModuleId = "quest" });

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "This profile uses a headset Mirador doesn't know (quest)."), outcome);
        Assert.Equal(0, h.Launcher.Starts);
    }

    [Fact]
    public async Task Module_refusal_becomes_the_outcome()
    {
        const string helperText =
            "Mirador's helper service isn't running. Reinstall Mirador; for a dev build, run tools\\dev-install-helper.ps1 as administrator.";
        await using var h = new Harness();
        h.Module.Refusal = new MiradorException(helperText);

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, helperText), outcome);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.False(File.Exists(h.Paths.SessionFile));
        Assert.Equal(new[] { MonadoLine(StatusLevel.Info, "Stopped") }, h.Controller.Status);
    }

    [Fact]
    public async Task What_the_module_saves_while_it_gets_ready_is_in_the_session_file_at_once()
    {
        await using var h = new Harness();
        IReadOnlyDictionary<string, string> early = new Dictionary<string, string> { ["wasRunning"] = "true" };
        h.Module.SavedDuringStart = early;
        h.Module.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Pipe.Up = true;

        var start = h.Controller.StartAsync(StartReason.Button, CancellationToken.None);
        await h.Module.Entered.Task.WaitAsync(Limit);

        // Were Mirador killed now, its next launch would find this and undo the module's step (Review Focus 4).
        var saved = new SessionFile(h.Paths.SessionFile).Read();
        Assert.NotNull(saved);
        Assert.Equal("p1", saved.Value.ProfileId);
        Assert.Equal("fake", saved.Value.ModuleId);
        Assert.Equal(early, saved.Value.Data);

        h.Module.Gate.SetResult();
        Assert.True((await start.WaitAsync(Limit)).Ok);
        Assert.Equal(h.Module.RecoveryData, new SessionFile(h.Paths.SessionFile).Read()!.Value.Data);
    }

    [Fact]
    public async Task A_module_that_undid_its_steps_before_refusing_leaves_no_session_file()
    {
        await using var h = new Harness();
        h.Module.SavedDuringStart = new Dictionary<string, string> { ["wasRunning"] = "true" };
        h.Module.SavedBeforeRefusal = new Dictionary<string, string>(); // undone: nothing left
        h.Module.Refusal = new MiradorException("The eye tracker isn't on USB.");

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "The eye tracker isn't on USB."), outcome);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task A_module_that_refuses_with_steps_left_to_undo_has_them_undone_later()
    {
        await using var h = new Harness();
        IReadOnlyDictionary<string, string> left = new Dictionary<string, string> { ["wasRunning"] = "true" };
        h.Module.SavedDuringStart = left;
        h.Module.Refusal = new MiradorException("Direct mode doesn't work, and Pimax's service wouldn't start again.");

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.False(outcome.Ok);
        Assert.Equal(left, new SessionFile(h.Paths.SessionFile).Read()!.Value.Data);

        await h.Controller.RecoverAsync(CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(left, h.Module.Recovered);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task A_save_after_the_module_got_ready_is_ignored()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();
        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        Assert.False(File.Exists(h.Paths.SessionFile));

        h.Module.SaveRecovery!(new Dictionary<string, string> { ["late"] = "1" });

        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task A_monado_service_that_ended_before_its_file_could_be_read_does_not_count()
    {
        await using var h = new Harness();
        h.Processes.Add("monado-service", 1234); // a stand-in that gave up just as Start looked
        h.Processes.EndAfterSnapshot.Add(1234);
        h.Pipe.Up = true;

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task Two_starts_share_one_service()
    {
        await using var h = new Harness();
        h.Module.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Pipe.Up = true;

        var first = h.Controller.StartAsync(StartReason.Button, CancellationToken.None);
        await h.Module.Entered.Task.WaitAsync(Limit);
        Assert.Equal(SessionState.Starting, h.Controller.State);
        Assert.Equal(MonadoLine(StatusLevel.Info, "Starting…"), h.Controller.Status[0]);
        var second = h.Controller.StartAsync(StartReason.Game, CancellationToken.None);
        Assert.False(second.IsCompleted);
        h.Module.Gate.SetResult();

        var outcomes = await Task.WhenAll(first, second).WaitAsync(Limit);

        Assert.All(outcomes, o => Assert.Equal(new StartOutcome(true, "Monado is running."), o));
        Assert.Equal(1, h.Launcher.Starts);
        Assert.Equal(1, h.Module.BeforeStartCalls);
    }

    /// <summary>A stand-in file in <paramref name="runtimeDir"/>'s <c>service</c> folder, as a game's runtime DLL would start it.</summary>
    private static string StandInBelow(string runtimeDir)
    {
        var path = Path.Combine(runtimeDir, "service", StandIn.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stand-in");
        return path;
    }

    [Fact]
    public async Task A_game_of_the_profiles_monado_starts_it()
    {
        await using var h = new Harness();
        h.Pipe.Up = true;
        var standIn = StandInBelow(h.RuntimeDir);

        var outcome = await h.Controller.StartAsync(StartReason.Game, standIn, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(true, "Monado is running."), outcome);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task Game_start_refused_when_headset_unplugged()
    {
        await using var h = new Harness();
        h.Pipe.Up = true;
        h.Module.UsesSteamVrLighthouse = true;
        h.Processes.Running.Add(new ProcInfo("vrserver", 42)); // a later step would refuse with its own words
        h.Usb.Devices.Clear();
        var standIn = StandInBelow(h.RuntimeDir);

        var outcome = await h.Controller.StartAsync(StartReason.Game, standIn, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "Monado wasn't started: the Fake headset isn't connected."), outcome);
        Assert.Equal(0, h.Module.BeforeStartCalls);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task Game_start_with_unknown_stand_in_refused_too()
    {
        await using var h = new Harness();
        h.Pipe.Up = true;
        h.Usb.Devices.Clear();

        var outcome = await h.Controller.StartAsync(StartReason.Game, null, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "Monado wasn't started: the Fake headset isn't connected."), outcome);
        Assert.Equal(0, h.Launcher.Starts);
    }

    [Fact]
    public async Task Button_start_ignores_presence()
    {
        await using var h = new Harness();
        h.Usb.Devices.Clear();

        var outcome = await h.StartRunningAsync();

        Assert.Equal(new StartOutcome(true, "Monado is running."), outcome);
    }

    [Fact]
    public async Task Game_start_on_custom_profile_ignores_presence()
    {
        await using var h = new Harness();
        h.UseProfile(Harness.DreamAir with { Name = "Custom", ModuleId = null });
        h.Pipe.Up = true;
        h.Usb.Devices.Clear();
        var standIn = StandInBelow(h.RuntimeDir);

        var outcome = await h.Controller.StartAsync(StartReason.Game, standIn, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(true, "Monado is running."), outcome);
    }

    [Fact]
    public async Task Game_joins_running_session_with_headset_unplugged()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();
        h.Usb.Devices.Clear();
        var standIn = StandInBelow(h.RuntimeDir);

        var outcome = await h.Controller.StartAsync(StartReason.Game, standIn, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(true, "Monado is already running."), outcome);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task A_game_of_another_monado_is_refused_before_any_step()
    {
        await using var h = new Harness();
        h.Pipe.Up = true;
        var other = StandInBelow(Path.Combine(h.Root, "other build", "runtime"));

        var outcome = await h.Controller.StartAsync(StartReason.Game, other, CancellationToken.None).WaitAsync(Limit);

        Assert.False(outcome.Ok);
        Assert.StartsWith($"The game uses the Monado in {Path.Combine(h.Root, "other build", "runtime")}, but the profile ", outcome.Message);
        Assert.Contains($"uses the one in {h.RuntimeDir}.", outcome.Message);
        Assert.Equal(0, h.Module.BeforeStartCalls);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
    }

    [Fact]
    public async Task A_game_of_another_monado_doesnt_get_the_running_one()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();
        var other = StandInBelow(Path.Combine(h.Root, "other build", "runtime"));

        var outcome = await h.Controller.StartAsync(StartReason.Game, other, CancellationToken.None).WaitAsync(Limit);

        Assert.False(outcome.Ok);
        Assert.Equal(SessionState.Running, h.Controller.State);
        Assert.Equal(1, h.Launcher.Starts);
    }

    [Fact]
    public async Task Starting_shows_the_module_progress_and_passes_on_its_warnings()
    {
        await using var h = new Harness();
        h.Module.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Module.Progress = "Getting the headset ready…";

        var start = Start(h);
        await h.Module.Entered.Task.WaitAsync(Limit);
        Assert.Equal(MonadoLine(StatusLevel.Info, "Getting the headset ready…"), h.Controller.Status[0]);
        h.Pipe.Up = true;
        h.Module.Gate.SetResult();
        await start.WaitAsync(Limit);
        h.Module.Session!.Warn(new SessionWarning("fake", "Careful."));

        Assert.Contains(new SessionWarning("fake", "Careful."), h.Warnings);
    }

    [Fact]
    public async Task Start_looks_for_the_pipe_every_100_ms()
    {
        await using var h = new Harness();

        var start = Start(h);
        await h.Pipe.FirstPoll.Task.WaitAsync(Limit);
        await h.Timers.Set(Poll).WaitAsync(Limit); // the controller now waits 100 ms
        h.Time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.Single(h.Pipe.Asked);
        Assert.False(start.IsCompleted);
        h.Pipe.Up = true;
        h.Time.Advance(TimeSpan.FromMilliseconds(1));

        var outcome = await start.WaitAsync(Limit);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(2, h.Pipe.Asked.Count);
    }

    [Fact]
    public async Task Exit_during_start_fails_with_last_lines_and_ends_session()
    {
        await using var h = new Harness();
        h.Launcher.FirstLines = ["line 1", "line 2"];

        var start = Start(h);
        await h.Pipe.FirstPoll.Task.WaitAsync(Limit);
        var process = h.Launcher.Last!;
        process.Emit("line 3", "line 4", "line 5", "line 6", "line 7");
        process.Exit(3);
        var outcome = await start.WaitAsync(Limit);

        var nl = Environment.NewLine;
        Assert.Equal(
            new StartOutcome(false, $"Monado stopped while starting (exit code 3). Last lines:{nl}line 3{nl}line 4{nl}line 5{nl}line 6{nl}line 7"),
            outcome);
        Assert.Equal(1, h.Module.Session!.Ends);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.False(File.Exists(h.Paths.SessionFile));
        Assert.Empty(h.EndedMessages); // a failed Start is told through its outcome

        string[] all = ["line 1", "line 2", "line 3", "line 4", "line 5", "line 6", "line 7"];
        Assert.Equal(all, h.Module.Session.Lines);
        Assert.Equal(all, h.Controller.LogTail);
        Assert.Equal(all, File.ReadAllLines(h.MonadoLog));
        Assert.True(process.Disposed);
        Assert.Equal(0, process.Listeners);
    }

    [Fact]
    public async Task Start_gives_up_after_45_seconds()
    {
        await using var h = new Harness();
        var log = h.MonadoLog;

        var start = Start(h);
        await h.Pipe.FirstPoll.Task.WaitAsync(Limit);
        await h.Timers.Set(Poll).WaitAsync(Limit);
        h.Time.Advance(TimeSpan.FromSeconds(44));
        Assert.False(start.IsCompleted);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        var outcome = await start.WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, $"Monado didn't come up within 45 s. Its log: {log}"), outcome);
        Assert.True(h.Launcher.Last!.Killed);
        Assert.Equal(1, h.Module.Session!.Ends);
        Assert.Equal(new[] { "kill", "end" }, h.Steps.All[^2..]);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task Stop_while_starting_ends_what_was_started()
    {
        await using var h = new Harness();

        var start = Start(h);
        await h.Pipe.FirstPoll.Task.WaitAsync(Limit);
        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        var outcome = await start.WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "Monado was stopped while it was starting."), outcome);
        Assert.True(h.Launcher.Last!.Killed);
        Assert.Equal(1, h.Module.Session!.Ends);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task Exit_while_running_ends_session_and_says_so()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();

        h.Launcher.Last!.Exit(5);
        var message = await h.HasEnded.Task.WaitAsync(Limit);

        Assert.Equal("Monado stopped by itself (exit code 5).", message);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.Equal(1, h.Module.Session!.Ends);
        Assert.False(File.Exists(h.Paths.SessionFile));
        Assert.Null(h.Controller.CurrentInstall);
        Assert.Equal(new[] { MonadoLine(StatusLevel.Info, "Stopped") }, h.Controller.Status);
    }

    [Fact]
    public async Task Exit_after_the_last_app_closed_says_so()
    {
        await using var h = new Harness();
        h.Settings.Save(new MonadoSettings(StopWhenLastAppCloses: true));
        await h.StartRunningAsync();
        Assert.Equal("1", h.Launcher.Spec.Environment["IPC_EXIT_ON_DISCONNECT"]);

        h.Launcher.Last!.Exit(0);

        Assert.Equal("Monado stopped after the last app closed.", await h.HasEnded.Task.WaitAsync(Limit));
    }

    [Fact]
    public async Task Stop_kills_ends_and_deletes_session_file()
    {
        await using var h = new Harness();
        await h.StartRunningAsync();
        Assert.True(File.Exists(h.Paths.SessionFile));
        var process = h.Launcher.Last!;

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.True(process.Killed);
        Assert.True(process.Disposed);
        Assert.Equal(0, process.Listeners);
        Assert.Equal(new[] { "kill", "end" }, h.Steps.All[^2..]);
        Assert.Equal(1, h.Module.Session!.Ends);
        Assert.False(File.Exists(h.Paths.SessionFile));
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.Equal(new[] { "Stopped." }, h.EndedMessages);

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        Assert.Single(h.EndedMessages);
    }

    [Fact]
    public async Task Folder_build_gets_the_stand_in_copied()
    {
        await using var h = new Harness();
        var folder = Path.Combine(h.Root, "Zoë Build", "monado build");
        var standInDir = Path.Combine(Harness.MakeBuild(folder), "service");
        var copy = Path.Combine(standInDir, "monado-service.exe");
        Directory.CreateDirectory(standInDir);
        File.WriteAllBytes(copy, [9, 9, 9]); // an older stand-in
        h.UseProfile(Harness.DreamAir with { Monado = new BuildSource(SourceKind.Folder, folder) });

        await h.StartRunningAsync();

        Assert.Equal(File.ReadAllBytes(h.StandInExe), File.ReadAllBytes(copy));
        Assert.Equal(File.ReadAllBytes(h.StandInExe), File.ReadAllBytes(Path.Combine(standInDir, "mirador-standin.exe")));
        var server = Path.Combine(folder, "src", "xrt", "targets", "service", "monado-service.exe");
        Assert.Equal(server, h.Launcher.Spec.FileName);
        Assert.Equal(Path.GetDirectoryName(server), h.Launcher.Spec.WorkingDirectory);
        Assert.DoesNotContain(h.Controller.Status, line => line.Key == "standin");

        // A build whose service folder is gone gets the folder and the stand-in again.
        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        Directory.Delete(standInDir, recursive: true);
        await h.Controller.StartAsync(StartReason.Button, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(File.ReadAllBytes(h.StandInExe), File.ReadAllBytes(copy));
    }

    [Fact]
    public async Task An_approved_build_runs_as_the_user_from_its_approved_copy()
    {
        var approvedDir = Path.Combine(Path.GetTempPath(), "Program Files", "Mirador", "builds", "S-1-5-21-1", "p1", "0123abc");
        var approved = new Mirador.Ipc.ApprovedBuild("S-1-5-21-1", "p1", "0123abc", approvedDir, new Dictionary<string, string>());
        await using var h = new Harness(approvedCopy: (_, _) => approved);
        var folder = Path.Combine(h.Root, "monado build");
        Harness.MakeBuild(folder);
        h.UseProfile(Harness.DreamAir with { Monado = new BuildSource(SourceKind.Folder, folder), RunAsAdministrator = false });

        await h.StartRunningAsync();

        Assert.Equal(Path.Combine(approvedDir, "bin", "monado-service.exe"), h.Launcher.Spec.FileName);
        Assert.Equal(Path.Combine(approvedDir, "bin"), h.Launcher.Spec.WorkingDirectory);
    }

    [Fact]
    public async Task The_stand_in_never_replaces_the_builds_own_service()
    {
        await using var h = new Harness();
        var folder = Path.Combine(h.Root, "odd build");
        var targets = Path.Combine(folder, "src", "xrt", "targets");
        var dll = Harness.Touch(targets, "openxr_monado.dll"); // so that service\ below the DLL is the real service's folder
        File.WriteAllText(Path.Combine(folder, "openxr_monado-dev.json"), Mirador.Core.Tests.Monado.TestManifest.Json(dll));
        var server = Harness.Touch(targets, "service", "monado-service.exe");
        File.WriteAllText(server, "the real service");
        h.UseProfile(Harness.DreamAir with { Monado = new BuildSource(SourceKind.Folder, folder) });

        await h.StartRunningAsync();

        Assert.Equal("the real service", File.ReadAllText(server));
        var line = Assert.Single(h.Controller.Status, l => l.Key == "standin");
        Assert.Equal(StatusLevel.Warning, line.Level);
        Assert.Contains(server, line.Text);
    }

    [Fact]
    public async Task The_stand_in_goes_below_the_selected_build_before_any_start()
    {
        await using var h = new Harness();
        var folder = Path.Combine(h.Root, "Zoë Build", "monado build");
        var copy = Path.Combine(Harness.MakeBuild(folder), "service", "monado-service.exe");
        h.UseProfile(Harness.DreamAir with { Monado = new BuildSource(SourceKind.Folder, folder) });

        h.Controller.PlaceStandIn();

        Assert.Equal(File.ReadAllBytes(h.StandInExe), File.ReadAllBytes(copy));
        Assert.Equal(0, h.Launcher.Starts);
    }

    [Fact]
    public async Task Placing_the_stand_in_leaves_the_bundled_monado_and_missing_builds_alone()
    {
        await using var h = new Harness();
        h.Controller.PlaceStandIn();
        Assert.False(Directory.Exists(Path.Combine(h.RuntimeDir, "service")));

        h.UseProfile(Harness.DreamAir with { Monado = new BuildSource(SourceKind.Folder, Path.Combine(h.Root, "gone")) });
        h.Controller.PlaceStandIn(); // no throw
    }

    [Theory]
    [InlineData("XRT_SERVICE_EXECUTABLE:UNINITIALIZED=monado-service\n", false)]
    [InlineData("XRT_SERVICE_EXECUTABLE:STRING=monado-service\n", false)]
    [InlineData("XRT_SERVICE_EXECUTABLE:STRING=mirador-standin\n", false)]
    [InlineData("XRT_SERVICE_EXECUTABLE:STRING=other-service\n", true)]
    [InlineData("CMAKE_BUILD_TYPE:STRING=RelWithDebInfo\n", true)]
    [InlineData("XRT_SERVICE_EXECUTABLE:UNINITIALIZED=\n", true)]
    public async Task A_build_that_doesnt_start_its_service_says_games_cant_start_it(string cache, bool warned)
    {
        await using var h = new Harness();
        var folder = Path.Combine(h.Root, "Zoë Build", "monado build");
        Harness.MakeBuild(folder);
        File.WriteAllText(Path.Combine(folder, "CMakeCache.txt"), "# This is the CMakeCache file.\n" + cache);
        h.UseProfile(Harness.DreamAir with { Monado = new BuildSource(SourceKind.Folder, folder) });

        await h.StartRunningAsync();

        var line = h.Controller.Status.SingleOrDefault(l => l.Key == "standin");
        Assert.Equal(warned, line is not null);
        if (line is not null)
        {
            Assert.Equal(StatusLevel.Warning, line.Level);
            Assert.Contains(ServiceLaunch.ConfigureOption, line.Text);
        }
    }

    [Fact]
    public async Task Missing_stand_in_is_a_status_line_not_a_refusal()
    {
        await using var h = new Harness();
        var folder = Path.Combine(h.Root, "Zoë Build", "monado build");
        Harness.MakeBuild(folder);
        h.UseProfile(Harness.DreamAir with { Monado = new BuildSource(SourceKind.Folder, folder) });
        File.Delete(h.StandInExe);

        await h.StartRunningAsync();

        Assert.Contains(
            new StatusLine("standin", "Games", StatusLevel.Info, "Games can't start Monado from this build: the stand-in is missing."),
            h.Controller.Status);
    }

    [Fact]
    public async Task Recover_hands_saved_data_to_the_module()
    {
        await using var h = new Harness();
        IReadOnlyDictionary<string, string> data = new Dictionary<string, string> { ["wasRunning"] = "true" };
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake", data);

        await h.Controller.RecoverAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(data, h.Module.Recovered);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task Recover_waits_while_a_monado_service_runs()
    {
        await using var h = new Harness();
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake", new Dictionary<string, string> { ["wasRunning"] = "true" });
        h.Processes.Add("monado-service", 1234, Harness.OtherMonado);

        await h.Controller.RecoverAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Null(h.Module.Recovered);
        Assert.True(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task Recover_goes_ahead_while_only_a_stand_in_runs()
    {
        await using var h = new Harness();
        IReadOnlyDictionary<string, string> data = new Dictionary<string, string> { ["wasRunning"] = "true" };
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake", data);
        h.Processes.Add("monado-service", 501, Harness.StandInImages + "monado-service.exe");

        await h.Controller.RecoverAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(data, h.Module.Recovered);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task Recover_without_a_session_file_does_nothing()
    {
        await using var h = new Harness();

        await h.Controller.RecoverAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Null(h.Module.Recovered);
        Assert.DoesNotContain("recover", h.Steps.All);
    }

    [Fact]
    public async Task Runtime_notice_when_monado_is_not_active()
    {
        await using var h = new Harness();
        h.Runtime.Active = @"C:\Other runtime\other_openxr.json";

        await h.StartRunningAsync();

        Assert.Equal("Monado (this profile) isn't the active OpenXR runtime, so games won't find it.", h.Controller.RuntimeNotice);

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        Assert.Null(h.Controller.RuntimeNotice);
    }

    [Fact]
    public async Task Status_follows_monado_and_its_log()
    {
        await using var h = new Harness();
        h.Module.UsesSteamVrLighthouse = true;
        Assert.Equal(new[] { MonadoLine(StatusLevel.Info, "Stopped") }, h.Controller.Status);

        await h.StartRunningAsync();
        var statusChanges = 0;
        h.Controller.StatusChanged += () => Interlocked.Increment(ref statusChanges);

        Assert.Equal(
            new[]
            {
                MonadoLine(StatusLevel.Ok, "Running (Dream Air)"),
                new StatusLine("apps", "Apps", StatusLevel.Info, "No apps"),
                new StatusLine("tracking", "Tracking", StatusLevel.Info, "Waiting for the headset"),
                new StatusLine("fake", "Headset", StatusLevel.Ok, "Ready"),
            },
            h.Controller.Status);

        string[] lines =
        [
            " INFO [client_connected] Client 1 connected",
            "\tapplication_name: 'hello_xr'",
            " INFO [client_connected] Client 2 connected",
            "\tapplication_name: 'VRChat'",
            " INFO [TrackedDeviceAdded] New device added: LHR-2A2E5F48",
            " INFO [TrackedDeviceAdded] Found lighthouse HMD: LHR-2A2E5F48",
            " INFO [TrackedDeviceAdded] Found lighthouse controller: LHR-AAAA0001",
        ];
        h.Launcher.Last!.Emit(lines);

        Assert.Equal(
            new[]
            {
                MonadoLine(StatusLevel.Ok, "Running (Dream Air)"),
                new StatusLine("apps", "Apps", StatusLevel.Ok, "hello_xr, VRChat"),
                new StatusLine("tracking", "Tracking", StatusLevel.Ok, "HMD LHR-2A2E5F48"),
                new StatusLine("fake", "Headset", StatusLevel.Ok, "Ready"),
            },
            h.Controller.Status);
        Assert.True(statusChanges >= 3, $"{statusChanges} status changes");
        Assert.Equal(lines, h.LogLines);
        Assert.Equal(lines, h.Controller.LogTail);
        Assert.Equal(lines, h.Module.Session!.Lines);
    }

    [Fact]
    public async Task Start_keeps_the_newest_10_monado_logs()
    {
        await using var h = new Harness();
        var logs = h.Paths.LogsDir;
        Directory.CreateDirectory(logs);
        string Old(int n) => Path.Combine(logs, $"monado-19990101-0000{n:00}.log");
        for (var n = 1; n <= 12; n++)
        {
            File.WriteAllText(Old(n), "old");
        }

        File.WriteAllText(Old(1) + ".1", "rolled");
        File.WriteAllText(Old(12) + ".1", "rolled");
        File.WriteAllText(Path.Combine(logs, "monado-19980101-000000.log.1"), "rolled, its log gone");
        File.WriteAllText(h.Paths.AppLogFile, "app");
        h.Launcher.FirstLines = ["hello"];

        await h.StartRunningAsync();

        var left = Directory.GetFiles(logs).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var expected = Enumerable.Range(4, 9).Select(n => Path.GetFileName(Old(n)))
            .Append(Path.GetFileName(Old(12)) + ".1")
            .Append(Path.GetFileName(h.MonadoLog))
            .Append("mirador.log")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, left);
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_is_one_warning()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.MonadoLog); // a folder where the file should be: every write fails
        h.Launcher.FirstLines = ["one"];

        await h.StartRunningAsync();

        // Heard for the line written before Mirador listened, then not again for the same failure.
        var warning = Assert.Single(h.Warnings);
        Assert.Equal("log", warning.Key);
        Assert.StartsWith("Mirador couldn't write Monado's log: ", warning.Text);
        h.Launcher.Last!.Emit("two", "three");
        Assert.Single(h.Warnings);
        Assert.Equal(new[] { "one", "two", "three" }, h.Module.Session!.Lines);
        Assert.Equal(new[] { "one", "two", "three" }, h.Controller.LogTail);
    }

    [Fact]
    public async Task A_module_that_fails_on_its_lines_is_one_warning_and_the_log_goes_on()
    {
        await using var h = new Harness();
        h.Module.SessionLineFailure = line => new InvalidOperationException($"Bad line {line}."); // a new message each time

        await h.StartRunningAsync();
        h.Launcher.Last!.Emit("one", "two", "three");

        Assert.Equal(
            new[] { new SessionWarning("module-log", "Mirador's Fake headset support couldn't read Monado's log: Bad line one.") },
            h.Warnings);
        Assert.Equal(new[] { "one", "two", "three" }, ReadWhileWritten(h.MonadoLog));
        Assert.Equal(new[] { "one", "two", "three" }, h.LogLines);
    }

    [Fact]
    public async Task Log_and_module_failures_are_one_warning_each_under_their_own_keys()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.MonadoLog); // every log write fails
        h.Module.SessionLineFailure = line => new InvalidOperationException($"Bad line {line}.");

        await h.StartRunningAsync();
        h.Launcher.Last!.Emit("one", "two", "three");

        Assert.Equal(new[] { "log", "module-log" }, h.Warnings.Select(w => w.Key).Order(StringComparer.Ordinal));
        Assert.StartsWith("Mirador couldn't write Monado's log: ", h.Warnings.Single(w => w.Key == "log").Text);
    }

    [Fact]
    public async Task A_module_that_fails_to_end_is_a_warning_and_its_session_file_stays()
    {
        await using var h = new Harness();
        h.Module.SessionEndFailure = new MiradorException("Mirador couldn't start the headset's software again.");
        await h.StartRunningAsync();

        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(new[] { new SessionWarning("headset", "Mirador couldn't start the headset's software again.") }, h.Warnings);
        Assert.Equal(new[] { "Stopped." }, h.EndedMessages);
        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.True(File.Exists(h.Paths.SessionFile)); // for the next launch's RecoverAsync
    }

    [Fact]
    public async Task A_start_first_undoes_a_session_that_failed_to_end()
    {
        await using var h = new Harness();
        h.Module.RecoveryData = WasRunning;
        h.Module.SessionEndFailure = new MiradorException("Mirador couldn't start the headset's software again.");
        await h.StartRunningAsync();
        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);

        // The headset's software is still stopped, so the next session would record that it wasn't running.
        h.Module.RecoveryData = WasNotRunning;
        h.Module.SessionEndFailure = null;
        await h.Controller.StartAsync(StartReason.Button, CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(WasRunning, h.Module.Recovered);
        var steps = h.Steps.All;
        Assert.True(Array.IndexOf(steps, "recover") < Array.LastIndexOf(steps, "before-start"), string.Join(", ", steps));
        Assert.Equal(WasNotRunning, new SessionFile(h.Paths.SessionFile).Read()?.Data);
    }

    [Fact]
    public async Task A_start_before_recovery_undoes_the_session_left_behind()
    {
        await using var h = new Harness();
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake", WasRunning); // Mirador was killed mid-session
        h.Module.RecoveryData = WasNotRunning;

        await h.StartRunningAsync(); // a game's START, served before the app called RecoverAsync

        Assert.Equal(WasRunning, h.Module.Recovered);
        Assert.Equal(new[] { "processes", "recover", "before-start", "launch", "pipe" }, h.Steps.All);
        Assert.Equal(WasNotRunning, new SessionFile(h.Paths.SessionFile).Read()?.Data);
    }

    [Fact]
    public async Task A_start_refuses_when_the_session_left_behind_cannot_be_undone()
    {
        await using var h = new Harness();
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake", WasRunning);
        h.Module.RecoverFailure = new MiradorException("Mirador's helper service isn't running.");

        var outcome = await Start(h).WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "Mirador's helper service isn't running."), outcome);
        Assert.Equal(0, h.Module.BeforeStartCalls);
        Assert.Equal(0, h.Launcher.Starts);
        Assert.Equal(WasRunning, new SessionFile(h.Paths.SessionFile).Read()?.Data);
    }

    [Fact]
    public async Task A_module_that_does_not_finish_stopping_is_given_60_seconds()
    {
        await using var h = new Harness();
        h.Module.SessionEndHangs = true;
        await h.StartRunningAsync();

        var stop = h.Controller.StopAsync(CancellationToken.None);
        await h.Module.Session!.HasEnded.Task.WaitAsync(Limit);
        await h.Timers.Set(TimeSpan.FromSeconds(60)).WaitAsync(Limit);
        h.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(stop.IsCompleted);
        Assert.Equal(SessionState.Stopping, h.Controller.State);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await stop.WaitAsync(Limit);

        Assert.Equal(SessionState.Stopped, h.Controller.State);
        Assert.Equal(new[] { "Stopped." }, h.EndedMessages);
        Assert.Equal(
            new[] { new SessionWarning("headset", "Fake headset didn't finish stopping within 60 s; Mirador tries again the next time it starts.") },
            h.Warnings);
        Assert.True(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task A_stop_breaks_off_a_start_that_waits_for_recovery()
    {
        await using var h = new Harness();
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake", WasRunning);
        h.Module.RecoverGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = h.Controller.RecoverAsync(CancellationToken.None);
        await h.Module.RecoverEntered.Task.WaitAsync(Limit);

        var start = Start(h);
        Assert.Equal(SessionState.Starting, h.Controller.State);
        await h.Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        var outcome = await start.WaitAsync(Limit);

        Assert.Equal(new StartOutcome(false, "Monado was stopped while it was starting."), outcome);
        Assert.False(recovery.IsCompleted);
        Assert.Equal(0, h.Module.BeforeStartCalls);
        Assert.Equal(0, h.Launcher.Starts);

        h.Module.RecoverGate.SetResult();
        await recovery.WaitAsync(Limit);
        Assert.Equal(WasRunning, h.Module.Recovered);
    }
}
