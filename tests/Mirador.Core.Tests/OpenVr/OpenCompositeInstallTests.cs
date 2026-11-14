using System.Net;
using System.Security.Cryptography;
using Mirador.Core.Build;
using Mirador.Core.Diagnostics;
using Mirador.Core.OpenVr;
using Mirador.Core.Tests.Build;

namespace Mirador.Core.Tests.OpenVr;

public sealed class OpenCompositeInstallTests : IDisposable
{
    private const string Url = "https://github.com/fzeruhn/OpenLamina/releases/download/v2026.11.20/OpenLamina-Setup.exe";
    private const string Source = "OpenLamina 2026.11.20, commit 75342495640501953fc2c17f73eeb4d124e8528e\n";
    private static readonly byte[] Setup = [0x4D, 0x5A, 1, 2, 3, 4];

    private readonly TempDir dir = new();
    private readonly FakeCommandRunner runner = new();
    private readonly Answer answer = new(Setup);

    public OpenCompositeInstallTests()
    {
        // The installer puts the release in its folder, as OpenLamina-Setup.exe /VERYSILENT does.
        runner.Effect = (spec, _) =>
        {
            Directory.CreateDirectory(Path.Combine(Folder, "bin"));
            File.WriteAllBytes(Path.Combine(Folder, "bin", "vrclient_x64.dll"), [1]);
            File.WriteAllText(Path.Combine(Folder, "SOURCE.txt"), InstalledSource);
            return Task.CompletedTask;
        };
    }

    public void Dispose() => dir.Dispose();

    private string Folder => dir.File("Programs", "OpenLamina");

    private string Downloads => dir.File("Mirador", "tools", "download");

    private string InstalledSource { get; set; } = Source;

    private OpenCompositeInstall Install => new(Folder, runner);

    private static Release Rel(string? url = Url, string? sha = null, string tag = "v2026.11.20") =>
        new(tag, "https://github.com/fzeruhn/OpenLamina/releases/tag/" + tag, url, sha ?? Convert.ToHexStringLower(SHA256.HashData(Setup)));

    private Task<InstallOutcome> RunAsync(Release release) =>
        Install.InstallAsync(new HttpClient(answer), release, Downloads, CancellationToken.None);

    [Fact]
    public async Task The_release_installer_runs_silently_and_the_release_is_installed()
    {
        Assert.False(Install.IsInstalled);
        Assert.Equal("OpenLamina: not installed", Install.VersionLine());

        Assert.Equal(InstallOutcome.Installed, await RunAsync(Rel()));

        var run = Assert.Single(runner.Commands);
        Assert.Equal(Path.Combine(Downloads, "OpenLamina-Setup-2026.11.20.exe"), run.FileName);
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS"], run.Arguments);
        Assert.True(Install.IsInstalled);
        Assert.Equal("2026.11.20", Install.Source?.Version);
        Assert.Equal("OpenLamina: 2026.11.20 (7534249)", Install.VersionLine());
        Assert.Empty(Directory.GetFiles(Downloads)); // the installer doesn't stay
    }

    [Fact]
    public async Task An_installer_that_isnt_the_one_github_lists_is_never_run()
    {
        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel(sha: new string('0', 64))));

        Assert.Contains("OpenLamina's installer", e.Message);
        Assert.Empty(runner.Commands);
        Assert.False(Install.IsInstalled);
    }

    [Theory]
    [InlineData("http://github.com/fzeruhn/OpenLamina/releases/download/v2026.11.20/OpenLamina-Setup.exe")]
    [InlineData("https://example.com/OpenLamina-Setup.exe")]
    [InlineData(null)]
    public async Task An_installer_not_on_github_is_never_fetched(string? url)
    {
        await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel(url)));

        Assert.Empty(answer.Requested);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_game_holding_openlamina_leaves_it_as_it_is()
    {
        await RunAsync(Rel());
        runner.Commands.Clear();
        answer.Requested.Clear();

        using (new FileStream(Path.Combine(Folder, "bin", "vrclient_x64.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(Install.InUse);
            Assert.Equal(InstallOutcome.InUse, await RunAsync(Rel(tag: "v2026.11.21")));
        }

        Assert.Empty(answer.Requested);
        Assert.Empty(runner.Commands);
        Assert.False(Install.InUse);

        // A 32-bit game holds the DLL beside it.
        File.WriteAllBytes(Path.Combine(Folder, "bin", "vrclient.dll"), [2]);
        using (new FileStream(Path.Combine(Folder, "bin", "vrclient.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(Install.InUse);
        }
    }

    [Fact]
    public async Task One_install_at_a_time()
    {
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        runner.Effect = async (_, _) =>
        {
            running.SetResult();
            await release.Task;
        };
        var install = Install;
        var first = install.InstallAsync(new HttpClient(answer), Rel(), Downloads, CancellationToken.None);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var e = await Assert.ThrowsAsync<MiradorException>(() => install.InstallAsync(new HttpClient(answer), Rel(), Downloads, CancellationToken.None));

        Assert.Equal("OpenLamina is being installed already: wait for that to finish.", e.Message);
        release.SetResult();
        await Assert.ThrowsAsync<MiradorException>(() => first.WaitAsync(TimeSpan.FromSeconds(10))); // its fake put nothing in place
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task A_failed_installer_says_so()
    {
        runner.Effect = null;
        runner.Answer = _ => ([], 5);

        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel()));

        Assert.Contains("ended with code 5", e.Message);
    }

    [Fact]
    public async Task An_installer_that_put_another_release_in_place_says_so()
    {
        InstalledSource = "OpenLamina 2026.11.06, commit 75342495640501953fc2c17f73eeb4d124e8528e\n";

        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(Rel()));

        Assert.Contains("doesn't hold OpenLamina 2026.11.20", e.Message);
    }

    [Fact]
    public async Task A_release_without_an_installer_is_refused()
    {
        var e = await Assert.ThrowsAsync<MiradorException>(() => RunAsync(new Release("v2026.11.06", "https://github.com", null, null)));

        Assert.Equal("OpenLamina 2026.11.06 has no installer that Mirador can check.", e.Message);
    }

    private sealed class Answer(byte[] body) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
