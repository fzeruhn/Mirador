using Mirador.Ipc;

namespace Mirador.DreamAir.Tests;

public class PimaxInstallTests
{
    private const string ServiceKey = @"HKLM\SYSTEM\CurrentControlSet\Services\PiServiceLauncher";
    private const string Uninstall = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // As Pimax Play 2.0.4 writes them (vr-docs\captures\windows-2026-10-20\raw\w0_inventory.txt).
    private const string ImagePath = "\"C:\\Program Files\\Pimax\\Runtime\\PiServiceLauncher.exe\"";
    private const string Runtime = @"C:\Program Files\Pimax\Runtime";

    private static FakeRegistry PimaxPlayEntry(FakeRegistry reg, string root = Uninstall, string location = @"C:\Program Files\Pimax\") =>
        reg.Set($@"{root}\{{0D1E4F7A-PIMAX}}", "DisplayName", "PimaxPlay")
            .Set($@"{root}\{{0D1E4F7A-PIMAX}}", "DisplayVersion", "2.0.4.329")
            .Set($@"{root}\{{0D1E4F7A-PIMAX}}", "InstallLocation", location);

    [Fact]
    public void The_services_folder_is_the_runtime_folder()
    {
        var reg = new FakeRegistry().Set(ServiceKey, "ImagePath", ImagePath);

        var pimax = PimaxInstall.Locate(reg);

        Assert.NotNull(pimax);
        Assert.Equal(Runtime, pimax.RuntimeDir);
    }

    [Theory]
    [InlineData(@"C:\Pimax\Runtime\PiServiceLauncher.exe", @"C:\Pimax\Runtime")]
    [InlineData("\"C:\\Program Files\\Pimax\\Runtime\\PiServiceLauncher.exe\" -service", Runtime)]
    [InlineData(@"C:\Program Files\Pimax\Runtime\PiServiceLauncher.exe -service", Runtime)]
    [InlineData(@"\??\C:\Program Files\Pimax\Runtime\PiServiceLauncher.exe", Runtime)]
    public void Image_paths_with_arguments_or_without_quotes_work_too(string imagePath, string runtime)
    {
        var reg = new FakeRegistry().Set(ServiceKey, "ImagePath", imagePath);

        Assert.Equal(runtime, PimaxInstall.Locate(reg)?.RuntimeDir);
    }

    [Fact]
    public void The_version_comes_from_pimax_plays_uninstall_entry()
    {
        var reg = PimaxPlayEntry(new FakeRegistry().Set(ServiceKey, "ImagePath", ImagePath));

        Assert.Equal(new PimaxInstall(Runtime, "2.0.4.329"), PimaxInstall.Locate(reg));
    }

    [Fact]
    public void Without_the_service_the_uninstall_entry_gives_the_folder()
    {
        var reg = PimaxPlayEntry(new FakeRegistry());

        Assert.Equal(new PimaxInstall(Runtime, "2.0.4.329"), PimaxInstall.Locate(reg));
    }

    [Fact]
    public void The_32_bit_uninstall_list_counts_too()
    {
        var reg = PimaxPlayEntry(new FakeRegistry(), Uninstall32, @"D:\Pimax");

        Assert.Equal(new PimaxInstall(@"D:\Pimax\Runtime", "2.0.4.329"), PimaxInstall.Locate(reg));
    }

    [Fact]
    public void The_service_wins_over_the_uninstall_entry()
    {
        var reg = PimaxPlayEntry(new FakeRegistry().Set(ServiceKey, "ImagePath", ImagePath), location: @"D:\Old Pimax");

        Assert.Equal(Runtime, PimaxInstall.Locate(reg)?.RuntimeDir);
    }

    [Fact]
    public void Other_programs_uninstall_entries_are_skipped()
    {
        var reg = new FakeRegistry()
            .Set($@"{Uninstall}\Steam", "DisplayName", "Steam")
            .Set($@"{Uninstall}\Steam", "InstallLocation", @"C:\Program Files (x86)\Steam");

        Assert.Null(PimaxInstall.Locate(reg));
    }

    [Fact]
    public void An_entry_without_a_location_and_no_service_is_not_an_install()
    {
        var reg = new FakeRegistry().Set($@"{Uninstall}\PimaxPlay", "DisplayName", "PimaxPlay");

        Assert.Null(PimaxInstall.Locate(reg));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData("PiServiceLauncher.exe")]
    public void An_image_path_that_is_no_full_path_falls_back_to_the_entry(string imagePath)
    {
        var reg = PimaxPlayEntry(new FakeRegistry().Set(ServiceKey, "ImagePath", imagePath), location: @"D:\Pimax");

        Assert.Equal(@"D:\Pimax\Runtime", PimaxInstall.Locate(reg)?.RuntimeDir);
    }

    [Fact]
    public void Nothing_in_the_registry_is_no_install()
    {
        Assert.Null(PimaxInstall.Locate(new FakeRegistry()));
    }

    [Fact]
    public void The_eye_runtime_and_its_geometry_files_are_below_the_runtime_folder()
    {
        var pimax = new PimaxInstall(Runtime, null);

        Assert.Equal(
            $@"{Runtime}\EyeTrackingServer\platform_runtime\{PimaxNames.EyeRuntimeProcess}.exe",
            pimax.TobiiRuntimeExe);
        Assert.Equal(
            $@"{Runtime}\EyeTrackingServer\platform_runtime\config\config_XR5PIMAXDA_K11_RAWH.bin",
            pimax.BlobPath("K11_RAWH"));
    }

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("K12_RAWH")]
    [InlineData("")]
    public void Only_known_variants_make_a_geometry_path(string variant)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PimaxInstall(Runtime, null).BlobPath(variant));
    }
}
