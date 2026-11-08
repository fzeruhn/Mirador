using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Ipc;

namespace Mirador.DreamAir.Tests;

public class DreamAirChecksTests
{
    private const string ServiceKey = @"HKLM\SYSTEM\CurrentControlSet\Services\PiServiceLauncher";
    private const string Uninstall = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PimaxPlay";

    // Full paths on whichever system runs the tests (C:\… on Windows): the checks only ask whether files are there,
    // and PimaxInstallTests covers reading Windows paths from the registry.
    private static readonly string Runtime = Path.GetFullPath("/Program Files/Pimax/Runtime");
    private static readonly string SteamVrDir = Path.GetFullPath("/Steam/steamapps/common/SteamVR");

    private static readonly PimaxInstall Pimax = new(Runtime, "2.0.4.329");

    private readonly FakeRegistry registry = new FakeRegistry()
        .Set(ServiceKey, "ImagePath", $"\"{Path.Combine(Runtime, "PiServiceLauncher.exe")}\"")
        .Set(Uninstall, "DisplayName", "PimaxPlay")
        .Set(Uninstall, "DisplayVersion", "2.0.4.329");

    private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
    {
        Pimax.TobiiRuntimeExe,
        Pimax.BlobPath("K11_RAWH"),
        SteamVr.DriverLighthouse(SteamVrDir),
    };

    private readonly FakeUsb usb = new(DreamAirIds.Board, DreamAirIds.EyeTracker, DreamAirIds.Receiver, new UsbId(0x046D, 0xC52B));

    private Func<string?> nvidia = () => "32.0.16.2101";
    private Func<string?> steamVr = () => SteamVrDir;

    private IReadOnlyList<CheckResult> Run(string variant = "K11_RAWH") =>
        new DreamAirChecks(registry, usb, files.Contains, nvidia, steamVr).Run(variant);

    private CheckResult Check(string label, string variant = "K11_RAWH") => Assert.Single(Run(variant), r => r.Label == label);

    [Fact]
    public void A_ready_pc_passes_every_check_in_order()
    {
        Assert.Equal(
            new CheckResult[]
            {
                new("NVIDIA GPU", StatusLevel.Ok, "Driver 621.01"),
                new("Pimax Play", StatusLevel.Ok, "2.0.4.329"),
                new("Eye geometry", StatusLevel.Ok, "config K11_RAWH"),
                new("SteamVR", StatusLevel.Ok, "Lighthouse driver found"),
                new("Headset", StatusLevel.Ok, "Connected"),
            },
            Run());
    }

    [Fact]
    public void No_nvidia_gpu_is_an_error()
    {
        nvidia = () => null;

        Assert.Equal(
            new CheckResult("NVIDIA GPU", StatusLevel.Error, "No NVIDIA GPU found.", "Mirador's Dream Air support needs a GeForce."),
            Check("NVIDIA GPU"));
    }

    [Fact]
    public void A_driver_version_mirador_cant_read_is_shown_as_windows_gives_it()
    {
        nvidia = () => "32.0.beta";

        Assert.Equal(new CheckResult("NVIDIA GPU", StatusLevel.Ok, "Driver 32.0.beta"), Check("NVIDIA GPU"));
    }

    [Fact]
    public void Windows_failing_to_list_the_gpus_is_an_error_with_its_message()
    {
        nvidia = () => throw new MiradorException("Windows didn't list the graphics cards within 5 seconds. Try again.");

        Assert.Equal(
            new CheckResult("NVIDIA GPU", StatusLevel.Error, "Windows didn't list the graphics cards within 5 seconds. Try again."),
            Check("NVIDIA GPU"));
    }

    [Fact]
    public void Pimax_play_not_installed_is_an_error_for_it_and_the_eye_geometry()
    {
        var results = new DreamAirChecks(new FakeRegistry(), usb, files.Contains, nvidia, steamVr).Run("K11_RAWH");

        Assert.Equal(
            new CheckResult("Pimax Play", StatusLevel.Error, "Pimax Play isn't installed.", "Install Pimax Play: Mirador uses its eye tracking runtime."),
            results[1]);
        Assert.Equal(
            new CheckResult("Eye geometry", StatusLevel.Error, "config_XR5PIMAXDA_K11_RAWH.bin comes with Pimax Play, which isn't installed.", "Install Pimax Play: Mirador uses its eye tracking runtime."),
            results[2]);
    }

    [Fact]
    public void A_missing_eye_tracking_runtime_is_an_error()
    {
        files.Remove(Pimax.TobiiRuntimeExe);

        Assert.Equal(
            new CheckResult("Pimax Play", StatusLevel.Error, "Pimax Play's eye tracking runtime is missing.", "Repair or update Pimax Play."),
            Check("Pimax Play"));
    }

    [Fact]
    public void Pimax_play_without_a_version_still_passes()
    {
        var registry = new FakeRegistry().Set(ServiceKey, "ImagePath", $"\"{Path.Combine(Runtime, "PiServiceLauncher.exe")}\"");

        var results = new DreamAirChecks(registry, usb, files.Contains, nvidia, steamVr).Run("K11_RAWH");

        Assert.Equal(new CheckResult("Pimax Play", StatusLevel.Ok, "Installed"), results[1]);
    }

