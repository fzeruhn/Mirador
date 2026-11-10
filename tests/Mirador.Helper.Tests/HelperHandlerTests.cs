using System.Security.Principal;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public sealed class HelperHandlerTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly FakeServiceControl services = new();
    private readonly FakeProcessControl processes = new();
    private readonly FakeRuntimeRegistry registry = new();

    public void Dispose() => temp.Dispose();

    private string BundledManifest => temp.File(@"monado\runtime\openxr_monado.json");

    private HelperHandler Handler(bool serviceRunning = false, bool bundleExists = true, string? version = null)
    {
        services.Running = serviceRunning;
        if (bundleExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BundledManifest)!);
            File.WriteAllText(BundledManifest, "{}");
        }

        return new HelperHandler(services, processes, registry, BundledManifest, version);
    }

    private static HelperRequest Request(HelperCommand command) => new(command);

    // Ping

    [Fact]
    public async Task Ping_returns_the_helper_version()
    {
        var reply = await Handler(version: "1.2.3").HandleAsync(Request(HelperCommand.Ping));

        Assert.True(reply.Ok);
        Assert.Equal("1.2.3", reply.Value);
    }

    [Fact]
    public async Task Get_install_folder_answers_the_helpers_install_folder()
    {
        var handler = new HelperHandler(services, processes, registry, BundledManifest, installDir: @"D:\Portable\Mirador");

        var reply = await handler.HandleAsync(Request(HelperCommand.GetInstallFolder));

        Assert.True(reply.Ok);
        Assert.Equal(@"D:\Portable\Mirador", reply.Value);

        // By default the folder above the helper's own, the folder the elevated start checks the caller against.
        var own = await Handler().HandleAsync(Request(HelperCommand.GetInstallFolder));
        Assert.Equal(HelperHandler.DefaultInstallDir, own.Value);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), own.Value);
    }

    [Fact]
    public async Task Ping_without_a_given_version_reports_the_assembly_version()
    {
        var reply = await Handler().HandleAsync(Request(HelperCommand.Ping));

        Assert.True(reply.Ok);
        Assert.False(string.IsNullOrWhiteSpace(reply.Value));
    }

    [Fact]
    public async Task Ping_answers_while_another_request_is_still_running()
    {
        using var gate = new ManualResetEventSlim();
        services.StopGate = gate;
        var handler = Handler(serviceRunning: true, version: "1.2.3");
        var stopping = handler.HandleAsync(Request(HelperCommand.StopPimax));
        Assert.True(services.StopEntered.Wait(TimeSpan.FromSeconds(5)), "StopPimax never started");

        var ping = handler.HandleAsync(Request(HelperCommand.Ping));

        var finished = await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(3)));
        gate.Set();
        Assert.True(finished == ping, "Ping waited for the running request");
        Assert.Equal(new HelperReply(true, Value: "1.2.3"), await ping);
        Assert.True((await stopping).Ok);
    }

    // StopPimax / StartPimax

    [Fact]
    public async Task StopPimax_reports_was_running_and_kills_the_fixed_list()
    {
        var r = await Handler(serviceRunning: true).HandleAsync(new(HelperCommand.StopPimax));

        Assert.True(r.Ok);
        Assert.True(r.WasRunning);
        Assert.Equal(["PiServiceLauncher"], services.Stopped);
        Assert.Equal([TimeSpan.FromSeconds(20)], services.StopTimeouts);
        Assert.Equal(PimaxNames.Processes, processes.Killed);
    }

    [Fact]
    public async Task StopPimax_with_a_stopped_service_still_ends_the_processes()
    {
        var r = await Handler(serviceRunning: false).HandleAsync(Request(HelperCommand.StopPimax));

        Assert.True(r.Ok);
        Assert.False(r.WasRunning);
        Assert.Empty(services.Stopped);
        Assert.Equal(PimaxNames.Processes, processes.Killed);
    }

    [Fact]
    public async Task StopPimax_without_pimax_is_ok()
    {
        services.Installed = false;

        var r = await Handler().HandleAsync(Request(HelperCommand.StopPimax));

        Assert.True(r.Ok);
        Assert.False(r.WasRunning);
        Assert.Empty(services.Stopped);
        Assert.Equal(PimaxNames.Processes, processes.Killed);
    }

    [Fact]
    public async Task StopPimax_that_cannot_stop_the_service_still_ends_the_processes_and_reports_why()
    {
        services.StopFails = new InvalidOperationException("The service PiServiceLauncher didn't stop within 20 seconds.");

        var r = await Handler(serviceRunning: true).HandleAsync(Request(HelperCommand.StopPimax));

        Assert.Equal(new HelperReply(false, "The service PiServiceLauncher didn't stop within 20 seconds.", WasRunning: true), r);
        Assert.Equal(PimaxNames.Processes, processes.Killed);
    }

    [Fact]
    public async Task StopPimax_that_cannot_end_a_process_reports_why_after_stopping_the_service()
    {
        processes.KillFails = new InvalidOperationException("Windows wouldn't end pi_server: Access is denied.");

        var r = await Handler(serviceRunning: true).HandleAsync(Request(HelperCommand.StopPimax));

        Assert.Equal(new HelperReply(false, "Windows wouldn't end pi_server: Access is denied.", WasRunning: true), r);
        Assert.Equal(["PiServiceLauncher"], services.Stopped);
    }

    [Fact]
    public async Task StopPimax_that_fails_twice_reports_the_first_failure()
    {
        services.StopFails = new InvalidOperationException("stop failed");
        processes.KillFails = new InvalidOperationException("kill failed");

        var r = await Handler(serviceRunning: true).HandleAsync(Request(HelperCommand.StopPimax));

        Assert.Equal(new HelperReply(false, "stop failed", WasRunning: true), r);
        Assert.Equal(PimaxNames.Processes, processes.Killed);
    }

    [Fact]
    public async Task StartPimax_starts_a_stopped_service()
    {
        var r = await Handler(serviceRunning: false).HandleAsync(Request(HelperCommand.StartPimax));

        Assert.True(r.Ok);
        Assert.Equal(["PiServiceLauncher"], services.Started);
    }

    [Fact]
    public async Task StartPimax_leaves_a_running_service_alone()
    {
        var r = await Handler(serviceRunning: true).HandleAsync(Request(HelperCommand.StartPimax));

        Assert.True(r.Ok);
        Assert.Empty(services.Started);
    }

    [Fact]
    public async Task StartPimax_starts_a_service_that_is_still_stopping_after_giving_it_time_to_stop()
    {
        // A StopPimax whose 20 s ran out leaves the service stopping. Left alone it would end up stopped for good.
        var handler = Handler(serviceRunning: false);
        services.Stopping = true;

        var r = await handler.HandleAsync(Request(HelperCommand.StartPimax));

        Assert.True(r.Ok);
        Assert.Equal(["PiServiceLauncher"], services.Started);
        Assert.Equal([TimeSpan.FromSeconds(20)], services.StartTimeouts);
    }

    [Fact]
    public async Task StartPimax_without_pimax_is_ok()
    {
        services.Installed = false;

        var r = await Handler().HandleAsync(Request(HelperCommand.StartPimax));

        Assert.True(r.Ok);
        Assert.Empty(services.Started);
    }

    // The OpenXR runtime

    [Fact]
    public async Task SetRuntimeBundled_records_the_previous_runtime_once()
    {
        registry.Active = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";
        var handler = Handler();

        var first = await handler.HandleAsync(Request(HelperCommand.SetRuntimeBundled));
        var second = await handler.HandleAsync(Request(HelperCommand.SetRuntimeBundled));

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(BundledManifest, registry.Active);
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Recorded);
    }

    [Fact]
    public async Task SetRuntimeBundled_with_another_runtime_back_records_that_one()
    {
        registry.Active = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";
        var handler = Handler();
        await handler.HandleAsync(Request(HelperCommand.SetRuntimeBundled));
        registry.Active = @"C:\Program Files\SteamVR\steamxr_win64.json";

        await handler.HandleAsync(Request(HelperCommand.SetRuntimeBundled));

        Assert.Equal(@"C:\Program Files\SteamVR\steamxr_win64.json", registry.Recorded);
    }

    [Fact]
    public async Task SetRuntimeBundled_with_no_active_runtime_records_nothing()
    {
        registry.Active = null;
        registry.Recorded = @"C:\older.json";

        var r = await Handler().HandleAsync(Request(HelperCommand.SetRuntimeBundled));

        Assert.True(r.Ok);
        Assert.Equal(BundledManifest, registry.Active);
        Assert.Equal(@"C:\older.json", registry.Recorded);
    }

    [Fact]
    public async Task SetRuntimeBundled_knows_the_bundle_by_its_full_path_ignoring_case()
    {
        var directory = Path.GetDirectoryName(BundledManifest)!;
        registry.Active = Path.Combine(directory, "..", "runtime", "OPENXR_MONADO.JSON").ToUpperInvariant();

        var r = await Handler().HandleAsync(Request(HelperCommand.SetRuntimeBundled));

        Assert.True(r.Ok);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task SetRuntimeBundled_without_bundle_fails()
    {
        registry.Active = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        var r = await Handler(bundleExists: false).HandleAsync(Request(HelperCommand.SetRuntimeBundled));

        Assert.False(r.Ok);
        Assert.Equal("The bundled Monado isn't installed yet. Install it on the Profiles page.", r.Error);
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
        Assert.Null(registry.Recorded);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task RestoreRuntime_puts_back_and_forgets()
    {
        registry.Active = BundledManifest;
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        var r = await Handler().RestoreRuntimeAsync();

        Assert.True(r.Ok);
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task RestoreRuntime_leaves_a_runtime_that_replaced_the_bundled_one_and_forgets()
    {
        registry.Active = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json";
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        var r = await Handler().RestoreRuntimeAsync();

        Assert.True(r.Ok);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json", registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task RestoreRuntime_with_no_active_runtime_forgets_without_setting_one()
    {
        registry.Active = null;
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        var r = await Handler().RestoreRuntimeAsync();

        Assert.True(r.Ok);
        Assert.Null(registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task RestoreRuntime_knows_the_bundle_by_its_full_path_ignoring_case()
    {
        registry.Active = BundledManifest.ToUpperInvariant();
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        await Handler().RestoreRuntimeAsync();

        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
    }

    [Fact]
    public async Task RestoreRuntime_with_nothing_recorded_is_ok_and_changes_nothing()
    {
        registry.Active = @"C:\somewhere\runtime.json";

        var r = await Handler().RestoreRuntimeAsync();

        Assert.True(r.Ok);
        Assert.Equal(@"C:\somewhere\runtime.json", registry.Active);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task Set_then_restore_leaves_the_registry_as_it_was()
    {
        registry.Active = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";
        var handler = Handler();

        await handler.HandleAsync(Request(HelperCommand.SetRuntimeBundled));
        await handler.RestoreRuntimeAsync();

        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
        Assert.Null(registry.Recorded);
    }

    // GetPimaxEyeBlob

    /// <summary>Pimax's runtime folder, as a full path on whichever system runs the tests.</summary>
    internal static readonly string PimaxRuntime = Path.GetFullPath("/Program Files/Pimax/Runtime");

    internal static readonly string EyeRuntimePath =
        Path.Combine(PimaxRuntime, "EyeTrackingServer", "platform_runtime", PimaxNames.EyeRuntimeProcess + ".exe");

    private static readonly string EyeRuntimeExe = $"\"{EyeRuntimePath}\"";

    [Fact]
    public async Task GetPimaxEyeBlob_returns_the_variant_of_the_eye_runtime()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] =
            [EyeRuntimeExe + " -x \"C:\\Program Files\\Pimax\\Runtime\\EyeTrackingServer\\platform_runtime\\config\\config_XR5PIMAXDA_K11_RAWH.bin\" \"C:\\log\""];

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.True(r.Ok);
        Assert.Equal("K11_RAWH", r.Value);
        Assert.Equal([PimaxNames.EyeRuntimeProcess], processes.Queried);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_skips_command_lines_without_a_known_variant()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] =
        [
            EyeRuntimeExe,
            EyeRuntimeExe + " -x config_XR5PIMAXDA_K12_RAWH.bin",
            EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin",
        ];

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.True(r.Ok);
        Assert.Equal("K10_RAYH", r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_answers_nothing_when_runtimes_disagree()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] =
        [
            EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin",
            EyeRuntimeExe + " -x config_XR5PIMAXDA_K11_RAWH.bin",
        ];

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.True(r.Ok);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_ignores_a_runtime_that_pimaxs_service_didnt_start()
    {
        // Pimax's own program file, started by someone else with a command line of their choosing.
        processes.Lines[PimaxNames.EyeRuntimeProcess] = [EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin"];
        processes.ParentId = 4242;

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.Null(r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_ignores_a_runtime_older_than_pimaxs_service()
    {
        // Its parent id was the service's before: a process id Windows gave out again.
        processes.Lines[PimaxNames.EyeRuntimeProcess] = [EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin"];
        processes.Started = new DateTime(2026, 11, 4, 9, 0, 0);

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.Null(r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_without_pimaxs_service_running_reads_nothing()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] = [EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin"];
        services.PimaxProcess = null;

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.Null(r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_ignores_the_runtime_in_another_folder_of_pimaxs()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] = [EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin"];
        processes.ExecutablePath = Path.Combine(PimaxRuntime, "Downloads", PimaxNames.EyeRuntimeProcess + ".exe");

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.Null(r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_ignores_a_program_named_like_the_runtime_outside_pimaxs_folder()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] = [EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin"];
        processes.ExecutablePath = Path.Combine(Path.GetFullPath("/Users/someone/Downloads"), PimaxNames.EyeRuntimeProcess + ".exe");

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.True(r.Ok);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_ignores_a_folder_that_only_starts_like_pimaxs()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] = [EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin"];
        processes.ExecutablePath = Path.Combine(PimaxRuntime + "Evil", PimaxNames.EyeRuntimeProcess + ".exe");

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.Null(r.Value);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_without_pimaxs_service_reads_nothing()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] = [EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin"];
        services.PimaxImagePath = null;

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.True(r.Ok);
        Assert.Null(r.Value);
        Assert.Empty(processes.Queried);
    }

    [Fact]
    public async Task GetPimaxEyeBlob_without_the_eye_runtime_is_ok_with_no_value()
    {
        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.True(r.Ok);
        Assert.Null(r.Value);
    }

    // Failures

    [Fact]
    public async Task Exceptions_become_error_replies()
    {
        registry.Fails = new UnauthorizedAccessException("Access to the registry key is denied.");
        registry.Active = @"C:\x.json";

        var r = await Handler().HandleAsync(Request(HelperCommand.SetRuntimeBundled));

        Assert.False(r.Ok);
        Assert.Equal("Access to the registry key is denied.", r.Error);
    }

    [Fact]
    public async Task A_very_long_error_message_is_cut_so_that_the_reply_fits_one_line()
    {
        services.StopFails = new InvalidOperationException(new string('x', 10_000));

        var r = await Handler(serviceRunning: true).HandleAsync(Request(HelperCommand.StopPimax));

        Assert.False(r.Ok);
        Assert.InRange(r.Error!.Length, 1, 1000);
        Assert.StartsWith("xxx", r.Error);
    }

    [Fact]
    public async Task A_very_long_error_message_outside_ascii_still_fits_one_line()
    {
        // JSON writes each of these as six bytes (\u00E9), so a cut by characters alone could overflow the line.
        services.StopFails = new InvalidOperationException(new string('\u00e9', 10_000));

        var r = await Handler(serviceRunning: true).HandleAsync(Request(HelperCommand.StopPimax));

        using var line = new MemoryStream();
        await LineCodec.WriteAsync(line, r, CancellationToken.None);
        Assert.InRange(line.Length, 1, LineCodec.MaxLineBytes + 1);
        Assert.StartsWith("\u00e9\u00e9", r.Error);
    }

    [Fact]
    public async Task A_command_outside_the_list_is_refused_and_does_nothing()
    {
        var r = await Handler(serviceRunning: true).HandleAsync(Request((HelperCommand)99));

        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Empty(services.Stopped);
        Assert.Empty(services.Started);
        Assert.Empty(processes.Killed);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task Requests_are_handled_one_at_a_time()
    {
        // The helper changes machine-wide state: two requests must never be inside the registry code together.
        registry.Active = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";
        var handler = Handler();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => handler.HandleAsync(Request(HelperCommand.SetRuntimeBundled))));

        Assert.Equal(1, registry.MaxConcurrent);
        Assert.Equal(BundledManifest, registry.Active);
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Recorded);
    }

    // RevokeElevatedMonado

    private static readonly SecurityIdentifier Alice = new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier Bob = new("S-1-5-21-1-2-3-1002");

    private static CallerInfo Caller(SecurityIdentifier sid) => new(sid, 1, 4242, @"C:\Program Files\Mirador\Mirador.exe");

    private HelperHandler ConsentHandler(FakeConsentValues values)
    {
        Handler(); // writes the bundled manifest
        return new HelperHandler(services, processes, registry, BundledManifest, consent: new ElevatedConsent(values));
    }

    [Fact]
    public async Task Revoke_uses_the_callers_sid()
    {
        var values = new FakeConsentValues();
        values.Sids.Add(Alice.Value);
        values.Sids.Add(Bob.Value);
        values.FileLines[Alice.Value] = ["ArcturusPerception.dll=" + new string('a', 64)];
        values.FileLines[Bob.Value] = ["ArcturusPerception.dll=" + new string('b', 64)];

        // The request is only a command: there is no way to put another SID in it.
        var reply = await ConsentHandler(values).HandleAsync(Request(HelperCommand.RevokeElevatedMonado), Caller(Alice));

        Assert.True(reply.Ok);
        Assert.Equal([Bob.Value], values.Sids);

        // The approved SteamVR files go with the consent, and only the caller's.
        Assert.Equal([Bob.Value], values.FileLines.Keys);
    }

    [Fact]
    public async Task Revoke_without_a_known_caller_deletes_nothing_and_says_so()
    {
        var values = new FakeConsentValues();
        values.Sids.Add(Alice.Value);

        var reply = await ConsentHandler(values).HandleAsync(Request(HelperCommand.RevokeElevatedMonado), caller: null);

        Assert.False(reply.Ok);
        Assert.False(string.IsNullOrWhiteSpace(reply.Error));
        Assert.Equal([Alice.Value], values.Sids);
    }

    [Fact]
    public async Task Revoke_that_cannot_delete_reports_why()
    {
        var values = new FakeConsentValues { Fails = new UnauthorizedAccessException("Access is denied.") };

        var reply = await ConsentHandler(values).HandleAsync(Request(HelperCommand.RevokeElevatedMonado), Caller(Alice));

        Assert.False(reply.Ok);
        Assert.Contains("Access is denied", reply.Error);
    }

    [Fact]
    public async Task Revoke_of_a_user_who_never_consented_is_fine()
    {
        var reply = await ConsentHandler(new FakeConsentValues()).HandleAsync(Request(HelperCommand.RevokeElevatedMonado), Caller(Alice));

        Assert.True(reply.Ok);
    }

    // StartMonadoElevated / StopMonadoElevated / WaitMonadoElevated

    private static readonly ElevatedStart Start = new(3000, 100, 6, false, "dreamair");

    private HelperHandler ElevatedHandler(FakeElevated elevated)
    {
        Handler(); // writes the bundled manifest
        return new HelperHandler(services, processes, registry, BundledManifest, elevated: elevated);
    }

    [Fact]
    public async Task Start_elevated_passes_the_caller_and_settings_to_the_runner()
    {
        var elevated = new FakeElevated { StartReply = new HelperReply(true, Value: "9001") };

        var reply = await ElevatedHandler(elevated).HandleAsync(new HelperRequest(HelperCommand.StartMonadoElevated, Start), Caller(Alice));

        Assert.Equal("9001", reply.Value);
        Assert.Equal(Start, elevated.StartedWith);
        Assert.Equal(Alice, elevated.StartCaller?.Sid);
    }

    [Fact]
    public async Task Start_elevated_without_a_caller_changes_nothing()
    {
        var elevated = new FakeElevated();

        var reply = await ElevatedHandler(elevated).HandleAsync(new HelperRequest(HelperCommand.StartMonadoElevated, Start), caller: null);

        Assert.False(reply.Ok);
        Assert.Null(elevated.StartedWith);
    }

    [Fact]
    public async Task Start_elevated_without_the_settings_is_refused()
    {
        var elevated = new FakeElevated();

        var reply = await ElevatedHandler(elevated).HandleAsync(new HelperRequest(HelperCommand.StartMonadoElevated), Caller(Alice));

        Assert.False(reply.Ok);
        Assert.Null(elevated.StartedWith);
    }

    [Fact]
    public async Task Start_elevated_without_a_runner_is_refused()
    {
        var reply = await Handler().HandleAsync(new HelperRequest(HelperCommand.StartMonadoElevated, Start), Caller(Alice));

        Assert.False(reply.Ok);
        Assert.False(string.IsNullOrWhiteSpace(reply.Error));
    }

    [Fact]
    public async Task Stop_elevated_routes_to_the_runner()
    {
        var elevated = new FakeElevated();

        var reply = await ElevatedHandler(elevated).HandleAsync(Request(HelperCommand.StopMonadoElevated), Caller(Alice));

        Assert.True(reply.Ok);
        Assert.Equal(Alice, elevated.StopCaller?.Sid);
    }

    [Fact]
    public async Task Wait_elevated_returns_the_runners_answer()
    {
        var elevated = new FakeElevated { WaitReply = new HelperReply(true, Value: "5") };

        var reply = await ElevatedHandler(elevated).HandleAsync(Request(HelperCommand.WaitMonadoElevated), Caller(Alice));

        Assert.Equal("5", reply.Value);
        Assert.Equal(Alice, elevated.WaitCaller?.Sid);
    }

    private sealed class FakeElevated : Mirador.Helper.ElevatedMonado.IElevatedMonado
    {
        public ElevatedStart? StartedWith { get; private set; }
        public CallerInfo? StartCaller { get; private set; }
        public CallerInfo? StopCaller { get; private set; }
        public CallerInfo? WaitCaller { get; private set; }
        public HelperReply StartReply { get; set; } = new(true, Value: "1");
        public HelperReply WaitReply { get; set; } = new(true);

        public HelperReply Start(ElevatedStart start, CallerInfo caller)
        {
            StartedWith = start;
            StartCaller = caller;
            return StartReply;
        }

        public HelperReply Stop(CallerInfo caller)
        {
            StopCaller = caller;
            return new HelperReply(true);
        }

        public HelperReply Wait(CallerInfo caller)
        {
            WaitCaller = caller;
            return WaitReply;
        }
    }
}
