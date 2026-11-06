using Mirador.Core.Monado;
using Mirador.Core.Runtime;

namespace Mirador.Core.Tests.Runtime;

public class RuntimeLineTests
{
    private static readonly MonadoInstall Bundled = new(
        @"C:\Program Files\Mirador\monado\runtime\openxr_monado.json",
        @"C:\Program Files\Mirador\monado\runtime\openxr_monado.dll",
        @"C:\Program Files\Mirador\monado\server\monado-service.exe",
        @"C:\Program Files\Mirador\monado\runtime\service",
        IsBundled: true);

    [Fact]
    public void This_profiles_monado()
    {
        Assert.Equal("Monado, this profile", RuntimeLine.Describe(Bundled.Manifest, Bundled, isActive: true));
    }

    [Fact]
    public void Another_runtime_by_its_file_name()
    {
        Assert.Equal(
            "PiOpenXR_64.json",
            RuntimeLine.Describe(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", Bundled, isActive: false));
        Assert.Equal(
            "steamxr_win64.json",
            RuntimeLine.Describe("\"C:/Program Files (x86)/Steam/steamapps/common/SteamVR/steamxr_win64.json\"", null, isActive: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void None_set(string? active)
    {
        Assert.Equal("None set", RuntimeLine.Describe(active, Bundled, isActive: false));
    }
}
