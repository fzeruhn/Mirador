using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

/// <summary>GitHub, answering from a table of URLs; anything else is a 404.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Answers { get; } = [];

    public List<string> Asked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Asked.Add(url);
        return Task.FromResult(Answers.TryGetValue(url, out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class MonadoInstallerTests : IDisposable
{
    private const string V1 = "monado-2026.11.10-1111111111";
    private const string V2 = "monado-2026.11.11-2222222222";
    private const string V3 = "monado-2026.11.12-3333333333";

    private readonly TempDir dir = new();
    private readonly FakeGitHub github = new();
    private readonly FakeRuntimeRegistry registry = new();
    private readonly List<(string Version, string Sha)> listed = [];
    private readonly MonadoInstaller installer;

    public MonadoInstallerTests()
    {
        Directory.CreateDirectory(dir.File("app", "standin"));
        File.WriteAllText(dir.File("app", "standin", "monado-service.exe"), "stand-in");
        installer = new MonadoInstaller(new HttpClient(github), dir.File("app", "monado"), dir.File("app", "standin", "monado-service.exe"), registry, "1.0");
        github.Answers[MonadoReleaseList.ApiUrl] = () => Json(ListJson());
    }

    public void Dispose() => dir.Dispose();

    private string Root => dir.File("app", "monado");

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private string ListJson() => "[" + string.Join(",", listed.Select(r =>
        $$"""{"tag_name":"{{r.Version}}","prerelease":true,"draft":false,"published_at":"2026-11-{{r.Version[15..17]}}T03:40:00Z","assets":[{"name":"{{r.Version}}.zip","browser_download_url":"https://github.com/fzeruhn/Mirador/releases/download/{{r.Version}}/{{r.Version}}.zip","digest":"sha256:{{r.Sha}}"}]}""")) + "]";

    /// <summary>Publishes a build: a zip with Monado's install layout (or <paramref name="entries"/>), listed with its hash.</summary>
    private void Publish(string version, Dictionary<string, string>? entries = null, string? listedSha = null)
    {
        entries ??= new()
        {
            ["openxr_monado.json"] = """{"runtime":{"library_path":"bin\\openxr_monado.dll"}}""",
            ["bin/openxr_monado.dll"] = version,
            ["bin/monado-service.exe"] = version,
            ["build-info.json"] = "{}",
        };
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        var bytes = memory.ToArray();
        listed.Add((version, listedSha ?? Convert.ToHexStringLower(SHA256.HashData(bytes))));
        github.Answers[$"https://github.com/fzeruhn/Mirador/releases/download/{version}/{version}.zip"] =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    [Fact]
    public async Task A_listed_build_is_installed_with_the_stand_in_and_becomes_current()
    {
        Publish(V1);

        var reply = await installer.InstallAsync(V1, CancellationToken.None);

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(V1, reply.Value);
        Assert.Equal(new MiradorMonadoVersions(V1, null), MiradorMonado.Read(Root));
        Assert.Equal("stand-in", File.ReadAllText(Path.Combine(Root, V1, "bin", "service", "monado-service.exe")));
        Assert.Equal(V1, File.ReadAllText(Path.Combine(Root, V1, "bin", "monado-service.exe")));
        Assert.Equal(Path.Combine(Root, V1, "openxr_monado.json"), installer.CurrentManifest());
        Assert.Equal(Path.Combine(Root, V1, "bin"), installer.CurrentServerDir());
        Assert.Contains(Path.Combine(Root, V1, "openxr_monado.json"), registry.Available);
        Assert.False(Directory.Exists(Path.Combine(Root, ".download", V1)));
        Assert.False(File.Exists(Path.Combine(Root, ".download", V1 + ".zip")));
    }

    [Fact]
    public async Task An_update_keeps_one_previous_version_moves_the_active_runtime_and_removes_older_ones()
    {
        Publish(V1);
        Publish(V2);
        Publish(V3);
        await installer.InstallAsync(V1, CancellationToken.None);
        registry.Active = Path.Combine(Root, V1, "openxr_monado.json");

        Assert.True((await installer.InstallAsync(V2, CancellationToken.None)).Ok);
        Assert.Equal(new MiradorMonadoVersions(V2, V1), MiradorMonado.Read(Root));
        Assert.Equal(Path.Combine(Root, V2, "openxr_monado.json"), registry.Active);

        Assert.True((await installer.InstallAsync(V3, CancellationToken.None)).Ok);
        Assert.Equal(new MiradorMonadoVersions(V3, V2), MiradorMonado.Read(Root));
        Assert.False(Directory.Exists(Path.Combine(Root, V1)));
        Assert.DoesNotContain(Path.Combine(Root, V1, "openxr_monado.json"), registry.Available);
        Assert.True(Directory.Exists(Path.Combine(Root, V2)));
    }

    [Fact]
    public async Task A_version_with_a_file_held_open_stays_whole_and_listed_and_goes_at_a_later_install()
    {
        Publish(V1);
        Publish(V2);
        Publish(V3);
        await installer.InstallAsync(V1, CancellationToken.None);
        await installer.InstallAsync(V2, CancellationToken.None);
        var held = Path.Combine(Root, V1, "bin", "openxr_monado.dll");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True((await installer.InstallAsync(V3, CancellationToken.None)).Ok);

            Assert.Equal(V1, File.ReadAllText(Path.Combine(Root, V1, "bin", "monado-service.exe")));
            Assert.True(File.Exists(Path.Combine(Root, V1, "openxr_monado.json")));
            Assert.Contains(Path.Combine(Root, V1, "openxr_monado.json"), registry.Available);
        }

        const string V4 = "monado-2026.11.13-4444444444";
        Publish(V4);
        Assert.True((await installer.InstallAsync(V4, CancellationToken.None)).Ok);
        Assert.False(Directory.Exists(Path.Combine(Root, V1)));
        Assert.DoesNotContain(Path.Combine(Root, V1, "openxr_monado.json"), registry.Available);
        Assert.Empty(Directory.GetDirectories(Path.Combine(Root, ".download"), "*.pruned-*"));
    }

    [Theory]
    [InlineData("""{"runtime":{"library_path":"D:\\a\\_temp\\monado-install\\bin\\openxr_monado.dll"}}""")]
    [InlineData("""{"runtime":{"library_path":"bin\\other.dll"}}""")]
    [InlineData("""{"runtime":{"library_path":"bin\\openxr_monado.dll","MND_libmonado_path":"..\\..\\libmonado.dll"}}""")]
    [InlineData("""{"runtime":{}}""")]
    [InlineData("not json")]
    public async Task A_download_whose_manifest_names_files_elsewhere_is_not_used(string manifest)
    {
        Publish(V1, new()
        {
            ["openxr_monado.json"] = manifest,
            ["bin/openxr_monado.dll"] = V1,
            ["bin/monado-service.exe"] = V1,
        });

        var reply = await installer.InstallAsync(V1, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Contains("manifest", reply.Error);
        Assert.Null(MiradorMonado.Read(Root).Current);
    }

    [Fact]
    public async Task A_manifest_with_monados_own_relative_paths_is_fine()
    {
        Publish(V1, new()
        {
            ["openxr_monado.json"] = """{"file_format_version":"1.0.0","runtime":{"name":"Monado","library_path":"./bin/openxr_monado.dll","MND_libmonado_path":"./bin/monado.dll"}}""",
            ["bin/openxr_monado.dll"] = V1,
            ["bin/monado-service.exe"] = V1,
        });

        var reply = await installer.InstallAsync(V1, CancellationToken.None);

        Assert.True(reply.Ok, reply.Error);
    }

    [Fact]
    public async Task An_older_installs_bundled_runtime_that_is_gone_moves_to_miradors_monado()
    {
        Publish(V1);
        registry.Active = Path.Combine(Root, "runtime", "openxr_monado.json");

        await installer.InstallAsync(V1, CancellationToken.None);

        Assert.Equal(Path.Combine(Root, V1, "openxr_monado.json"), registry.Active);
    }

    [Fact]
    public async Task Another_runtime_stays_active()
    {
        Publish(V1);
        registry.Active = @"C:\Program Files\Pimax\openxr.json";

        await installer.InstallAsync(V1, CancellationToken.None);

        Assert.Equal(@"C:\Program Files\Pimax\openxr.json", registry.Active);
    }

    [Fact]
    public async Task Roll_back_swaps_current_and_previous_and_the_active_runtime_follows()
    {
        Publish(V1);
        Publish(V2);
        await installer.InstallAsync(V1, CancellationToken.None);
        await installer.InstallAsync(V2, CancellationToken.None);
        registry.Active = Path.Combine(Root, V2, "openxr_monado.json");

        var reply = installer.RollBack();

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(new MiradorMonadoVersions(V1, V2), MiradorMonado.Read(Root));
        Assert.Equal(Path.Combine(Root, V1, "openxr_monado.json"), registry.Active);
    }

    [Fact]
    public async Task Installing_the_kept_version_again_swaps_back_without_a_download()
    {
        Publish(V1);
        Publish(V2);
        await installer.InstallAsync(V1, CancellationToken.None);
        await installer.InstallAsync(V2, CancellationToken.None);
        github.Asked.Clear();

        var reply = await installer.InstallAsync(V1, CancellationToken.None);

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(new MiradorMonadoVersions(V1, V2), MiradorMonado.Read(Root));
        Assert.Empty(github.Asked);
    }

    [Fact]
    public async Task Two_installs_at_once_take_turns()
    {
        Publish(V1);

        var replies = await Task.WhenAll(installer.InstallAsync(V1, CancellationToken.None), installer.InstallAsync(V1, CancellationToken.None));

        Assert.All(replies, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(new MiradorMonadoVersions(V1, null), MiradorMonado.Read(Root));
        Assert.True(File.Exists(Path.Combine(Root, V1, "bin", "service", "monado-service.exe")));
    }

    [Fact]
    public void Roll_back_without_an_earlier_version_says_so()
    {
        Assert.False(installer.RollBack().Ok);
    }

    [Theory]
    [InlineData("..\\..\\Windows")]
    [InlineData("monado-latest")]
    [InlineData("")]
    public async Task A_name_that_is_no_version_is_refused_without_asking_github(string version)
    {
        var reply = await installer.InstallAsync(version, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Empty(github.Asked);
    }

    [Fact]
    public async Task A_version_github_doesnt_list_is_refused()
    {
        Publish(V1);

        var reply = await installer.InstallAsync(V2, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Contains("isn't one of the bundled Monado's builds", reply.Error);
        Assert.DoesNotContain(github.Asked, u => u.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_that_doesnt_match_its_hash_is_not_used_and_the_current_build_stays()
    {
        Publish(V1);
        await installer.InstallAsync(V1, CancellationToken.None);
        Publish(V2, listedSha: new string('0', 64));

        var reply = await installer.InstallAsync(V2, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Contains("SHA-256", reply.Error);
        Assert.Equal(new MiradorMonadoVersions(V1, null), MiradorMonado.Read(Root));
        Assert.False(Directory.Exists(Path.Combine(Root, V2)));
    }

    [Fact]
    public async Task An_entry_that_would_land_outside_its_folder_is_refused()
    {
        Publish(V1, new() { ["../../evil.dll"] = "x", ["openxr_monado.json"] = "{}" });

        var reply = await installer.InstallAsync(V1, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Contains("outside its folder", reply.Error);
        Assert.False(File.Exists(dir.File("app", "evil.dll")));
        Assert.Null(MiradorMonado.Read(Root).Current);
    }

    [Fact]
    public async Task A_build_without_the_service_is_refused()
    {
        Publish(V1, new() { ["openxr_monado.json"] = "{}", ["bin/openxr_monado.dll"] = "x" });

        var reply = await installer.InstallAsync(V1, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Contains("monado-service.exe", reply.Error);
        Assert.False(Directory.Exists(Path.Combine(Root, V1)));
    }

    [Fact]
    public async Task Github_not_answering_installs_nothing()
    {
        github.Answers[MonadoReleaseList.ApiUrl] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var reply = await installer.InstallAsync(V1, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Contains("GitHub didn't answer", reply.Error);
    }

    [Fact]
    public void The_manifests_of_any_version_count_as_miradors_and_others_dont()
    {
        Assert.True(installer.IsMiradors(Path.Combine(Root, V1, "openxr_monado.json")));
        Assert.False(installer.IsMiradors(Path.Combine(Root, "runtime", "openxr_monado.json")));
        Assert.False(installer.IsMiradors(dir.File("elsewhere", V1, "openxr_monado.json")));
    }
}

public class MonadoReleaseListTests
{
    private static string Release(string tag, bool prerelease = true, bool draft = false, string? url = null, string digest = "sha256:" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12", string? name = null) =>
        $$"""{"tag_name":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},"published_at":"2026-11-10T03:40:00Z","assets":[{"name":"{{name ?? tag + ".zip"}}","browser_download_url":"{{url ?? $"https://github.com/fzeruhn/Mirador/releases/download/{tag}/{tag}.zip"}}","digest":"{{digest}}"}]}""";

    [Fact]
    public void Only_listed_builds_with_their_zip_and_digest_count()
    {
        const string good = "monado-2026.11.10-1a2b3c4d5e";
        var json = "[" + string.Join(",",
            Release(good),
            Release("v0.2.0", prerelease: false),
            Release("monado-2026.11.09-aaaaaaaaaa", prerelease: false),
            Release("monado-2026.11.08-bbbbbbbbbb", draft: true),
            Release("monado-2026.11.07-cccccccccc", url: "https://evil.example/monado.zip"),
            Release("monado-2026.11.06-dddddddddd", digest: "md5:00"),
            Release("monado-2026.11.05-eeeeeeeeee", name: "other.zip"),
            Release("monado-../../x")) + "]";

        var releases = MonadoReleaseList.Parse(json);

        var release = Assert.Single(releases);
        Assert.Equal(good, release.Version);
        Assert.Equal(new string('a', 0) + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12", release.ZipSha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Text_that_isnt_the_list_gives_none(string json)
    {
        Assert.Empty(MonadoReleaseList.Parse(json));
    }
}
