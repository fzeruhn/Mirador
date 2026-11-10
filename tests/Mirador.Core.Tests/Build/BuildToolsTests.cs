using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

public class BuildToolsTests
{
    private const string VcTools = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
    private const string Sdk = "Microsoft.VisualStudio.Component.Windows11SDK.26100";
    private const string Pin = "5d4a3dc90c0b3773f3bac81bf41297b54328f471";

    private static VsInstance Vs(string name, string version, params string[] packages) =>
        new(@"C:\VS\" + name, name, Version.Parse(version), true, new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase));

    // --- vswhere ---

    [Fact]
    public void Vswhere_output_gives_each_install_with_its_components()
    {
        const string json = """
            [{"instanceId":"a","installationPath":"C:\\BT","installationVersion":"18.10.12201.205",
              "displayName":"Visual Studio Build Tools 2026","isComplete":true,
              "packages":[{"id":"Microsoft.VisualStudio.Component.VC.Tools.x86.x64","version":"18.10"},{"id":"X"}]},
             {"instanceId":"b","installationPath":"C:\\Pro","installationVersion":"17.14.37614.0",
              "displayName":"Visual Studio Professional 2022","isComplete":false}]
            """;

        var all = VsWhere.Parse(json);

        Assert.Equal(2, all.Count);
        Assert.Equal(@"C:\BT", all[0].Path);
        Assert.Equal("Visual Studio Build Tools 2026", all[0].Name);
        Assert.Equal(new Version(18, 10, 12201, 205), all[0].Version);
        Assert.True(all[0].IsComplete);
        Assert.Contains(VcTools, all[0].Packages);
        Assert.False(all[1].IsComplete);
        Assert.Empty(all[1].Packages);
    }

    [Fact]
    public void Vswhere_output_that_isnt_json_gives_no_installs()
    {
        Assert.Empty(VsWhere.Parse("not json"));
        Assert.Empty(VsWhere.Parse(""));
    }

    // --- Visual Studio ---

    [Fact]
    public void The_newest_install_with_the_cpp_tools_and_the_sdk_is_found()
    {
        var plan = VsPlan.For([Vs("Visual Studio Professional 2022", "17.14.1", VcTools, Sdk), Vs("Visual Studio Build Tools 2026", "18.10.1", VcTools, Sdk)]);

        Assert.Equal(ToolState.Found, plan.State);
        Assert.Equal("Visual Studio Build Tools 2026", plan.Install!.Name);
        Assert.Empty(plan.Missing);
        Assert.Contains("Visual Studio Build Tools 2026", plan.Detail);
    }

    [Fact]
    public void An_install_without_the_sdk_can_be_fixed_by_adding_it()
    {
        var plan = VsPlan.For([Vs("Visual Studio Professional 2022", "17.14.1", VcTools)]);

        Assert.Equal(ToolState.Fixable, plan.State);
        Assert.Equal([Sdk], plan.Missing);
        Assert.Contains("Windows 11 SDK", plan.Detail);
    }

    [Fact]
    public void The_recipe_takes_the_newest_install_with_the_cpp_tools_so_that_one_is_checked()
    {
        // build-monado.ps1 enters vswhere -latest -requires VC.Tools: the 2026 one, whatever the 2022 one has.
        var plan = VsPlan.For([Vs("Visual Studio Professional 2022", "17.14.1", VcTools, Sdk), Vs("Visual Studio Build Tools 2026", "18.10.1", VcTools)]);

        Assert.Equal(ToolState.Fixable, plan.State);
        Assert.Equal("Visual Studio Build Tools 2026", plan.Install!.Name);
        Assert.Equal([Sdk], plan.Missing);
    }

    [Fact]
    public void Without_the_cpp_tools_anywhere_the_newest_install_gets_both()
    {
        var plan = VsPlan.For([Vs("Visual Studio Community 2022", "17.2", "Other"), Vs("Visual Studio Professional 2022", "17.14.1")]);

        Assert.Equal(ToolState.Fixable, plan.State);
        Assert.Equal("Visual Studio Professional 2022", plan.Install!.Name);
        Assert.Equal([VcTools, Sdk], plan.Missing);
    }

    [Fact]
    public void No_install_means_microsofts_build_tools_are_offered()
    {
        var plan = VsPlan.For([]);

        Assert.Equal(ToolState.Missing, plan.State);
        Assert.Null(plan.Install);
        Assert.Contains("Build Tools", plan.Detail);
    }

    [Fact]
    public void An_unfinished_install_or_one_older_than_2022_doesnt_count()
    {
        var old = Vs("Visual Studio Professional 2019", "16.11.1", VcTools, Sdk);
        var broken = Vs("Visual Studio Build Tools 2026", "18.10.1", VcTools, Sdk) with { IsComplete = false };

        Assert.Equal(ToolState.Missing, VsPlan.For([old, broken]).State);
    }

    [Fact]
    public void Modify_adds_only_whats_missing_to_that_install_through_its_own_installer()
    {
        var plan = VsPlan.For([Vs("Visual Studio Professional 2022", "17.14.1", VcTools)]);

        var command = plan.Command(@"C:\Program Files (x86)\Microsoft Visual Studio\Installer", @"C:\dl\vs_buildtools.exe");

        Assert.Equal(@"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe", command.FileName);
        // setup.exe takes no --wait (only the bootstrapper does): it is the installer, and runs until it is done.
        Assert.Equal(["modify", "--installPath", @"C:\VS\Visual Studio Professional 2022", "--add", Sdk, "--passive", "--norestart"], command.Arguments);
    }

    [Fact]
    public void A_new_install_asks_for_the_components_upstreams_ci_installs()
    {
        var command = VsPlan.For([]).Command(@"C:\Installer", @"C:\dl\vs_buildtools.exe");

        Assert.Equal(@"C:\dl\vs_buildtools.exe", command.FileName);
        Assert.Equal(
            [
                "--add", "Microsoft.VisualStudio.Component.VC.CoreBuildTools",
                "--add", "Microsoft.VisualStudio.ComponentGroup.NativeDesktop.Core",
                "--add", "Microsoft.VisualStudio.Component.Windows10SDK",
                "--add", Sdk,
                "--add", "Component.Microsoft.Windows.CppWinRT",
                "--add", VcTools,
                "--passive", "--norestart", "--wait",
            ],
            command.Arguments);
    }

    [Fact]
    public void A_found_install_has_no_command()
    {
        var plan = VsPlan.For([Vs("Visual Studio Build Tools 2026", "18.10.1", VcTools, Sdk)]);

        Assert.Throws<InvalidOperationException>(() => plan.Command(@"C:\Installer", @"C:\dl\vs_buildtools.exe"));
    }

    // --- Mirador's small tools ---

    private static ToolPin TestPin(byte[] zip, string url = "https://github.com/Kitware/CMake/releases/download/v9.9.9/cmake.zip") =>
        new("CMake", "cmake", "9.9.9", url, Convert.ToHexStringLower(SHA256.HashData(zip)), @"bin\cmake.exe");

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return memory.ToArray();
    }

