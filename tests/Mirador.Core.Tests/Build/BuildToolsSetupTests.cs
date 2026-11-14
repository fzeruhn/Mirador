using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

public sealed class BuildToolsSetupTests : IDisposable
{
    private const string Pin = "9eec002df1cd109f8f104cd966a763daf9751489";
    private const string VsWhereJsonWithoutSdk = """
        [{"installationPath":"C:\\VS","installationVersion":"17.14.1","displayName":"Visual Studio Professional 2022","isComplete":true,
          "packages":[{"id":"Microsoft.VisualStudio.Component.VC.Tools.x86.x64"}]}]
        """;

    private readonly TempDir dir = new();
    private readonly FakeCommandRunner runner = new();
    private readonly FakeElevated elevated = new();
    private readonly Files files = new();
    private readonly HttpClient http;
    private readonly List<string> lines = [];

    public BuildToolsSetupTests()
    {
        http = new HttpClient(files);
        File.WriteAllText(dir.File("vswhere.exe"), "");
        runner.Answer = spec => spec.FileName.EndsWith("vswhere.exe", StringComparison.Ordinal) ? ([VsWhereJsonWithoutSdk], 0)
            : spec.Arguments.Contains("rev-parse") ? ([Pin], 0)
            : ([], 0);
        runner.Effect = (spec, _) =>
        {
            if (spec.Arguments.Contains("checkout"))
            {
                Directory.CreateDirectory(Path.Combine(spec.WorkingDirectory!, ".git"));
                Directory.CreateDirectory(Path.Combine(spec.WorkingDirectory!, "scripts", "buildsystems"));
                File.WriteAllText(Path.Combine(spec.WorkingDirectory!, ".git", "HEAD"), Pin);
                File.WriteAllText(Path.Combine(spec.WorkingDirectory!, "scripts", "buildsystems", "vcpkg.cmake"), "");
            }

            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        http.Dispose();
        dir.Dispose();
    }

    private ToolPin Tool(string name, string folder, string exe)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry(exe.Replace('\\', '/')).Open();
            stream.Write(Encoding.UTF8.GetBytes(name));
        }

