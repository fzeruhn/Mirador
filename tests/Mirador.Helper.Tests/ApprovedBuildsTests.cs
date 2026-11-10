using Mirador.Ipc;

namespace Mirador.Helper.Tests;

internal sealed class FakeApprovedBuildStore : IApprovedBuildStore
{
    public Dictionary<(string, string), ApprovedBuild> Builds { get; } = [];

    public ApprovedBuild? Get(string owner, string profileId) => Builds.GetValueOrDefault((owner, profileId));

    public void Set(ApprovedBuild build) => Builds[(build.Owner, build.ProfileId)] = build;

    public void Remove(string owner, string profileId) => Builds.Remove((owner, profileId));

    public IReadOnlyList<string> ProfilesOf(string owner) => [.. Builds.Keys.Where(k => k.Item1 == owner).Select(k => k.Item2)];
}

public sealed class ApprovedBuildsTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Finn = "S-1-5-21-1-2-3-1001";
    private const string Other = "S-1-5-21-1-2-3-1002";
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

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.Equal(Path.Combine(App, "builds", Finn, "p1", Commit), build.Folder);
        Assert.Equal("service v1", File.ReadAllText(Path.Combine(build.ServerDir, "monado-service.exe")));
        Assert.Equal(3, build.Files.Count);
        Assert.Contains("./bin/openxr_monado.dll", File.ReadAllText(build.Manifest));
        Assert.Same(build, store.Get(Finn, "p1"));
        Assert.Null(ApprovedBuilds.Verify(build, App));
        Assert.True(ApprovedBuilds.Matches(build, source));
    }

    [Fact]
    public void A_rebuild_no_longer_matches_and_a_new_approval_replaces_the_old_copy()
    {
        var source = MakeInstall();
        var first = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);
        File.WriteAllText(Path.Combine(source, "bin", "monado-service.exe"), "service v2");

        Assert.False(ApprovedBuilds.Matches(first, source));

        var second = ApprovedBuilds.Approve(Finn, "p1", "abcdef1234", source, App, store);
        Assert.False(Directory.Exists(first.Folder));
        Assert.True(ApprovedBuilds.Matches(second, source));
    }

    [Fact]
    public void A_file_swapped_in_the_approved_copy_is_caught()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(Path.Combine(build.ServerDir, "monado-service.exe"), "evil");

        Assert.Contains("changed since it was approved", ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_dll_added_to_the_approved_copy_is_caught()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        File.WriteAllText(Path.Combine(build.ServerDir, "version.dll"), "evil");

        Assert.Contains("was added", ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_record_naming_another_folder_is_refused()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        var moved = build with { Folder = temp.File("user", "install") };

        Assert.Contains("doesn't name its own folder", ApprovedBuilds.Verify(moved, App));
    }

    [Fact]
    public void A_link_in_the_build_is_refused()
    {
        var source = MakeInstall();
        Directory.CreateDirectory(temp.File("elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(source, "bin", "plugins"), temp.File("elsewhere"));

        var e = Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store));

        Assert.Contains("is a link", e.Message);
        Assert.Null(store.Get(Finn, "p1"));
    }

    [Theory]
    [InlineData("..", Commit)]
    [InlineData("a\\b", Commit)]
    [InlineData("p1", "..\\x")]
    [InlineData("p1", "HEAD")]
    public void Names_that_could_be_paths_are_refused(string profileId, string commit)
    {
        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(Finn, profileId, commit, MakeInstall(), App, store));
    }

    [Fact]
    public void A_build_without_the_service_is_refused()
    {
        var source = MakeInstall();
        File.Delete(Path.Combine(source, "bin", "monado-service.exe"));

        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store));
    }

    [Fact]
    public void A_manifest_naming_a_dll_outside_the_build_is_replaced_by_miradors_own()
    {
        var source = MakeInstall();
        File.WriteAllText(Path.Combine(source, "openxr_monado.json"),
            "{\"runtime\":{\"library_path\":\"" + Path.Combine(source, "bin", "openxr_monado.dll").Replace("\\", "\\\\") + "\"}}");

        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, source, App, store);

        Assert.DoesNotContain(source, File.ReadAllText(build.Manifest));
        Assert.Null(ApprovedBuilds.Verify(build, App));
    }

    [Fact]
    public void A_manifest_changed_to_name_another_dll_is_caught()
    {
        var build = ApprovedBuilds.Approve(Finn, "p1", Commit, MakeInstall(), App, store);
        var json = """{"runtime":{"library_path":"../../evil.dll"}}""";
        File.WriteAllText(build.Manifest, json);
        var rehashed = build with { Files = new Dictionary<string, string>(build.Files) { ["openxr_monado.json"] = ApprovedBuilds.HashOf(build.Manifest) } };

        Assert.Contains("doesn't name its own DLL", ApprovedBuilds.Verify(rehashed, App));
    }

    [Fact]
    public void Approvals_are_per_user()
    {
        ApprovedBuilds.Approve(Finn, "custom", Commit, MakeInstall(), App, store);
        ApprovedBuilds.Approve(Other, "custom", Commit, MakeInstall("other"), App, store);

        Assert.NotNull(store.Get(Finn, "custom"));
        Assert.NotNull(store.Get(Other, "custom"));
        Assert.True(Directory.Exists(store.Get(Finn, "custom")!.Folder));
        Assert.Null(ApprovedBuilds.Verify(store.Get(Finn, "custom")!, App));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("S-1-5\\..")]
    [InlineData("S-1-5-21-1\n")]
    public void An_owner_that_isnt_a_sid_is_refused(string owner)
    {
        Assert.Throws<InvalidOperationException>(() => ApprovedBuilds.Approve(owner, "p1", Commit, MakeInstall(), App, store));
    }
}