    private static readonly byte[] CmakeZip = Zip(("cmake-9.9.9-windows-x86_64/bin/cmake.exe", "exe"), ("cmake-9.9.9-windows-x86_64/share/x.txt", "x"));

    [Fact]
    public void The_pinned_tools_are_https_downloads_from_github_with_a_sha256()
    {
        Assert.Equal(["git", "cmake", "ninja"], ToolPins.All.Select(p => p.Folder));
        foreach (var pin in ToolPins.All)
        {
            Assert.StartsWith("https://github.com/", pin.Url, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", pin.Sha256);
        }
    }

    [Fact]
    public void A_tool_not_downloaded_is_missing()
    {
        using var dir = new TempDir();
        var status = new BuildToolsCheck(dir.Path, Pin).Tool(TestPin(CmakeZip));

        Assert.Equal(ToolState.Missing, status.State);
        Assert.Contains("9.9.9", status.Detail);
    }

    [Fact]
    public async Task A_download_that_matches_its_pin_is_unpacked_without_its_top_folder_and_then_found()
    {
        using var dir = new TempDir();
        var pin = TestPin(CmakeZip);
        using var http = new HttpClient(new Answer(CmakeZip));

        await new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(pin, _ => { }, CancellationToken.None);

        Assert.Equal("exe", File.ReadAllText(dir.File("cmake", "bin", "cmake.exe")));
        Assert.True(File.Exists(dir.File("cmake", "share", "x.txt")));
        Assert.Equal(ToolState.Found, new BuildToolsCheck(dir.Path, Pin).Tool(pin).State);
        Assert.Equal(["cmake"], Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_tool_at_another_version_can_be_updated_and_the_update_replaces_it()
    {
        using var dir = new TempDir();
        var oldZip = Zip(("bin/cmake.exe", "old"), ("bin/old-only.txt", "o"));
        using (var http = new HttpClient(new Answer(oldZip)))
        {
            await new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(TestPin(oldZip) with { Version = "9.0.0" }, _ => { }, CancellationToken.None);
        }

        var pin = TestPin(CmakeZip);
        var status = new BuildToolsCheck(dir.Path, Pin).Tool(pin);
        Assert.Equal(ToolState.Fixable, status.State);
        Assert.Contains("9.0.0", status.Detail);

        using (var http = new HttpClient(new Answer(CmakeZip)))
        {
            await new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(pin, _ => { }, CancellationToken.None);
        }

        Assert.Equal("exe", File.ReadAllText(dir.File("cmake", "bin", "cmake.exe")));
        Assert.False(File.Exists(dir.File("cmake", "bin", "old-only.txt")));
    }

    [Fact]
    public async Task A_download_with_another_sha256_is_refused_and_leaves_nothing()
    {
        using var dir = new TempDir();
        var pin = TestPin(CmakeZip) with { Sha256 = new string('0', 64) };
        using var http = new HttpClient(new Answer(CmakeZip));

        var e = await Assert.ThrowsAsync<MiradorException>(() =>
            new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(pin, _ => { }, CancellationToken.None));

        Assert.Contains("SHA-256", e.Message);
        Assert.False(Directory.Exists(dir.File("cmake")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_zip_entry_that_would_leave_its_folder_is_refused_and_the_old_copy_stays()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("cmake", "bin"));
        File.WriteAllText(dir.File("cmake", "bin", "cmake.exe"), "kept");
        var bad = Zip(("bin/cmake.exe", "exe"), ("../../evil.txt", "x"));
        using var http = new HttpClient(new Answer(bad));

        var e = await Assert.ThrowsAsync<MiradorException>(() =>
            new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(TestPin(bad), _ => { }, CancellationToken.None));

        Assert.Contains("outside", e.Message);
        Assert.Equal("kept", File.ReadAllText(dir.File("cmake", "bin", "cmake.exe")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir.Path)!, "evil.txt")));
    }

    [Fact]
    public async Task A_zip_without_the_tools_program_is_refused()
    {
        using var dir = new TempDir();
        var empty = Zip(("readme.txt", "r"));
        using var http = new HttpClient(new Answer(empty));

        await Assert.ThrowsAsync<MiradorException>(() =>
            new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(TestPin(empty), _ => { }, CancellationToken.None));

        Assert.False(Directory.Exists(dir.File("cmake")));
    }

    [Theory]
    [InlineData("http://github.com/Kitware/CMake/releases/download/v9/cmake.zip")]
    [InlineData("https://example.com/cmake.zip")]
    [InlineData("https://github.com.example.com/cmake.zip")]
    public async Task Only_https_downloads_from_github_are_fetched(string url)
    {
        using var dir = new TempDir();
        var answer = new Answer(CmakeZip);
        using var http = new HttpClient(answer);

        await Assert.ThrowsAsync<MiradorException>(() =>
            new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(TestPin(CmakeZip, url), _ => { }, CancellationToken.None));

        Assert.Empty(answer.Requested);
    }

    [Fact]
    public async Task A_failed_download_says_so_and_leaves_nothing()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new Answer(CmakeZip, HttpStatusCode.NotFound));

        var e = await Assert.ThrowsAsync<MiradorException>(() =>
            new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin).InstallAsync(TestPin(CmakeZip), _ => { }, CancellationToken.None));

