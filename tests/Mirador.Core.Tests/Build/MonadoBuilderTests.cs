using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Build;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Core.Tests.Monado;

namespace Mirador.Core.Tests.Build;

public class MonadoBuilderTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly TempDir dir = new();
    private readonly FakeCommandRunner runner = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero));
    private readonly MonadoLocator locator;
    private readonly MonadoBuilder builder;

    private static readonly Profile Fork = new(
        "p1", "Fork", null,
        BuildSource.FromBranch("https://gitlab.freedesktop.org/ultradev/monado.git", "pimax-p3c", ["-DXRT_HAVE_SDL2=OFF"]),
        [], new Dictionary<string, string>());

    public MonadoBuilderTests()
    {
        locator = new MonadoLocator(dir.File("app", "monado"), dir.File("Builds"));
        Directory.CreateDirectory(dir.File("system"));
        File.WriteAllBytes(dir.File("system", "KERNEL32.dll"), TestPe.Build());
        var tools = new BuildTools(dir.File("app", "tools", "build-monado.ps1"), null, "powershell.exe");
        builder = new MonadoBuilder(runner, locator, tools, new BuildChecks(runner, dir.File("system"), time), time);
        runner.Answer = spec => spec.Arguments.SequenceEqual(["rev-parse", "HEAD"]) ? ([Commit], 0) : ([], 0);
        runner.Effect = (spec, _) =>
        {
            if (spec.FileName == "powershell.exe")
            {
                WriteInstall(ArgumentAfter(spec, "-Install"));
            }

            return Task.CompletedTask;
        };
    }

    public void Dispose() => dir.Dispose();

    private static string ArgumentAfter(CommandSpec spec, string name) => spec.Arguments[spec.Arguments.ToList().IndexOf(name) + 1];

    private static void WriteInstall(string folder, string marker = "new")
    {
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllBytes(Path.Combine(folder, "bin", "openxr_monado.dll"), TestPe.Build("KERNEL32.dll"));
        File.WriteAllBytes(Path.Combine(folder, "bin", "monado-service.exe"), TestPe.Build("KERNEL32.dll"));
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"), TestManifest.Json(Path.Combine("bin", "openxr_monado.dll")));
        File.WriteAllText(Path.Combine(folder, "marker.txt"), marker);
    }

    private List<string> Lines { get; } = [];

    private Task<BuildOutcome> Build(CancellationToken ct = default) => builder.BuildAsync(Fork, Lines.Add, ct);

    [Fact]
    public async Task Miradors_own_tools_are_used_once_they_are_there_without_a_restart()
    {
        var toolsDir = dir.File("tools");
        var withTools = new MonadoBuilder(
            runner, locator, new BuildTools(dir.File("app", "tools", "build-monado.ps1"), toolsDir, "powershell.exe"),
            new BuildChecks(runner, dir.File("system"), time), time);

        await withTools.BuildAsync(Fork, Lines.Add, CancellationToken.None);
        Assert.DoesNotContain("-Tools", runner.Commands.Last(c => c.FileName == "powershell.exe").Arguments);

        Directory.CreateDirectory(toolsDir);
        await withTools.BuildAsync(Fork, Lines.Add, CancellationToken.None);
        Assert.Equal(toolsDir, ArgumentAfter(runner.Commands.Last(c => c.FileName == "powershell.exe"), "-Tools"));
    }

    [Fact]
    public async Task A_build_fetches_the_branch_runs_the_recipe_and_installs_it_with_its_checks()
    {
        var outcome = await Build();

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(Commit, outcome.Commit);
        var git = runner.Commands.Where(c => c.FileName == "git").Select(c => string.Join(' ', c.Arguments.SkipWhile(a => a == "-c" || a.Contains('=')))).ToList();
        Assert.Contains("init -q", git);
        Assert.Contains("remote add origin https://gitlab.freedesktop.org/ultradev/monado.git", git);
        Assert.Contains("fetch --depth 1 --no-tags origin pimax-p3c", git);
        Assert.Contains("checkout -q --force --detach FETCH_HEAD", git);
        Assert.All(runner.Commands.Where(c => c.FileName == "git"), c => Assert.Equal("0", c.Environment!["GIT_TERMINAL_PROMPT"]));

        var recipe = runner.Commands.Single(c => c.FileName == "powershell.exe");
        Assert.Equal(dir.File("app", "tools", "build-monado.ps1"), ArgumentAfter(recipe, "-File"));
        Assert.Equal(dir.File("Builds", "p1", "src"), ArgumentAfter(recipe, "-Source"));
        Assert.Equal(dir.File("Builds", "p1", "build"), ArgumentAfter(recipe, "-Build"));
        Assert.Equal(dir.File("Builds", "p1", "install.new"), ArgumentAfter(recipe, "-Install"));
        Assert.Equal(["-DXRT_HAVE_SDL2=OFF"], File.ReadAllLines(ArgumentAfter(recipe, "-ExtraCMakeOptionsFile")));
        Assert.DoesNotContain("-Tools", recipe.Arguments);

        Assert.Equal("new", File.ReadAllText(dir.File("Builds", "p1", "install", "marker.txt")));
        Assert.False(Directory.Exists(dir.File("Builds", "p1", "install.new")));
        Assert.Equal(Commit, BuildCheckResult.Load(dir.File("Builds", "p1", "install"))!.Commit);
        Assert.Equal(dir.File("Builds", "p1", "install", "openxr_monado.json"), locator.Locate(Fork).Manifest);
        Assert.Contains("Building https://gitlab.freedesktop.org/ultradev/monado.git pimax-p3c", File.ReadAllText(outcome.Log));
        Assert.Equal(outcome.Log, builder.LastLog("p1"));
        Assert.Contains(Lines, l => l.StartsWith("Built 0123456789.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Locks_a_stopped_git_left_are_removed_before_the_fetch()
    {
        // A cancelled build kills git mid-fetch: its shallow.lock (or index.lock) would fail every later fetch.
        var git = dir.File("Builds", "p1", "src", ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "shallow.lock"), "");
        File.WriteAllText(Path.Combine(git, "index.lock"), "");
        File.WriteAllText(Path.Combine(git, "config"), "kept");

        var outcome = await Build();

        Assert.True(outcome.Ok, outcome.Message);
        Assert.False(File.Exists(Path.Combine(git, "shallow.lock")));
        Assert.False(File.Exists(Path.Combine(git, "index.lock")));
        Assert.True(File.Exists(Path.Combine(git, "config")));
        Assert.Contains(Lines, l => l.Contains("shallow.lock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_build_keeps_the_last_good_install()
    {
        WriteInstall(dir.File("Builds", "p1", "install"), "old");
        runner.Answer = spec => spec.FileName == "powershell.exe" ? (["error C2065"], 1) : spec.Arguments.SequenceEqual(["rev-parse", "HEAD"]) ? ([Commit], 0) : ([], 0);

        var outcome = await Build();

        Assert.False(outcome.Ok);
        Assert.Contains("exit code 1", outcome.Message);
        Assert.Equal("old", File.ReadAllText(dir.File("Builds", "p1", "install", "marker.txt")));
        Assert.False(Directory.Exists(dir.File("Builds", "p1", "install.new")));
        Assert.Contains("error C2065", File.ReadAllText(outcome.Log));
    }

    [Fact]
    public async Task A_failed_fetch_runs_no_recipe()
    {
        runner.Answer = spec => spec.Arguments.Contains("fetch") ? (["fatal: couldn't find remote ref pimax-p3c"], 128) : ([], 0);

        var outcome = await Build();

        Assert.False(outcome.Ok);
        Assert.Contains("couldn't be fetched", outcome.Message);
        Assert.DoesNotContain(runner.Commands, c => c.FileName == "powershell.exe");
    }

    [Fact]
    public async Task A_cancelled_build_keeps_the_last_good_install()
    {
        WriteInstall(dir.File("Builds", "p1", "install"), "old");
        using var cts = new CancellationTokenSource();
        runner.Effect = (spec, ct) =>
        {
            if (spec.FileName == "powershell.exe")
            {
                WriteInstall(ArgumentAfter(spec, "-Install"));
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(cts.Token));

        Assert.Equal("old", File.ReadAllText(dir.File("Builds", "p1", "install", "marker.txt")));
        Assert.False(Directory.Exists(dir.File("Builds", "p1", "install.new")));
        Assert.False(builder.IsBuilding("p1"));
    }

    [Fact]
    public async Task A_source_mirador_wouldnt_clone_runs_nothing()
    {
        var local = Fork with { Monado = BuildSource.FromBranch(@"C:\Users\finn\monado", "main") };

        var outcome = await builder.BuildAsync(local, Lines.Add, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task One_build_at_a_time_per_profile()
    {
        var release = new TaskCompletionSource();
        runner.Effect = async (spec, _) =>
        {
            if (spec.FileName == "powershell.exe")
            {
                await release.Task;
                WriteInstall(ArgumentAfter(spec, "-Install"));
            }
        };

        var first = Build();
        Assert.True(builder.IsBuilding("p1"));
        await Assert.ThrowsAsync<MiradorException>(() => Build());
        release.SetResult();

        Assert.True((await first).Ok);
        Assert.False(builder.IsBuilding("p1"));
    }
}
