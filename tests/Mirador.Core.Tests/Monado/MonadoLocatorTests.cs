using Mirador.Core.Monado;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Monado;

public class MonadoLocatorTests
{
    /// <summary>Creates an empty file (and its folders) and returns its path.</summary>
    private static string Touch(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>A Monado build folder the way a dev build leaves it: a manifest with an absolute library path.</summary>
    private static string MakeBuild(string folder, string manifestName = "openxr_monado-dev.json", bool withService = true)
    {
        var dll = Touch(folder, "src", "xrt", "targets", "openxr", "openxr_monado.dll");
        File.WriteAllText(Path.Combine(folder, manifestName), TestManifest.Json(dll));
        if (withService)
        {
            Touch(folder, "src", "xrt", "targets", "service", "monado-service.exe");
        }

        return dll;
    }

    private static MiradorException LocateFails(string bundledRoot, BuildSource source) =>
        Assert.Throws<MiradorException>(() => new MonadoLocator(bundledRoot).Locate(source));

    [Fact]
    public void Finds_a_dev_build_folder()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "build");
        var dll = MakeBuild(folder);

        var install = new MonadoLocator(dir.File("no-bundle")).Locate(new BuildSource(SourceKind.Folder, folder));

        Assert.Equal(Path.Combine(folder, "openxr_monado-dev.json"), install.Manifest);
        Assert.Equal(dll, install.RuntimeDll);
        Assert.Equal(Path.Combine(folder, "src", "xrt", "targets", "service", "monado-service.exe"), install.ServiceExe);
        Assert.Equal(Path.Combine(folder, "src", "xrt", "targets", "openxr", "service"), install.StandInDir);
        Assert.False(install.IsBundled);
    }

    [Fact]
    public void Finds_a_build_folder_with_spaces_and_non_ascii()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "Zoë Build", "monado build");
        var dll = MakeBuild(folder);

        var install = new MonadoLocator(dir.File("no-bundle")).Locate(new BuildSource(SourceKind.Folder, folder));

        Assert.Equal(Path.Combine(folder, "openxr_monado-dev.json"), install.Manifest);
        Assert.Equal(dll, install.RuntimeDll);
        Assert.Equal(Path.Combine(folder, "src", "xrt", "targets", "service", "monado-service.exe"), install.ServiceExe);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(dll)!, "service"), install.StandInDir);
    }

    [Fact]
    public void A_folder_with_only_openxr_monado_json_is_found_too()
    {
        using var dir = new TempDir();
        var dll = MakeBuild(dir.Path, manifestName: "openxr_monado.json");

        var install = new MonadoLocator(dir.File("no-bundle")).Locate(new BuildSource(SourceKind.Folder, dir.Path));

        Assert.Equal(dir.File("openxr_monado.json"), install.Manifest);
        Assert.Equal(dll, install.RuntimeDll);
    }

    [Fact]
    public void The_dev_manifest_wins_over_openxr_monado_json()
    {
        using var dir = new TempDir();
        var devDll = MakeBuild(dir.Path);
        File.WriteAllText(dir.File("openxr_monado.json"), TestManifest.Json(Touch(dir.Path, "other", "openxr_monado.dll")));

        var install = new MonadoLocator(dir.File("no-bundle")).Locate(new BuildSource(SourceKind.Folder, dir.Path));

        Assert.Equal(dir.File("openxr_monado-dev.json"), install.Manifest);
        Assert.Equal(devDll, install.RuntimeDll);
    }

    [Fact]
    public void Missing_service_names_the_file()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "build");
        MakeBuild(folder, withService: false);

        var ex = LocateFails(dir.File("no-bundle"), new BuildSource(SourceKind.Folder, folder));

        Assert.Contains("monado-service.exe", ex.Message);
        Assert.Contains(folder, ex.Message);
    }

    [Fact]
    public void Missing_manifest_says_it_is_not_a_monado_build()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "elsewhere");
        Directory.CreateDirectory(folder);

        var ex = LocateFails(dir.File("no-bundle"), new BuildSource(SourceKind.Folder, folder));

        Assert.Contains($"{folder} has no openxr_monado-dev.json or openxr_monado.json: is it a Monado build folder?", ex.Message);
    }

    [Fact]
    public void A_missing_runtime_dll_names_the_file()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "build");
        var dll = MakeBuild(folder);
        File.Delete(dll);

        var ex = LocateFails(dir.File("no-bundle"), new BuildSource(SourceKind.Folder, folder));

        Assert.Contains(dll, ex.Message);
    }

    [Fact]
    public void A_manifest_that_is_not_json_is_reported()
    {
        using var dir = new TempDir();
        MakeBuild(dir.Path);
        File.WriteAllText(dir.File("openxr_monado-dev.json"), "not json at all");

        var ex = LocateFails(dir.File("no-bundle"), new BuildSource(SourceKind.Folder, dir.Path));

        Assert.Contains(dir.File("openxr_monado-dev.json"), ex.Message);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_reported()
    {
        using var dir = new TempDir();
        var folder = dir.File("gone");

        var ex = LocateFails(dir.File("no-bundle"), new BuildSource(SourceKind.Folder, folder));

        Assert.Contains($"The Monado build folder {folder} doesn't exist.", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_folder_source_without_a_folder_says_to_pick_one(string? folder)
    {
        using var dir = new TempDir();

        var ex = LocateFails(dir.File("no-bundle"), new BuildSource(SourceKind.Folder, folder));

        Assert.Contains("Pick a Monado build folder in the profile.", ex.Message);
    }

    [Fact]
    public void Bundled_layout_resolves_relative_library_path()
    {
        using var dir = new TempDir();
        var root = dir.File("app");
        File.WriteAllText(Touch(root, "runtime", "openxr_monado.json"), TestManifest.Json("./openxr_monado.dll"));
        Touch(root, "runtime", "openxr_monado.dll");
        Touch(root, "server", "monado-service.exe");

        var install = new MonadoLocator(root).Locate(BuildSource.Bundled);

        Assert.Equal(Path.Combine(root, "runtime", "openxr_monado.json"), install.Manifest);
        Assert.Equal(Path.Combine(root, "runtime", "openxr_monado.dll"), install.RuntimeDll);
        Assert.Equal(Path.Combine(root, "server", "monado-service.exe"), install.ServiceExe);
        Assert.Equal(Path.Combine(root, "runtime", "service"), install.StandInDir); // where the runtime DLL looks
        Assert.False(Directory.Exists(install.StandInDir)); // the installer puts the stand-in there; locating creates nothing
        Assert.True(install.IsBundled);
    }

    [Fact]
    public void No_monado_from_an_older_install_says_pick_another()
    {
        using var dir = new TempDir();

        var ex = LocateFails(dir.File("app"), BuildSource.Bundled);

        Assert.Equal("This Mirador has no Monado from an older install. Pick another Monado in the profile.", ex.Message);
    }

    [Fact]
    public void Bundled_monado_without_its_service_names_the_file()
    {
        using var dir = new TempDir();
        var root = dir.File("app");
        File.WriteAllText(Touch(root, "runtime", "openxr_monado.json"), TestManifest.Json("./openxr_monado.dll"));
        Touch(root, "runtime", "openxr_monado.dll");

        var ex = LocateFails(root, BuildSource.Bundled);

        Assert.Contains(Path.Combine(root, "server", "monado-service.exe"), ex.Message);
    }
}