        var bytes = memory.ToArray();
        var url = $"https://github.com/test/{folder}/releases/download/v1/{folder}.zip";
        files.Bodies[url] = bytes;
        return new ToolPin(name, folder, "1.0", url, Convert.ToHexStringLower(SHA256.HashData(bytes)), exe);
    }

    private BuildToolsSetup Setup(params ToolPin[] pins) => new(
        runner, http, elevated, dir.File("tools"), Pin, dir.File("vswhere.exe"), @"C:\VS Installer", _ => "CN=Microsoft Corporation, O=Microsoft Corporation", pins);

    [Fact]
    public async Task The_check_lists_visual_studio_each_tool_and_vcpkg()
    {
        var report = await Setup(Tool("Git", "git", @"cmd\git.exe"), Tool("CMake", "cmake", @"bin\cmake.exe")).CheckAsync(CancellationToken.None);

        Assert.Equal(["Visual Studio C++ tools", "Git", "CMake", "vcpkg"], report.Parts.Select(p => p.Name));
        Assert.Equal([ToolState.Fixable, ToolState.Missing, ToolState.Missing, ToolState.Missing], report.Parts.Select(p => p.State));
        Assert.False(report.AllFound);
        Assert.Equal(
            "Mirador's build tools aren't all ready (missing: Git, CMake, vcpkg; to update: Visual Studio C++ tools).",
            report.Problem());
    }

    [Fact]
    public async Task Installing_what_is_missing_does_the_tools_then_vcpkg_with_miradors_git_then_visual_studio_last()
    {
        var setup = Setup(Tool("Git", "git", @"cmd\git.exe"), Tool("CMake", "cmake", @"bin\cmake.exe"));

        var failures = await setup.InstallMissingAsync(await setup.CheckAsync(CancellationToken.None), lines.Add, CancellationToken.None);

        Assert.Empty(failures);
        var clone = runner.Commands.Single(c => c.Arguments.Contains("clone"));
        Assert.Equal(dir.File("tools", "git", "cmd", "git.exe"), clone.FileName);
        var vs = elevated.Commands.Single();
        Assert.Equal(@"C:\VS Installer\setup.exe", vs.FileName);
        Assert.Contains("Microsoft.VisualStudio.Component.Windows11SDK.26100", vs.Arguments);
        Assert.True((await setup.CheckAsync(CancellationToken.None)).Parts.Skip(1).All(p => p.State == ToolState.Found));
    }

    [Fact]
    public async Task Found_parts_are_left_alone()
    {
        var git = Tool("Git", "git", @"cmd\git.exe");
        var setup = Setup(git);
        await setup.InstallMissingAsync(await setup.CheckAsync(CancellationToken.None), lines.Add, CancellationToken.None);
        files.Requested.Clear();
        runner.Commands.Clear();
        elevated.Commands.Clear();
        runner.Answer = spec => spec.FileName.EndsWith("vswhere.exe", StringComparison.Ordinal)
            ? ([VsWhereJsonWithoutSdk.Replace("\"}]", "\"},{\"id\":\"Microsoft.VisualStudio.Component.Windows11SDK.26100\"}]", StringComparison.Ordinal)], 0)
            : ([Pin], 0);

        var report = await setup.CheckAsync(CancellationToken.None);
        var failures = await setup.InstallMissingAsync(report, lines.Add, CancellationToken.None);

        Assert.True(report.AllFound);
        Assert.Null(report.Problem());
        Assert.Empty(failures);
        Assert.Empty(files.Requested);
        Assert.Empty(elevated.Commands);
        Assert.DoesNotContain(runner.Commands, c => c.Arguments.Contains("clone") || c.Arguments.Contains("fetch"));
    }

    [Fact]
    public async Task A_new_visual_studio_comes_from_microsofts_bootstrapper()
    {
        runner.Answer = spec => spec.FileName.EndsWith("vswhere.exe", StringComparison.Ordinal) ? (["[]"], 0) : ([Pin], 0);
        files.Bodies[VsBootstrapper.Url] = Encoding.ASCII.GetBytes("MZ");
        var setup = Setup();

        await setup.InstallMissingAsync(await setup.CheckAsync(CancellationToken.None), lines.Add, CancellationToken.None);

        var ran = elevated.Commands.Single().FileName;
        Assert.Equal("vs_buildtools.exe", Path.GetFileName(ran));
        Assert.Equal(dir.File("tools", "download"), Path.GetDirectoryName(Path.GetDirectoryName(ran)));
        Assert.Contains("--wait", elevated.Commands.Single().Arguments);
        Assert.False(File.Exists(ran));
        Assert.Empty(Directory.GetFileSystemEntries(dir.File("tools", "download")));
    }

    [Fact]
    public async Task A_failing_part_is_reported_and_the_rest_still_installed()
    {
        var bad = Tool("CMake", "cmake", @"bin\cmake.exe") with { Sha256 = new string('0', 64) };
        var setup = Setup(Tool("Git", "git", @"cmd\git.exe"), bad);
        elevated.Code = 1;

        var failures = await setup.InstallMissingAsync(await setup.CheckAsync(CancellationToken.None), lines.Add, CancellationToken.None);

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("CMake", StringComparison.Ordinal) && f.Contains("SHA-256", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Visual Studio", StringComparison.Ordinal) && f.Contains('1'));
        var after = await setup.CheckAsync(CancellationToken.None);
        Assert.Equal(ToolState.Found, after.Parts.Single(p => p.Name == "Git").State);
        Assert.Equal(ToolState.Found, after.Parts.Single(p => p.Name == "vcpkg").State);
    }

    [Fact]
    public async Task Only_one_install_runs_at_a_time()
    {
        var cloning = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var inner = runner.Effect!;
        runner.Effect = async (spec, ct) =>
        {
            if (spec.Arguments.Contains("clone"))
            {
                cloning.SetResult();
                await release.Task;
            }

            await inner(spec, ct);
        };
        var setup = Setup();
        var report = await setup.CheckAsync(CancellationToken.None);

        var first = setup.InstallMissingAsync(report, lines.Add, CancellationToken.None);
        await cloning.Task;
        Assert.True(setup.IsInstalling);
        var e = await Assert.ThrowsAsync<MiradorException>(() => setup.InstallMissingAsync(report, lines.Add, CancellationToken.None));
        release.SetResult();
        await first;

        Assert.Contains("already", e.Message, StringComparison.Ordinal);
        Assert.False(setup.IsInstalling);
        Assert.Single(runner.Commands, c => c.Arguments.Contains("clone"));
    }

    [Fact]
    public async Task A_declined_prompt_says_nothing_changed()
    {
        elevated.Declined = true;
        var setup = Setup();

        var failures = await setup.InstallMissingAsync(await setup.CheckAsync(CancellationToken.None), lines.Add, CancellationToken.None);

        Assert.Contains("declined", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_install_that_wants_a_restart_says_so()
    {
        elevated.Code = 3010;
        var setup = Setup();

        var failures = await setup.InstallMissingAsync(await setup.CheckAsync(CancellationToken.None), lines.Add, CancellationToken.None);

        Assert.Empty(failures);
        Assert.Contains(lines, l => l.Contains("restart", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeElevated : IElevatedLauncher
    {
        public List<CommandSpec> Commands { get; } = [];

        public int Code { get; set; }

        public bool Declined { get; set; }

        public Task<int> RunAsync(CommandSpec spec, CancellationToken ct)
        {
            Commands.Add(spec);
            return Declined ? throw new ElevationDeclinedException() : Task.FromResult(Code);
        }
    }

    private sealed class Files : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Bodies { get; } = [];

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(Bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
