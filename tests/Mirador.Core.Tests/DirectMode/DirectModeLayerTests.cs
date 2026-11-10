using Mirador.Core.DirectMode;
using Mirador.Core;

namespace Mirador.Core.Tests.DirectMode;

public class DirectModeLayerTests
{
    private const string Json = "dreamair_vk_layer.json";
    private const string Dll = "dreamair_vk_layer.dll";
    private const string Check = "dreamair-display-check.exe";

    [Fact]
    public void A_build_folder_has_the_layer_and_the_check_in_their_own_folders()
    {
        // dreamair-vk-layer\build, as install.ps1 -Source takes it.
        using var dir = new TempDir();
        dir.Touch("layer", Json);
        dir.Touch("layer", Dll);
        dir.Touch("check", Check);

        var files = DirectModeLayer.Locate(dir.Path, bundledDir: "unused");

        Assert.Equal(new LayerFiles(Path.Combine(dir.Path, "layer"), Path.Combine(dir.Path, "check", Check)), files);
    }

    [Fact]
    public void A_release_folder_has_everything_side_by_side()
    {
        using var dir = new TempDir();
        dir.Touch(Json);
        dir.Touch(Dll);
        dir.Touch(Check);

        var files = DirectModeLayer.Locate(dir.Path, bundledDir: "unused");

        Assert.Equal(new LayerFiles(dir.Path, Path.Combine(dir.Path, Check)), files);
    }

    [Fact]
    public void Files_in_the_folder_itself_win_over_its_layer_and_check_folders()
    {
        using var dir = new TempDir();
        foreach (var name in new[] { Json, Dll, Check })
        {
            dir.Touch(name);
        }

        dir.Touch("layer", Json);
        dir.Touch("layer", Dll);
        dir.Touch("check", Check);

        Assert.Equal(new LayerFiles(dir.Path, Path.Combine(dir.Path, Check)), DirectModeLayer.Locate(dir.Path, "unused"));
    }

    [Fact]
    public void A_trailing_separator_on_the_folder_is_fine()
    {
        using var dir = new TempDir();
        dir.Touch(Json);
        dir.Touch(Dll);
        dir.Touch(Check);

        var files = DirectModeLayer.Locate(dir.Path + Path.DirectorySeparatorChar, "unused");

        Assert.Equal(dir.Path, files.ManifestDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_folder_the_bundled_layer_is_used(string? folder)
    {
        using var bundled = new TempDir();
        bundled.Touch(Json);
        bundled.Touch(Dll);
        bundled.Touch(Check);

        var files = DirectModeLayer.Locate(folder, bundled.Path);

        Assert.Equal(new LayerFiles(bundled.Path, Path.Combine(bundled.Path, Check)), files);
    }

    [Theory]
    [InlineData(Dll, Dll)]
    [InlineData(Json, Json)]
    public void A_missing_layer_file_is_named(string missing, string named)
    {
        using var dir = new TempDir();
        foreach (var name in new[] { Json, Dll }.Where(name => name != missing))
        {
            dir.Touch("layer", name);
        }

        dir.Touch("check", Check);

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(dir.Path, "unused"));

        Assert.Equal(
            $"The folder {dir.Path} has no {named}, in it or in its layer folder: is it the direct-mode layer's build or release folder? Pick another folder in the profile.",
            e.Message);
    }

    [Fact]
    public void An_empty_folder_names_both_layer_files()
    {
        using var dir = new TempDir();

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(dir.Path, "unused"));

        Assert.Contains($"has no {Json} or {Dll}, in it or in its layer folder", e.Message);
    }

    [Fact]
    public void Layer_files_split_across_two_folders_dont_count()
    {
        using var dir = new TempDir();
        dir.Touch(Json);
        dir.Touch("layer", Dll);
        dir.Touch(Check);

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(dir.Path, "unused"));

        Assert.Contains($"has no {Json} and {Dll} side by side", e.Message);
    }

    [Fact]
    public void A_missing_check_tool_is_named()
    {
        using var dir = new TempDir();
        dir.Touch("layer", Json);
        dir.Touch("layer", Dll);

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(dir.Path, "unused"));

        Assert.Equal(
            $"The folder {dir.Path} has no {Check}, in it or in its check folder: is it the direct-mode layer's build or release folder? Pick another folder in the profile.",
            e.Message);
    }

    [Fact]
    public void A_folder_that_isnt_there_says_so()
    {
        using var dir = new TempDir();
        var gone = Path.Combine(dir.Path, "gone");

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(gone, "unused"));

        Assert.Equal(
            $"The direct-mode layer folder {gone} doesn't exist. Pick another folder in the profile, or clear it to use the layer that comes with Mirador.",
            e.Message);
    }

    [Fact]
    public void A_folder_that_isnt_a_full_path_is_refused()
    {
        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate("build", "unused"));

        Assert.Equal("The direct-mode layer folder \"build\" isn't a full path. Pick the folder again in the profile.", e.Message);
    }

    [Fact]
    public void An_incomplete_bundled_layer_asks_for_a_reinstall()
    {
        using var bundled = new TempDir();
        bundled.Touch(Json);
        bundled.Touch(Check);

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(null, bundled.Path));

        Assert.Equal(
            $"The direct-mode layer that comes with Mirador is incomplete: {Dll} is missing from {bundled.Path}. Reinstall Mirador, or pick a layer folder in the profile.",
            e.Message);
    }

    [Fact]
    public void A_bundled_layer_without_its_check_tool_asks_for_a_reinstall()
    {
        using var bundled = new TempDir();
        bundled.Touch(Json);
        bundled.Touch(Dll);

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(null, bundled.Path));

        Assert.Equal(
            $"The direct-mode layer that comes with Mirador is incomplete: {Check} is missing from {bundled.Path}. Reinstall Mirador, or pick a layer folder in the profile.",
            e.Message);
    }

    [Fact]
    public void No_bundled_layer_at_all_asks_for_a_reinstall()
    {
        using var dir = new TempDir();
        var gone = Path.Combine(dir.Path, "layer");

        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Locate(null, gone));

        Assert.Equal(
            $"The direct-mode layer that comes with Mirador is missing ({gone}). Reinstall Mirador, or pick a layer folder in the profile.",
            e.Message);
    }
}
