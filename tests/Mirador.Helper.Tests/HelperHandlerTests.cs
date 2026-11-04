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
        Assert.Equal("This Mirador has no bundled Monado.", r.Error);
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
        Assert.Null(registry.Recorded);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task RestoreRuntime_puts_back_and_forgets()
    {
        registry.Active = BundledManifest;
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        var r = await Handler().HandleAsync(Request(HelperCommand.RestoreRuntime));

        Assert.True(r.Ok);
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task RestoreRuntime_leaves_a_runtime_that_replaced_the_bundled_one_and_forgets()
    {
        registry.Active = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json";
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        var r = await Handler().HandleAsync(Request(HelperCommand.RestoreRuntime));

        Assert.True(r.Ok);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json", registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task RestoreRuntime_with_no_active_runtime_forgets_without_setting_one()
    {
        registry.Active = null;
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        var r = await Handler().HandleAsync(Request(HelperCommand.RestoreRuntime));

        Assert.True(r.Ok);
        Assert.Null(registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task RestoreRuntime_knows_the_bundle_by_its_full_path_ignoring_case()
    {
        registry.Active = BundledManifest.ToUpperInvariant();
        registry.Recorded = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

        await Handler().HandleAsync(Request(HelperCommand.RestoreRuntime));

        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
    }

    [Fact]
    public async Task RestoreRuntime_with_nothing_recorded_is_ok_and_changes_nothing()
    {
        registry.Active = @"C:\somewhere\runtime.json";

        var r = await Handler().HandleAsync(Request(HelperCommand.RestoreRuntime));

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
        await handler.HandleAsync(Request(HelperCommand.RestoreRuntime));

        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.Active);
        Assert.Null(registry.Recorded);
    }

    // GetPimaxEyeBlob

    private const string EyeRuntimeExe =
        "\"C:\\Program Files\\Pimax\\Runtime\\EyeTrackingServer\\platform_runtime\\platform_runtime_XR5EYECHIP_WIN10_x64.exe\"";

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
    public async Task GetPimaxEyeBlob_takes_the_first_command_line_that_has_a_known_variant()
    {
        processes.Lines[PimaxNames.EyeRuntimeProcess] =
        [
            EyeRuntimeExe,
            EyeRuntimeExe + " -x config_XR5PIMAXDA_K12_RAWH.bin",
            EyeRuntimeExe + " -x config_XR5PIMAXDA_K10_RAYH.bin",
            EyeRuntimeExe + " -x config_XR5PIMAXDA_K11_RAWH.bin",
        ];

        var r = await Handler().HandleAsync(Request(HelperCommand.GetPimaxEyeBlob));

        Assert.True(r.Ok);
        Assert.Equal("K10_RAYH", r.Value);
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
}
