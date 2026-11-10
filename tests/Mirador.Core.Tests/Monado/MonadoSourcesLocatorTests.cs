using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Monado;

/// <summary>Mirador's Monado, a built branch and an install folder (Mirador_Plan.md Task 60).</summary>
public class MonadoSourcesLocatorTests
{
    private const string Version = "monado-2026.11.10-1a2b3c4d5e";

    /// <summary>Monado's Windows install layout: the manifest at the top naming bin\openxr_monado.dll, the service beside it.</summary>
    private static void MakeInstall(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "openxr_monado.dll"), "");
        File.WriteAllText(Path.Combine(folder, "bin", "monado-service.exe"), "");
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"), TestManifest.Json(Path.Combine("bin", "openxr_monado.dll")));
    }

    [Fact]
    public void Miradors_monado_is_the_current_version_in_its_root()
    {
        using var dir = new TempDir();
        var root = dir.File("monado");
        MakeInstall(Path.Combine(root, Version));
        MiradorMonado.Write(root, new MiradorMonadoVersions(Version, null));

        var install = new MonadoLocator(root).Locate(BuildSource.Mirador);

        Assert.Equal(Path.Combine(root, Version, "openxr_monado.json"), install.Manifest);
        Assert.Equal(Path.Combine(root, Version, "bin", "openxr_monado.dll"), install.RuntimeDll);
        Assert.Equal(Path.Combine(root, Version, "bin", "monado-service.exe"), install.ServiceExe);
        Assert.Equal(Path.Combine(root, Version, "bin", "service"), install.StandInDir);
        Assert.True(install.IsBundled);
    }

    [Fact]
    public void A_profile_running_as_administrator_gets_its_builds_approved_copy()
    {
        using var dir = new TempDir();
        var folder = dir.File("my-build");
        MakeInstall(folder);
        var approved = new ApprovedBuild(
            "S-1-5-21-1", "p1", "folder", dir.File("app", "builds", "S-1-5-21-1", "p1", "folder"), new Dictionary<string, string>());
        var asked = new List<string>();
        var locator = new MonadoLocator(dir.File("monado"), approvedCopy: (p, install) =>
        {
            asked.Add($"{p.Id} {install.Manifest}");
            return approved;
        });
        var profile = new Profile(
            "p1", "Mine", null, new BuildSource(SourceKind.Folder, folder), [], new Dictionary<string, string>(), RunAsAdministrator: true);

        Assert.Same(approved, locator.Locate(profile).Approved);
        Assert.Equal([$"p1 {Path.Combine(folder, "openxr_monado.json")}"], asked);
        Assert.Null(locator.Locate(profile with { RunAsAdministrator = false }).Approved);
        Assert.Single(asked);
    }

    [Fact]
    public void A_branch_profile_gets_its_approved_copy_and_none_when_its_build_isnt_the_approved_one()
    {
        using var dir = new TempDir();
        var approved = new ApprovedBuild(
            "S-1-5-21-1", "p1", "abc1234", dir.File("app", "builds", "S-1-5-21-1", "p1", "abc1234"), new Dictionary<string, string>());
        var matches = true;
        var locator = new MonadoLocator(dir.File("monado"), dir.File("Builds"), (_, _) => matches ? approved : null);
        MakeInstall(locator.BranchInstallDir("p1"));
        var profile = new Profile("p1", "Mine", null, BuildSource.UpstreamMain, [], new Dictionary<string, string>(), RunAsAdministrator: true);

        var install = locator.Locate(profile);
        Assert.Same(approved, install.Approved);
        Assert.Equal(Path.Combine(locator.BranchInstallDir("p1"), "openxr_monado.json"), install.Manifest);

        matches = false;
        Assert.Null(locator.Locate(profile).Approved);
    }

    [Fact]
    public void Miradors_monado_has_no_approved_copy_to_look_up()
    {
        using var dir = new TempDir();
        var root = dir.File("monado");
        MakeInstall(Path.Combine(root, Version));
        MiradorMonado.Write(root, new MiradorMonadoVersions(Version, null));
        var locator = new MonadoLocator(root, approvedCopy: (_, _) => throw new InvalidOperationException("not asked"));
        var profile = new Profile("p1", "Mine", null, BuildSource.Mirador, [], new Dictionary<string, string>(), RunAsAdministrator: true);

        Assert.Null(locator.Locate(profile).Approved);
    }

    [Fact]
    public void Miradors_monado_not_installed_says_where_to_install_it()
    {
        using var dir = new TempDir();

        var e = Assert.Throws<MiradorException>(() => new MonadoLocator(dir.File("monado")).Locate(BuildSource.Mirador));

        Assert.Contains("isn't installed yet", e.Message);
    }

    [Theory]
    [InlineData("""{"current":"..\\..\\Windows"}""")]
    [InlineData("""{"current":"monado-2026.11.10-1a2b3c4d5e\\..\\x"}""")]
    [InlineData("not json")]
    public void A_current_file_naming_no_version_counts_as_nothing_installed(string text)
    {
        using var dir = new TempDir();
        var root = dir.File("monado");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, MiradorMonado.CurrentFile), text);

        Assert.Null(MiradorMonado.Read(root).Current);
        Assert.Contains("isn't installed yet", Assert.Throws<MiradorException>(() => new MonadoLocator(root).Locate(BuildSource.Mirador)).Message);
    }

    [Fact]
    public void A_branch_is_found_in_its_profiles_build_folder_once_built()
    {
        using var dir = new TempDir();
        var locator = new MonadoLocator(dir.File("monado"), dir.File("Builds"));
        var profile = new Profile("p1", "Fork", null, BuildSource.UpstreamMain, [], new Dictionary<string, string>());

        Assert.Contains("isn't built yet", Assert.Throws<MiradorException>(() => locator.Locate(profile)).Message);

        MakeInstall(dir.File("Builds", "p1", "install"));
        var install = locator.Locate(profile);

        Assert.Equal(dir.File("Builds", "p1", "install", "openxr_monado.json"), install.Manifest);
        Assert.False(install.IsBundled);
        Assert.Equal(dir.File("Builds", "p1", "install"), locator.BranchInstallDir("p1"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("")]
    public void A_profile_id_that_could_be_a_path_is_refused(string id)
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentException>(() => new MonadoLocator(dir.File("monado"), dir.File("Builds")).BranchDir(id));
    }

    [Fact]
    public void A_build_folder_can_be_an_install_folder()
    {
        using var dir = new TempDir();
        MakeInstall(dir.File("install"));

        var install = new MonadoLocator(dir.File("monado")).Locate(new BuildSource(SourceKind.Folder, dir.File("install")));

        Assert.Equal(dir.File("install", "bin", "monado-service.exe"), install.ServiceExe);
        Assert.False(install.IsBundled);
    }

    [Fact]
    public void New_profiles_default_to_miradors_monado()
    {
        Assert.All(ProfileSet.Defaults().Profiles, p => Assert.Equal(SourceKind.Mirador, p.Monado.Kind));
    }
}
