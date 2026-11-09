using Mirador.Ipc;

namespace Mirador.Helper.Tests;

internal sealed class FakeApprovedBuildStore : IApprovedBuildStore
{
    public Dictionary<string, ApprovedBuild> Builds { get; } = [];

    public ApprovedBuild? Get(string profileId) => Builds.GetValueOrDefault(profileId);

    public void Set(ApprovedBuild build) => Builds[build.ProfileId] = build;

    public void Remove(string profileId) => Builds.Remove(profileId);
}

public sealed class ApprovedBuildsTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly TempDir temp = new();
    private readonly FakeApprovedBuildStore store = new();

    public void Dispose() => temp.Dispose();

    private string App => temp.File("app");

    private string MakeInstall(string name = "install", string service = "service v1")
    {
        var folder = temp.File("user", name);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "bin", "openxr_monado.dll"), "dll v1");
        File.WriteAllText(Path.Combine(folder, "bin", "monado-service.exe"), service);
        return folder;
    }

    [Fact]
    public void Approving_copies_the_build_into_the_install_folder_and_records_every_files_hash()
    {
        var source = MakeInstall();

        var build = ApprovedBuilds.Approve("p1", Commit, source, App, store);

        Assert.Equal(Path.Combine(App, "builds", "p1", Commit), build.Folder);
        Assert.Equal("service v1", File.ReadAllText(Path.Combine(build.ServerDir, "monado-service.exe")));
        Assert.Equal(3, build.Files.Count);
        Assert.Same(build, store.Get("p1"));
        Assert.Null(ApprovedBuilds.Verify(build, App));
        Assert.True(ApprovedBuilds.Matches(build, source));
    }

    [Fact]
    public void A_rebuild_no_longer_matches_and_a_new_approval_replaces_the_old_copy()
    {
        var source = MakeInstall();
        var first = ApprovedBuilds.Approve("p1", Commit, source, App, store);
        File.WriteAllText(Path.Combine(source, "bin", "monado-service.exe"), "service v2");

        Assert.False(ApprovedBuilds.Matches(first, source));

        var second = ApprovedBuilds.Approve("p1", "abcdef1234", source, App, store);
        Assert.False(Directory.Exists(first.Folder));
        Assert.True(ApprovedBuilds.Matches(second, source));
    }

    [Fact]
    public void A_file_swapped_in_the_approved_copy_is_caught()
    {
        var build = ApprovedBuilds.Approve("p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(Path.Combine(build.ServerDir, "monado-service.exe"), "evil");

        Assert.Contains("changed since it was approved", ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_dll_added_to_the_approved_copy_is_caught()
    {
        var build = ApprovedBuilds.Approve("p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(Path.Combine(build.ServerDir, "version.dll"), "evil");

        Assert.Contains("was added", ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_record_naming_another_folder_is_refused()
    {
        var build = ApprovedBuilds.Approve("p1", Commit, MakeInstall(), App, store);
        var moved = build with { Folder = temp.File("user", "install") };

        Assert.Contains("doesn't name its own folder", ApprovedBuilds.Verify(moved, App));
    }

    [Fact]
    public void A_link_in_the_build_is_refused()
    {
        var source = MakeInstall();
        Directory.CreateDirectory(temp.File("elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(source, "bin", "plugins"), temp.File("elsewhere"));

        var e = Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve("p1", Commit, source, App, store));

        Assert.Contains("is a link", e.Message);
        Assert.Null(store.Get("p1"));
    }

    [Theory]
    [InlineData("..", Commit)]
    [InlineData("a\\b", Commit)]
    [InlineData("p1", "..\\x")]
    [InlineData("p1", "HEAD")]
    public void Names_that_could_be_paths_are_refused(string profileId, string commit)
    {
        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(profileId, commit, MakeInstall(), App, store));
    }

    [Fact]
    public void A_build_without_the_service_is_refused()
    {
        var source = MakeInstall();
        File.Delete(Path.Combine(source, "bin", "monado-service.exe"));

        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve("p1", Commit, source, App, store));
    }
}