        Assert.Contains("CMake", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    // --- vcpkg ---

    private static void WriteHead(string vcpkgDir, string head)
    {
        Directory.CreateDirectory(Path.Combine(vcpkgDir, ".git"));
        Directory.CreateDirectory(Path.Combine(vcpkgDir, "scripts", "buildsystems"));
        File.WriteAllText(Path.Combine(vcpkgDir, "scripts", "buildsystems", "vcpkg.cmake"), "");
        File.WriteAllText(Path.Combine(vcpkgDir, ".git", "HEAD"), head + "\n");
    }

    [Fact]
    public void Vcpkg_at_the_pinned_commit_is_found_at_another_fixable_and_absent_missing()
    {
        using var dir = new TempDir();
        var check = new BuildToolsCheck(dir.Path, Pin);
        Assert.Equal(ToolState.Missing, check.Vcpkg().State);

        WriteHead(dir.File("vcpkg"), "0123456789012345678901234567890123456789");
        Assert.Equal(ToolState.Fixable, check.Vcpkg().State);
        Assert.Contains("0123456", check.Vcpkg().Detail);

        WriteHead(dir.File("vcpkg"), Pin);
        Assert.Equal(ToolState.Found, check.Vcpkg().State);
    }

    [Fact]
    public async Task Vcpkg_is_cloned_whole_over_https_and_checked_out_at_the_pin()
    {
        using var dir = new TempDir();
        var runner = new FakeCommandRunner
        {
            Effect = (spec, _) =>
            {
                if (spec.Arguments.Contains("clone"))
                {
                    WriteHead(spec.Arguments[^1], "ref: refs/heads/master");
                }
                else if (spec.Arguments.Contains("checkout"))
                {
                    WriteHead(spec.WorkingDirectory!, Pin);
                }

                return Task.CompletedTask;
            },
            Answer = spec => spec.Arguments.Contains("rev-parse") ? ([Pin], 0) : ([], 0),
        };
        using var http = new HttpClient(new Answer([]));

        await new ToolsInstaller(http, runner, dir.Path, Pin).InstallVcpkgAsync("git", _ => { }, CancellationToken.None);

        var clone = runner.Commands[0];
        Assert.Equal("git", clone.FileName);
        Assert.Contains("clone", clone.Arguments);
        Assert.Contains("https://github.com/microsoft/vcpkg.git", clone.Arguments);
        Assert.DoesNotContain(clone.Arguments, a => a.StartsWith("--depth", StringComparison.Ordinal) || a.StartsWith("--filter", StringComparison.Ordinal));
        Assert.Contains("credential.helper=", clone.Arguments);
        Assert.Equal("0", clone.Environment!["GIT_TERMINAL_PROMPT"]);
        Assert.Contains(runner.Commands, c => c.Arguments.Contains("checkout") && c.Arguments.Contains(Pin));
        Assert.Equal(ToolState.Found, new BuildToolsCheck(dir.Path, Pin).Vcpkg().State);
    }

    [Fact]
    public async Task An_existing_vcpkg_is_fetched_not_cloned_again()
    {
        using var dir = new TempDir();
        WriteHead(dir.File("vcpkg"), "0123456789012345678901234567890123456789");
        File.WriteAllText(dir.File("vcpkg", "vcpkg.exe"), "built from the old commit");
        var runner = new FakeCommandRunner
        {
            Effect = (spec, _) =>
            {
                if (spec.Arguments.Contains("checkout"))
                {
                    WriteHead(spec.WorkingDirectory!, Pin);
                }

                return Task.CompletedTask;
            },
            Answer = spec => spec.Arguments.Contains("rev-parse") ? ([Pin], 0) : ([], 0),
        };
        using var http = new HttpClient(new Answer([]));

        await new ToolsInstaller(http, runner, dir.Path, Pin).InstallVcpkgAsync("git", _ => { }, CancellationToken.None);

        Assert.DoesNotContain(runner.Commands, c => c.Arguments.Contains("clone"));
        Assert.Contains(runner.Commands, c => c.Arguments.Contains("fetch"));

        // The program built from the old commit's sources goes: the recipe bootstraps it again from the pinned ones.
        Assert.False(File.Exists(Path.Combine(dir.Path, "vcpkg", "vcpkg.exe")));
    }

    [Fact]
    public async Task A_vcpkg_that_ends_at_another_commit_is_refused()
    {
        using var dir = new TempDir();
        var runner = new FakeCommandRunner
        {
            Effect = (spec, _) =>
            {
                if (spec.Arguments.Contains("clone"))
                {
                    WriteHead(spec.Arguments[^1], "ref: refs/heads/master");
                }

                return Task.CompletedTask;
            },
            Answer = spec => spec.Arguments.Contains("rev-parse") ? (["1111111111111111111111111111111111111111"], 0) : ([], 0),
        };
        using var http = new HttpClient(new Answer([]));

        var e = await Assert.ThrowsAsync<MiradorException>(() =>
            new ToolsInstaller(http, runner, dir.Path, Pin).InstallVcpkgAsync("git", _ => { }, CancellationToken.None));

        Assert.Contains("vcpkg", e.Message);
        Assert.False(Directory.Exists(dir.File("vcpkg")));
    }

    [Fact]
    public async Task A_failing_git_says_which_step_failed()
    {
        using var dir = new TempDir();
        var runner = new FakeCommandRunner { Answer = _ => (["fatal: unable to access"], 128) };
        using var http = new HttpClient(new Answer([]));

        var e = await Assert.ThrowsAsync<MiradorException>(() =>
            new ToolsInstaller(http, runner, dir.Path, Pin).InstallVcpkgAsync("git", _ => { }, CancellationToken.None));

        Assert.Contains("clone", e.Message);
        Assert.False(Directory.Exists(dir.File("vcpkg")));
    }

    // --- Microsoft's bootstrapper ---

    private const string MicrosoftSubject = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Fact]
    public async Task The_bootstrapper_signed_by_microsoft_is_kept_alone_in_a_new_folder_held_until_it_is_done()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("version.dll"), "planted");
        var answer = new Answer(Encoding.ASCII.GetBytes("MZ"));
        using var http = new HttpClient(answer);

        string folder;
        using (var file = await VsBootstrapper.DownloadAsync(http, dir.Path, _ => MicrosoftSubject, CancellationToken.None))
        {
            Assert.Equal(new Uri(VsBootstrapper.Url), answer.Requested.Single());
            folder = Path.GetDirectoryName(file.Path)!;
            Assert.NotEqual(dir.Path, folder);
            Assert.Equal(["vs_buildtools.exe"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));

            // Nobody can change it between the signer check and its end.
            Assert.Throws<IOException>(() => new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            using var read = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CN=Valve Corp., O=Valve Corp.")]
    [InlineData("CN=Microsoft Corporation Evil, O=Someone")]
    [InlineData("CN=Someone, O=Microsoft Corporation")]
    [InlineData("CN=\"Microsoft Corporation, O=Microsoft Corporation\", O=Evil")]
    public async Task A_bootstrapper_not_signed_by_microsoft_is_refused_and_deleted(string? subject)
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new Answer(Encoding.ASCII.GetBytes("MZ")));

        var e = await Assert.ThrowsAsync<MiradorException>(() => VsBootstrapper.DownloadAsync(http, dir.Path, _ => subject, CancellationToken.None));

        Assert.Contains("Microsoft", e.Message);
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    // --- the swap ---

    [Fact]
    public async Task A_new_copy_that_cant_be_moved_in_puts_the_old_one_back()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("cmake", "bin"));
        File.WriteAllText(dir.File("cmake", "bin", "cmake.exe"), "old");
        using var http = new HttpClient(new Answer(CmakeZip));
        var installer = new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin, (from, to) =>
        {
            if (from.EndsWith(".new", StringComparison.Ordinal))
            {
                throw new IOException("in use by a scanner");
            }

            Directory.Move(from, to);
        });

        var e = await Assert.ThrowsAsync<MiradorException>(() => installer.InstallAsync(TestPin(CmakeZip), _ => { }, CancellationToken.None));

        Assert.Contains("CMake", e.Message);
        Assert.Equal("old", File.ReadAllText(dir.File("cmake", "bin", "cmake.exe")));
        Assert.Equal(["cmake"], Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_move_that_fails_for_a_moment_is_tried_again()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new Answer(CmakeZip));
        var failures = 2;
        var installer = new ToolsInstaller(http, new FakeCommandRunner(), dir.Path, Pin, (from, to) =>
        {
            if (failures-- > 0)
            {
                throw new IOException("in use for a moment");
            }

            Directory.Move(from, to);
        });

        await installer.InstallAsync(TestPin(CmakeZip), _ => { }, CancellationToken.None);

        Assert.Equal("exe", File.ReadAllText(dir.File("cmake", "bin", "cmake.exe")));
    }

    [Fact]
    public void Without_a_vcpkg_pin_the_check_says_so()
    {
        using var dir = new TempDir();

        var status = new BuildToolsCheck(dir.Path, "").Vcpkg();

        Assert.Equal(ToolState.Missing, status.State);
        Assert.Contains("vcpkg-commit.txt", status.Detail);
    }

    private sealed class Answer(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }
}
