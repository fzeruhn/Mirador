using Mirador.Core.DirectMode;
using Mirador.Core;

namespace Mirador.Core.Tests.DirectMode;

public class DirectModeLayerTests
{
    private const string Json = "mirador_direct_mode.json";
    private const string Dll = "mirador_direct_mode.dll";
    private const string Check = "mirador-display-check.exe";

    [Fact]
    public void The_layer_that_comes_with_mirador_has_everything_side_by_side()
    {
        using var bundled = new TempDir();
        bundled.Touch(Json);
        bundled.Touch(Dll);
        bundled.Touch(Check);

        Assert.Equal(new LayerFiles(bundled.Path, Path.Combine(bundled.Path, Check)), DirectModeLayer.Locate(bundled.Path));
    }

    [Fact]
    public void A_trailing_separator_on_the_folder_is_fine()
    {
        using var bundled = new TempDir();
        bundled.Touch(Json);
        bundled.Touch(Dll);
        bundled.Touch(Check);

        Assert.Equal(bundled.Path, DirectModeLayer.Locate(bundled.Path + Path.DirectorySeparatorChar).ManifestDir);
    }

    [Fact]
    public void The_layer_is_only_found_under_its_new_names()
    {
        using var bundled = new TempDir();
        bundled.Touch("dreamair_vk_layer.json");
        bundled.Touch("dreamair_vk_layer.dll");
        bundled.Touch("dreamair-display-check.exe");

        Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(bundled.Path));
    }

    [Fact]
    public void An_incomplete_layer_asks_for_a_reinstall()
    {
        using var bundled = new TempDir();
        bundled.Touch(Json);
        bundled.Touch(Check);

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(bundled.Path));

        Assert.Equal(
            $"The direct-mode layer that comes with Mirador is incomplete: {Dll} is missing from {bundled.Path}. Reinstall Mirador.",
            e.Message);
    }

    [Fact]
    public void A_layer_without_its_check_tool_asks_for_a_reinstall()
    {
        using var bundled = new TempDir();
        bundled.Touch(Json);
        bundled.Touch(Dll);

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(bundled.Path));

        Assert.Equal(
            $"The direct-mode layer that comes with Mirador is incomplete: {Check} is missing from {bundled.Path}. Reinstall Mirador.",
            e.Message);
    }

    [Fact]
    public void An_empty_layer_folder_names_every_file()
    {
        using var bundled = new TempDir();

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(bundled.Path));

        Assert.Contains($"{Json} and {Dll} and {Check} are missing", e.Message);
    }

    [Fact]
    public void No_layer_folder_at_all_asks_for_a_reinstall()
    {
        using var dir = new TempDir();
        var gone = Path.Combine(dir.Path, "layer");

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(gone));

        Assert.Equal($"The direct-mode layer that comes with Mirador is missing ({gone}). Reinstall Mirador.", e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_folder_for_it_the_layer_asks_for_a_reinstall(string bundledDir)
    {
        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(bundledDir));

        Assert.Equal("This Mirador doesn't know where the direct-mode layer that comes with it is. Reinstall Mirador.", e.Message);
    }
}
