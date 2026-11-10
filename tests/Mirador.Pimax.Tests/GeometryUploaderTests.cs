using Microsoft.Extensions.Time.Testing;
using Mirador.Core;
using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Pimax.Tests;

public sealed class GeometryUploaderTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    // What the runtime writes on a good upload; the last two only as it ends (its stdout holds them back).
    private static readonly string[] Licence =
    [
        "Tobii platform runtime 1.21.0.0",
        "Opened device 'XR5DA-0123456789'",
        "validating licenses for XR5_PIMAX_DA_K11_RAWH",
    ];

    private static readonly string[] AtEnd =
    [
        "No local calibration found for the device, doing nothing",
        "tobii_xr_create successful",
    ];

    private readonly TempDir dir = new();
    private readonly TimerWatch time = new(new FakeTimeProvider());
    private readonly FakeUsbDevices usb = new(DreamAirIds.Board, DreamAirIds.EyeTracker, DreamAirIds.Receiver);
    private readonly FakeProcesses processes = new();
    private readonly FakeConsoleStarter starter = new(_ => new FakeConsole(Licence) { LinesAtEnter = AtEnd });
    private readonly PimaxInstall pimax;
    private readonly AppPaths paths;

    public GeometryUploaderTests()
    {
        var runtime = Path.Combine(dir.Path, "Pimax", "Runtime");
        pimax = new PimaxInstall(runtime, "2.0.4.329");
        Directory.CreateDirectory(Path.GetDirectoryName(pimax.BlobPath("K11_RAWH"))!);
        File.WriteAllText(pimax.TobiiRuntimeExe, "exe");
        File.WriteAllText(pimax.BlobPath("K11_RAWH"), "blob");
        paths = new AppPaths(Path.Combine(dir.Path, "Mirador"));
    }

    public void Dispose() => dir.Dispose();

    private GeometryUploader Uploader() => new(starter.Start, usb, processes, paths, time);

    private string RuntimeDir => Path.Combine(paths.GeometryDir, "runtime");

    private string LogPath => Path.Combine(paths.GeometryDir, "runtime.log");

    /// <summary>Runs an upload through its two waits: the moment after the licence check, and the end.</summary>
    private async Task<GeometryResult> UploadAsync(string variant = "K11_RAWH")
    {
        var upload = Uploader().UploadAsync(pimax, variant, CancellationToken.None);
        await time.Set(GeometryUploader.SendWait).WaitAsync(Limit);
        time.Clock.Advance(GeometryUploader.SendWait);
        return await upload.WaitAsync(Limit);
    }

    [Fact]
    public void Evaluate_reads_the_serial_and_the_created_session()
    {
        Assert.Equal((true, true, "XR5DA-0123456789"), GeometryUploader.Evaluate([.. Licence, .. AtEnd]));
    }

    [Fact]
    public void Evaluate_doesnt_need_the_licence_line()
    {
        Assert.Equal((true, true, "XR5DA-0123456789"), GeometryUploader.Evaluate(["Opened device 'XR5DA-0123456789'", "tobii_xr_create successful"]));
    }

    [Fact]
    public void Evaluate_without_the_created_line_is_not_an_upload()
    {
        Assert.Equal((true, false, "XR5DA-0123456789"), GeometryUploader.Evaluate(Licence));
    }

    [Fact]
    public void Evaluate_without_the_device_is_not_connected()
    {
        Assert.Equal((false, false, (string?)null), GeometryUploader.Evaluate(["validating licenses", "error: no device"]));
    }

    [Fact]
    public async Task Uploads_from_a_copy_of_the_runtime_and_presses_enter()
    {
        var result = await UploadAsync();

        Assert.Equal(new GeometryResult(true, "XR5DA-0123456789", "Geometry uploaded", LogPath), result);

        var spec = Assert.Single(starter.Specs);
        Assert.Equal(Path.Combine(RuntimeDir, PimaxNames.EyeRuntimeProcess + ".exe"), spec.FileName);
        Assert.Equal($"-x config_XR5PIMAXDA_K11_RAWH.bin \"{Path.Combine("..", "log")}\"", spec.Arguments);
        Assert.Equal(RuntimeDir, spec.WorkingDirectory);
        Assert.Equal("exe", File.ReadAllText(spec.FileName));
        Assert.Equal("blob", File.ReadAllText(Path.Combine(RuntimeDir, "config_XR5PIMAXDA_K11_RAWH.bin")));
        Assert.True(Directory.Exists(Path.Combine(paths.GeometryDir, "log")));

        var runtime = starter.Last!;
        Assert.Equal([""], runtime.Written);
        Assert.True(runtime.InputClosed);
        Assert.False(runtime.Killed);
        Assert.True(runtime.Disposed);
        Assert.Equal([.. Licence, .. AtEnd], File.ReadAllLines(LogPath));
    }

    [Fact]
    public async Task Enter_waits_two_seconds_after_the_licence_check()
    {
        var upload = Uploader().UploadAsync(pimax, "K11_RAWH", CancellationToken.None);
        await time.Set(GeometryUploader.SendWait).WaitAsync(Limit);

        time.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Empty(starter.Last!.Written);

        time.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await upload.WaitAsync(Limit);
        Assert.Equal([""], starter.Last.Written);
    }

    [Fact]
    public async Task No_eye_tracker_on_usb_refuses_without_running_anything()
    {
        usb.Ids.Remove(DreamAirIds.EyeTracker);

        var result = await Uploader().UploadAsync(pimax, "K11_RAWH", CancellationToken.None);

        Assert.Equal(new GeometryResult(false, null, "The eye tracker isn't on USB. Is the headset connected and switched on?", LogPath), result);
        Assert.Empty(starter.Specs);
    }

    [Theory]
    [InlineData("pi_server")]
    [InlineData("vrss_gaze_provider")]
    [InlineData("platform_runtime_XR5EYECHIP_WIN10_x64")] // Pimax's own copy of the runtime
    public async Task A_program_holding_the_tracker_refuses(string holder)
    {
        processes.Add(holder);

        var result = await Uploader().UploadAsync(pimax, "K11_RAWH", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal($"The eye geometry wasn't uploaded, so gaze will be about 10° off: {holder} is running and holds the eye tracker.", result.Message);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task Mirador_s_stand_in_doesnt_count_as_a_holder()
    {
        // A game's Start: the stand-in, named monado-service, waits for Mirador while the module uploads.
        processes.Add("monado-service");

        Assert.True((await UploadAsync()).Ok);
    }

    [Fact]
    public async Task An_unknown_variant_is_a_failure_not_a_throw()
    {
        var result = await Uploader().UploadAsync(pimax, "K12_RAWH", CancellationToken.None);

        Assert.Equal(
            new GeometryResult(false, null, "The eye geometry wasn't uploaded, so gaze will be about 10° off: Mirador doesn't know the eye geometry variant K12_RAWH.", LogPath),
            result);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task Usb_devices_that_cant_be_listed_say_the_geometry_wasnt_uploaded()
    {
        var failing = new FailingUsb(new MiradorException("Windows couldn't list the USB devices (busy), so Mirador can't tell which headset is plugged in. Try again."));

        var result = await new GeometryUploader(starter.Start, failing, processes, paths, time).UploadAsync(pimax, "K11_RAWH", CancellationToken.None);

        Assert.Equal(
            "The eye geometry wasn't uploaded, so gaze will be about 10° off: Windows couldn't list the USB devices (busy), so Mirador can't tell which headset is plugged in. Try again.",
            result.Message);
    }

    [Fact]
    public async Task A_runtime_that_ends_after_its_licence_check_isnt_waited_for()
    {
        var starter = new FakeConsoleStarter(_ =>
        {
            var quick = new FakeConsole([.. Licence, .. AtEnd]);
            quick.End(0);
            return quick;
        });

        var result = await new GeometryUploader(starter.Start, usb, processes, paths, time)
            .UploadAsync(pimax, "K11_RAWH", CancellationToken.None).WaitAsync(Limit);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task No_created_session_is_a_failure_pointing_at_the_log()
    {
        var starter = new FakeConsoleStarter(_ => new FakeConsole(Licence) { LinesAtEnter = ["licence check failed"] });
        var upload = new GeometryUploader(starter.Start, usb, processes, paths, time).UploadAsync(pimax, "K11_RAWH", CancellationToken.None);
        await time.Set(GeometryUploader.SendWait).WaitAsync(Limit);
        time.Clock.Advance(GeometryUploader.SendWait);

        var result = await upload.WaitAsync(Limit);

        Assert.Equal(
            new GeometryResult(false, "XR5DA-0123456789", $"The eye geometry wasn't uploaded, so gaze will be about 10° off. Details: {LogPath}", LogPath),
            result);
        Assert.Contains("licence check failed", File.ReadAllLines(LogPath));
    }

    [Fact]
    public async Task Without_a_licence_line_enter_comes_after_30_seconds()
    {
        var starter = new FakeConsoleStarter(_ => new FakeConsole("Opened device 'XR5DA-0123456789'") { LinesAtEnter = AtEnd });
        var upload = new GeometryUploader(starter.Start, usb, processes, paths, time).UploadAsync(pimax, "K11_RAWH", CancellationToken.None);
        await time.Set(GeometryUploader.LicenceWait).WaitAsync(Limit);

        time.Clock.Advance(GeometryUploader.LicenceWait);
        var result = await upload.WaitAsync(Limit);

        Assert.True(result.Ok);
        Assert.Equal([""], starter.Last!.Written);
        Assert.Contains("Mirador: No licence check within 30 s.", File.ReadAllLines(LogPath));
    }

    [Fact]
    public async Task A_runtime_that_ends_early_isnt_waited_for()
    {
        var starter = new FakeConsoleStarter(_ =>
        {
            var crashed = new FakeConsole("error: device busy");
            crashed.End(1);
            return crashed;
        });

        var result = await new GeometryUploader(starter.Start, usb, processes, paths, time)
            .UploadAsync(pimax, "K11_RAWH", CancellationToken.None).WaitAsync(Limit);

        Assert.False(result.Ok);
        Assert.Equal(["error: device busy"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public async Task A_runtime_that_doesnt_stop_is_killed_after_15_seconds()
    {
        // Its stdout holds back the created line until it ends, so a killed runtime never says it.
        var starter = new FakeConsoleStarter(_ => new FakeConsole(Licence) { ExitCodeAtEnter = null });
        var upload = new GeometryUploader(starter.Start, usb, processes, paths, time).UploadAsync(pimax, "K11_RAWH", CancellationToken.None);
        await time.Set(GeometryUploader.SendWait).WaitAsync(Limit);
        time.Clock.Advance(GeometryUploader.SendWait);
        await time.Set(GeometryUploader.ExitWait).WaitAsync(Limit);

        time.Clock.Advance(GeometryUploader.ExitWait);
        var result = await upload.WaitAsync(Limit);

        Assert.True(starter.Last!.Killed);
        Assert.False(result.Ok);
        Assert.Contains("Mirador: Didn't stop within 15 s of ENTER; ended it.", File.ReadAllLines(LogPath));
    }

    [Fact]
    public async Task A_runtime_that_wont_start_is_a_failure_with_the_reason_in_the_log()
    {
        starter.Refusal = new MiradorException("Couldn't start platform_runtime_XR5EYECHIP_WIN10_x64.exe: Access is denied.");

        var result = await Uploader().UploadAsync(pimax, "K11_RAWH", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(["Mirador: Couldn't start platform_runtime_XR5EYECHIP_WIN10_x64.exe: Access is denied."], File.ReadAllLines(LogPath));
    }

    [Fact]
    public async Task A_missing_geometry_file_is_a_failure_not_a_throw()
    {
        File.Delete(pimax.BlobPath("K11_RAWH"));

        var result = await Uploader().UploadAsync(pimax, "K11_RAWH", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(starter.Specs);
        Assert.Contains("config_XR5PIMAXDA_K11_RAWH.bin", File.ReadAllText(LogPath));
    }

    [Fact]
    public async Task Cancelling_ends_the_runtime_and_throws()
    {
        using var cts = new CancellationTokenSource();
        var upload = Uploader().UploadAsync(pimax, "K11_RAWH", cts.Token);
        await time.Set(GeometryUploader.SendWait).WaitAsync(Limit);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload.WaitAsync(Limit));
        Assert.True(starter.Last!.Killed);
        Assert.True(File.Exists(LogPath));
    }

    private sealed class FailingUsb(MiradorException failure) : Core.Modules.IUsbDevices
    {
        public IReadOnlySet<Core.Modules.UsbId> Present() => throw failure;
    }
}