    [Fact]
    public void A_missing_geometry_file_is_an_error()
    {
        files.Remove(Pimax.BlobPath("K11_RAWH"));

        Assert.Equal(
            new CheckResult("Eye geometry", StatusLevel.Error, "config_XR5PIMAXDA_K11_RAWH.bin is missing from Pimax Play.", "Repair or update Pimax Play."),
            Check("Eye geometry"));
    }

    [Fact]
    public void The_geometry_checked_is_the_variant_asked_for()
    {
        Assert.Equal(StatusLevel.Error, Check("Eye geometry", "K10_RAYH").Level);

        files.Add(Pimax.BlobPath("K10_RAYH"));

        Assert.Equal(new CheckResult("Eye geometry", StatusLevel.Ok, "config K10_RAYH"), Check("Eye geometry", "K10_RAYH"));
    }

    [Fact]
    public void No_steamvr_is_an_error()
    {
        steamVr = () => null;

        Assert.Equal(
            new CheckResult("SteamVR", StatusLevel.Error, "SteamVR isn't installed.", "Install SteamVR from Steam. It doesn't need to run: Mirador uses its Lighthouse driver."),
            Check("SteamVR"));
    }

    [Fact]
    public void Steamvr_without_its_lighthouse_driver_is_an_error()
    {
        files.Remove(SteamVr.DriverLighthouse(SteamVrDir));

        Assert.Equal(
            new CheckResult("SteamVR", StatusLevel.Error, "SteamVR's Lighthouse driver is missing.", "Repair SteamVR: in Steam, open its Properties, then Installed Files, and verify its files."),
            Check("SteamVR"));
    }

    [Fact]
    public void Missing_headset_parts_are_named_as_a_warning()
    {
        usb.Ids.Remove(DreamAirIds.EyeTracker);
        usb.Ids.Remove(DreamAirIds.Receiver);

        Assert.Equal(
            new CheckResult("Headset", StatusLevel.Warning, "Not connected: eye tracker, Lighthouse receiver.", "Plug the headset in and switch it on."),
            Check("Headset"));
    }

    [Fact]
    public void A_headset_not_plugged_in_at_all_is_a_warning_naming_every_part()
    {
        usb.Ids.Clear();

        Assert.Equal(
            new CheckResult("Headset", StatusLevel.Warning, "Not connected: headset board, eye tracker, Lighthouse receiver.", "Plug the headset in and switch it on."),
            Check("Headset"));
    }

    [Fact]
    public void Windows_failing_to_list_usb_devices_is_a_warning_with_its_message()
    {
        usb.Failure = new MiradorException("Windows couldn't list the USB devices (busy), so Mirador can't tell which headset is plugged in. Try again.");

        Assert.Equal(
            new CheckResult("Headset", StatusLevel.Warning, "Windows couldn't list the USB devices (busy), so Mirador can't tell which headset is plugged in. Try again."),
            Check("Headset"));
    }

    [Theory]
    [InlineData("K12_RAWH")]
    [InlineData(@"..\..\evil")]
    public void An_unknown_geometry_variant_is_an_error_not_a_throw(string variant)
    {
        Assert.Equal(
            new CheckResult("Eye geometry", StatusLevel.Error, $"Mirador doesn't know the eye geometry variant {variant}.", "Pick your headset's variant in Eye tracking."),
            Check("Eye geometry", variant));
    }

    [Fact]
    public void A_file_steam_holds_open_the_whole_time_reads_as_missing()
    {
        using var dir = new TempDir();
        var path = dir.Touch("libraryfolders.vdf");
        var waits = 0;

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(DreamAirChecks.ReadTextOrNull(path, _ => waits++));
        }

        Assert.Equal(2, waits);
    }

    [Fact]
    public void A_file_steam_lets_go_of_between_tries_is_read()
    {
        using var dir = new TempDir();
        var path = dir.Touch("libraryfolders.vdf");
        File.WriteAllText(path, "\"libraryfolders\" {}");
        var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var text = DreamAirChecks.ReadTextOrNull(path, _ => held.Dispose());

        Assert.Equal("\"libraryfolders\" {}", text);
    }

    [Fact]
    public void A_missing_file_reads_as_missing_without_waiting()
    {
        using var dir = new TempDir();
        var waits = 0;

        Assert.Null(DreamAirChecks.ReadTextOrNull(Path.Combine(dir.Path, "gone.vdf"), _ => waits++));
        Assert.Null(DreamAirChecks.ReadTextOrNull(Path.Combine(dir.Path, "gone", "libraryfolders.vdf"), _ => waits++));
        Assert.Equal(0, waits);
    }

    private sealed class FakeUsb(params UsbId[] ids) : IUsbDevices
    {
        public HashSet<UsbId> Ids { get; } = [.. ids];

        public MiradorException? Failure { get; set; }

        public IReadOnlySet<UsbId> Present() => Failure is null ? Ids.ToHashSet() : throw Failure;
    }
}
