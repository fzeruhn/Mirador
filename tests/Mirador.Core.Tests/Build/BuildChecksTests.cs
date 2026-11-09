using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

public class BuildChecksTests
{
    private readonly FakeCommandRunner runner = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 9, 20, 0, 0, TimeSpan.Zero));

    /// <summary>A complete install: the manifest, the runtime importing a DLL beside it and one of Windows'.</summary>
    private static void WriteInstall(TempDir dir, string runtimeImports = "deps.dll")
    {
        Directory.CreateDirectory(dir.File("install", "bin"));
        Directory.CreateDirectory(dir.File("system"));
        File.WriteAllText(dir.File("install", "openxr_monado.json"),
            """{"file_format_version":"1.0.0","runtime":{"library_path":"bin\\openxr_monado.dll"}}""".Replace("\\\\", Path.DirectorySeparatorChar == '\\' ? "\\\\" : "/"));
        File.WriteAllBytes(dir.File("install", "bin", "openxr_monado.dll"), TestPe.Build([.. runtimeImports.Split(','), "KERNEL32.dll", "api-ms-win-crt-runtime-l1-1-0.dll"]));
        File.WriteAllBytes(dir.File("install", "bin", "monado-service.exe"), TestPe.Build("deps.dll", "KERNEL32.dll"));
        File.WriteAllBytes(dir.File("install", "bin", "deps.dll"), TestPe.Build("KERNEL32.dll"));
        File.WriteAllBytes(dir.File("system", "KERNEL32.dll"), TestPe.Build());
    }

    private static void WriteCache(TempDir dir, string service = "monado-service")
    {
        Directory.CreateDirectory(dir.File("build"));
        File.WriteAllLines(dir.File("build", "CMakeCache.txt"), ["# cache", "CMAKE_BUILD_TYPE:STRING=RelWithDebInfo", $"XRT_SERVICE_EXECUTABLE:STRING={service}"]);
    }

    private Task<BuildCheckResult> Run(TempDir dir, bool withBuild = true, string? ctest = "ctest") =>
        new BuildChecks(runner, dir.File("system"), time).RunAsync(
            dir.File("install"), withBuild ? dir.File("build") : null, ctest, "abc123", _ => { }, CancellationToken.None);

    [Fact]
    public async Task A_complete_build_passes_every_check_and_runs_ctest_in_the_build_folder()
    {
        using var dir = new TempDir();
        WriteInstall(dir);
        WriteCache(dir);

        var result = await Run(dir);

        Assert.True(result.AllPassed, string.Join("\n", result.Checks));
        Assert.All(result.Checks, c => Assert.Equal(CheckOutcome.Passed, c.Outcome));
        Assert.Equal("abc123", result.Commit);
        Assert.Equal(time.GetUtcNow(), result.CheckedAt);
        var ctest = Assert.Single(runner.Commands);
        Assert.Equal("ctest", ctest.FileName);
        Assert.Equal(["--test-dir", dir.File("build"), "--output-on-failure", "--timeout", "120"], ctest.Arguments);
    }

    [Fact]
    public async Task A_dll_found_nowhere_fails_and_says_who_needs_it()
    {
        using var dir = new TempDir();
        WriteInstall(dir, runtimeImports: "deps.dll,hidapi.dll");
        WriteCache(dir);

        var result = await Run(dir);

        var dlls = result.Checks.Single(c => c.Name == "DLLs");
        Assert.Equal(CheckOutcome.Failed, dlls.Outcome);
        Assert.Contains("hidapi.dll (needed by openxr_monado.dll)", dlls.Detail);
        Assert.False(result.AllPassed);
        Assert.Contains("DLLs", result.Summary());
    }

    [Fact]
    public async Task A_dll_needed_by_a_dll_beside_it_is_checked_too()
    {
        using var dir = new TempDir();
        WriteInstall(dir);
        WriteCache(dir);
        File.WriteAllBytes(dir.File("install", "bin", "deps.dll"), TestPe.Build("libusb-1.0.dll"));

        var dlls = (await Run(dir)).Checks.Single(c => c.Name == "DLLs");

        Assert.Equal(CheckOutcome.Failed, dlls.Outcome);
        Assert.Contains("libusb-1.0.dll (needed by deps.dll)", dlls.Detail);
    }

    [Fact]
    public async Task A_missing_service_and_a_manifest_naming_another_dll_fail()
    {
        using var dir = new TempDir();
        WriteInstall(dir);
        WriteCache(dir);
        File.Delete(dir.File("install", "bin", "monado-service.exe"));
        File.WriteAllBytes(dir.File("install", "other.dll"), TestPe.Build());
        File.WriteAllText(dir.File("install", "openxr_monado.json"), """{"runtime":{"library_path":"other.dll"}}""");

        var result = await Run(dir);

        Assert.Equal(CheckOutcome.Failed, result.Checks.Single(c => c.Name == "Files").Outcome);
        Assert.Contains("monado-service.exe", result.Checks.Single(c => c.Name == "Files").Detail);
        Assert.Equal(CheckOutcome.Failed, result.Checks.Single(c => c.Name == "Runtime manifest").Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("monado-service.exe")]
    public async Task A_build_without_the_service_setting_fails_that_check(string value)
    {
        using var dir = new TempDir();
        WriteInstall(dir);
        WriteCache(dir, value);

        var check = (await Run(dir)).Checks.Single(c => c.Name == "Service setting");

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("games can't start", check.Detail);
    }

    [Fact]
    public async Task Failed_unit_tests_are_named()
    {
        using var dir = new TempDir();
        WriteInstall(dir);
        WriteCache(dir);
        runner.Answer = _ => (["  1/3 Test #1: tests_a ....   Passed", "The following tests FAILED:", "\t  2 - tests_json (Failed)", "\t  3 - tests_slow (Timeout)"], 8);

        var check = (await Run(dir)).Checks.Single(c => c.Name == "Unit tests");

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Equal("Failed: tests_json (Failed), tests_slow (Timeout).", check.Detail);
    }

    [Fact]
    public async Task Without_a_build_folder_or_ctest_those_checks_are_skipped_not_failed()
    {
        using var dir = new TempDir();
        WriteInstall(dir);

        var result = await Run(dir, withBuild: false);

        Assert.True(result.AllPassed);
        Assert.Equal(CheckOutcome.Skipped, result.Checks.Single(c => c.Name == "Service setting").Outcome);
        Assert.Equal(CheckOutcome.Skipped, result.Checks.Single(c => c.Name == "Unit tests").Outcome);
        Assert.Empty(runner.Commands);

        WriteCache(dir);
        Assert.Equal(CheckOutcome.Skipped, (await Run(dir, ctest: null)).Checks.Single(c => c.Name == "Unit tests").Outcome);
    }

    [Fact]
    public async Task Ctest_that_cannot_start_fails_the_check_and_the_rest_still_run()
    {
        using var dir = new TempDir();
        WriteInstall(dir);
        WriteCache(dir);
        runner.Effect = (_, _) => throw new MiradorException("ctest.exe couldn't be started: not found.");

        var result = await Run(dir);

        Assert.Equal(CheckOutcome.Failed, result.Checks.Single(c => c.Name == "Unit tests").Outcome);
        Assert.Equal(5, result.Checks.Count);
    }

    [Fact]
    public async Task The_result_is_saved_and_loaded_from_the_install_folder()
    {
        using var dir = new TempDir();
        WriteInstall(dir);
        WriteCache(dir);
        var result = await Run(dir);

        result.Save(dir.File("install"));
        var loaded = BuildCheckResult.Load(dir.File("install"));

        Assert.NotNull(loaded);
        Assert.Equal(result.Commit, loaded.Commit);
        Assert.Equal(result.Checks, loaded.Checks);
        Assert.Contains("\"outcome\": \"Passed\"", File.ReadAllText(dir.File("install", "checks.json")));
        Assert.Null(BuildCheckResult.Load(dir.File("system")));
    }
}
