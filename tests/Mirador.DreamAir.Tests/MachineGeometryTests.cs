using Mirador.Core;
using Mirador.Core.Modules;

namespace Mirador.DreamAir.Tests;

[Trait("Category", "Hardware")]
public class MachineGeometryTests
{
    /// <summary>
    /// Uploads the tested variant's geometry with the real runtime, from this PC's Pimax Play, to the real tracker. Stop
    /// Pimax's service first (<c>Stop-Service PiServiceLauncher</c>, elevated), then run with <c>MIRADOR_MACHINE_TESTS=1</c>:
    /// <c>dotnet test tests\Mirador.DreamAir.Tests --filter Category=Machine</c>. Files go to a temp folder.
    /// </summary>
    [Trait("Category", "Machine")]
    [MachineFact]
    public async Task The_real_runtime_uploads_the_geometry()
    {
        using var dir = new TempDir();
        var pimax = PimaxInstall.Locate(new WindowsRegistryReader());
        Assert.NotNull(pimax);
        var uploader = new GeometryUploader(
            ConsoleProcess.Start, new WmiUsbDevices(), new SystemProcessList(), new AppPaths(dir.Path), TimeProvider.System);

        var result = await uploader.UploadAsync(pimax, EyeBlobVariants.Tested, CancellationToken.None);

        Assert.True(result.Ok, $"{result.Message}\n{(File.Exists(result.LogPath) ? File.ReadAllText(result.LogPath) : "")}");
        Assert.False(string.IsNullOrWhiteSpace(result.TrackerSerial));
    }
}
