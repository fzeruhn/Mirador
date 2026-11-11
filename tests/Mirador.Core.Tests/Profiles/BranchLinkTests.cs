using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

public class BranchLinkTests
{
    [Theory]
    [InlineData("https://gitlab.freedesktop.org/ultradev/monado/-/tree/pimax-p3c", "https://gitlab.freedesktop.org/ultradev/monado.git", "pimax-p3c")]
    [InlineData("https://gitlab.freedesktop.org/ultradev/monado/-/tree/pimax-p3c?ref_type=heads", "https://gitlab.freedesktop.org/ultradev/monado.git", "pimax-p3c")]
    [InlineData("https://gitlab.freedesktop.org/monado/monado/-/merge_requests/3090", "https://gitlab.freedesktop.org/monado/monado.git", "refs/merge-requests/3090/head")]
    [InlineData("https://gitlab.freedesktop.org/monado/monado/-/merge_requests/3090/diffs", "https://gitlab.freedesktop.org/monado/monado.git", "refs/merge-requests/3090/head")]
    [InlineData("https://gitlab.freedesktop.org/monado/monado", "https://gitlab.freedesktop.org/monado/monado.git", "main")]
    [InlineData("https://gitlab.freedesktop.org/monado/monado.git", "https://gitlab.freedesktop.org/monado/monado.git", "main")]
    [InlineData("https://gitlab.com/group/sub/monado/-/tree/feature/x", "https://gitlab.com/group/sub/monado.git", "feature/x")]
    [InlineData("https://github.com/fzeruhn/monado/tree/dream-air", "https://github.com/fzeruhn/monado.git", "dream-air")]
    [InlineData("https://github.com/fzeruhn/monado/pull/7", "https://github.com/fzeruhn/monado.git", "refs/pull/7/head")]
    [InlineData("  https://github.com/fzeruhn/monado  ", "https://github.com/fzeruhn/monado.git", "main")]
    public void Links_give_the_repository_and_the_ref(string link, string url, string gitRef)
    {
        Assert.Equal((url, gitRef), BranchLink.Parse(link));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\Users\\finn\\monado")]
    [InlineData("file:///C:/Users/finn/monado")]
    [InlineData("http://gitlab.freedesktop.org/monado/monado")]
    [InlineData("ssh://git@gitlab.freedesktop.org/monado/monado.git")]
    [InlineData("git@github.com:fzeruhn/monado.git")]
    [InlineData("ext::sh -c touch% /tmp/x")]
    [InlineData("https://github.com/")]
    [InlineData("https://gitlab.freedesktop.org/monado/monado/-/issues/5")]
    [InlineData("https://github.com/a/b/tree/--upload-pack=evil")]
    [InlineData("https://github.com/fzeruhn/monado/commit/0123456789abcdef0123456789abcdef01234567")]
    [InlineData("https://github.com/fzeruhn/monado/blob/main/README.md")]
    [InlineData("https://github.com/fzeruhn/monado/releases")]
    public void Other_links_are_refused(string link)
    {
        Assert.Throws<MiradorException>(() => BranchLink.Parse(link));
    }

    [Theory]
    [InlineData("https://user:token@github.com/a/b.git")]
    [InlineData("https://localhost/a/b.git")]
    [InlineData("https://127.0.0.1/a/b.git")]
    [InlineData("-uhttps://github.com/a/b.git")]
    [InlineData("https://github.com/a/b c.git")]
    [InlineData(null)]
    public void Urls_with_credentials_local_hosts_or_options_are_refused(string? url)
    {
        Assert.NotNull(BranchLink.UrlProblem(url));
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/dream-air", true)]
    [InlineData("refs/merge-requests/12/head", true)]
    [InlineData("v25.1", true)]
    [InlineData("-x", false)]
    [InlineData("--upload-pack=x", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    [InlineData("a~1", false)]
    [InlineData("a:b", false)]
    [InlineData(".hidden", false)]
    [InlineData("x.lock", false)]
    [InlineData("a@{1}", false)]
    [InlineData("", false)]
    public void Refs_must_be_names_git_accepts_and_no_options(string gitRef, bool ok)
    {
        Assert.Equal(ok, BranchLink.RefProblem(gitRef) is null);
    }

    [Theory]
    [InlineData("-DXRT_HAVE_SDL2=OFF", true)]
    [InlineData("-DXRT_BUILD_DRIVER_PSVR:BOOL=ON", true)]
    [InlineData("-DCMAKE_CXX_FLAGS=/W3", true)]
    [InlineData("-C evil.cmake", false)]
    [InlineData("-GVisual Studio 17", false)]
    [InlineData("--build", false)]
    [InlineData("XRT_HAVE_SDL2=OFF", false)]
    [InlineData("-D=1", false)]
    public void Options_must_be_cmake_settings(string option, bool ok)
    {
        Assert.Equal(ok, BranchLink.OptionsProblem([option]) is null);
    }

    [Fact]
    public void Options_split_on_spaces_and_lines()
    {
        Assert.Equal(["-DA=1", "-DB=2", "-DC=x"], BranchLink.SplitOptions(" -DA=1\r\n-DB=2\t-DC=x \n"));
    }

    [Fact]
    public void Upstream_main_is_known_and_described()
    {
        Assert.True(BuildSource.UpstreamMain.IsUpstreamMain);
        Assert.False(BuildSource.FromBranch(BuildSource.UpstreamUrl, "main", ["-DA=1"]).IsUpstreamMain);
        Assert.Equal("upstream main, built here", BuildSource.UpstreamMain.Describe());
        Assert.Equal(BuildSource.FromBranch("https://a/b.git", "x", ["-DA=1"]), BuildSource.FromBranch("https://a/b.git", "x", ["-DA=1"]));
    }
}

public class BranchSourceStoreTests
{
    [Fact]
    public void A_branch_source_round_trips_and_only_its_own_fields_are_written()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.File("profiles.json"));
        var source = BuildSource.FromBranch("https://gitlab.freedesktop.org/ultradev/monado.git", "pimax-p3c", ["-DXRT_HAVE_SDL2=OFF"]);
        var set = ProfileSet.Defaults().Replace(ProfileSet.Defaults().Profiles[1] with { Monado = source });

        store.Save(set);
        var text = File.ReadAllText(dir.File("profiles.json"));

        Assert.Equal(source, store.Load().Profiles[1].Monado);
        Assert.Contains("\"cMakeOptions\"", text);
        Assert.DoesNotContain("isUpstreamMain", text);
        Assert.DoesNotContain("\"options\"", text);
        Assert.Contains("\"kind\": \"Mirador\"", text);
    }
}
