using Mirador.Core.DirectMode;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Tests.DirectMode;

public sealed class DirectModeCheckTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly List<string> started = [];
    private readonly List<string> progress = [];
    private string? remembered = "unset";
    private DisplayCheckResult? saved;
    private (string[] Lines, int Code) answer = (["PASS: 1800 flips in 20.0 s, 0 missed"], 0);
    private bool startFails;

    public void Dispose() => dir.Dispose();

    private LayerFiles Layer() => new(dir.Path, Path.Combine(dir.Path, DirectModeLayer.CheckExeName));

    private Task Ensure(string? driver, string? lastGood)
    {
        var starter = new FakeConsoleStarter(spec =>
        {
            started.Add(spec.FileName);
            if (startFails)
            {
                throw new MiradorException("Couldn't start dreamair-display-check.exe: Access is denied.");
            }

            var check = new FakeConsole(answer.Lines);
            check.End(answer.Code);
            return check;
        });
        var check = new DirectModeCheck(() => driver, starter.Start);
        return check.EnsureAsync(Layer(), lastGood, d => remembered = d, r => saved = r, new Progress(progress), CancellationToken.None);
    }

    [Fact]
    public async Task The_same_driver_isnt_checked_again()
    {
        await Ensure("621.01", "621.01");

        Assert.Empty(started);
        Assert.Equal("unset", remembered);
    }

    [Fact]
    public async Task A_new_driver_that_passes_is_remembered()
    {
        await Ensure("621.01", "620.00");

        Assert.Equal([Path.Combine(dir.Path, DirectModeLayer.CheckExeName)], started);
        Assert.Equal("621.01", remembered);
        Assert.True(saved!.Pass);
        Assert.Contains("Checking direct mode with the new NVIDIA driver…", progress);
    }

    [Fact]
    public async Task A_new_driver_that_fails_refuses_with_what_to_do_and_isnt_remembered()
    {
        answer = (["FAIL: NVAPI call 0x1F2E3D4C is missing in this driver", "what to do: go back to driver 620.00"], 1);

        var e = await Assert.ThrowsAsync<MiradorException>(() => Ensure("621.01", "620.00"));

        Assert.Equal("Direct mode doesn't work with NVIDIA driver 621.01: NVAPI call 0x1F2E3D4C is missing in this driver. Go back to driver 620.00.", e.Message);
        Assert.Equal("unset", remembered);
        Assert.False(saved!.Pass);
    }

    [Fact]
    public async Task A_check_that_didnt_finish_says_try_again()
    {
        startFails = true;

        var e = await Assert.ThrowsAsync<MiradorException>(() => Ensure("621.01", null));

        Assert.StartsWith("Mirador couldn't check direct mode with the new NVIDIA driver 621.01:", e.Message, StringComparison.Ordinal);
        Assert.EndsWith("Try again.", e.Message, StringComparison.Ordinal);
    }

    // --- turning the layer on ---

    private static Profile WithLayerFolder(string? folder) => new(
        "p1", "P", null, BuildSource.Mirador, [],
        folder is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [DirectModeLayer.OptionKey] = folder });

    private void Touch(params string[] parts)
    {
        var file = dir.File(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
    }

    [Fact]
    public void The_bundled_layer_gets_the_layers_variables_and_no_path()
    {
        Touch("bundled", DirectModeLayer.ManifestName);
        Touch("bundled", DirectModeLayer.DllName);
        Touch("bundled", DirectModeLayer.CheckExeName);

        var env = DirectModeLayer.Environment(WithLayerFolder(null), dir.File("bundled"));

        Assert.Equal(
            [
                .. DirectModeEnvironment.Variables.Select(v => new EnvVar(v.Key, v.Value)),
                .. DirectModeEnvironment.Cleared.Select(name => new EnvVar(name, "")),
            ],
            env);
    }

    [Fact]
    public void Variables_a_shell_may_have_left_that_change_the_layer_are_taken_out_of_monados_environment()
    {
        // As start-monado.ps1 did: an empty value removes the variable from what Monado inherits.
        Assert.Equal(
            ["DISABLE_DREAMAIR_VK_LAYER", "IPC_RELAXED_CONNECTION_SECURITY", "XRT_COMPOSITOR_FORCE_NVAPI_DIRECT", "DREAMAIR_VK_LAYER_TEST_NO_EDID", "DREAMAIR_VK_LAYER_IGNORE_HOLDERS"],
            DirectModeEnvironment.Cleared);
        var inherited = new Dictionary<string, string> { ["IPC_RELAXED_CONNECTION_SECURITY"] = "1", ["PATH"] = @"C:\Windows" };
        Touch("bundled", DirectModeLayer.ManifestName);
        Touch("bundled", DirectModeLayer.DllName);
        Touch("bundled", DirectModeLayer.CheckExeName);
        var profile = WithLayerFolder(null);

        var env = Mirador.Core.Monado.MonadoEnvironment.Build(inherited, new Mirador.Core.Monado.MonadoSettings(), profile, DirectModeLayer.Environment(profile, dir.File("bundled")));

        Assert.False(env.ContainsKey("IPC_RELAXED_CONNECTION_SECURITY"));
        Assert.Equal("1", env["DREAMAIR_VK_LAYER"]);
        Assert.Equal(@"C:\Windows", env["PATH"]);
    }

    [Fact]
    public void A_layer_folder_adds_the_implicit_layer_path()
    {
        Touch("build", "layer", DirectModeLayer.ManifestName);
        Touch("build", "layer", DirectModeLayer.DllName);
        Touch("build", "check", DirectModeLayer.CheckExeName);

        var env = DirectModeLayer.Environment(WithLayerFolder(dir.File("build")), "unused");

        Assert.Equal(new EnvVar("VK_ADD_IMPLICIT_LAYER_PATH", dir.File("build", "layer")), env[^1]);
        Assert.Equal(DirectModeEnvironment.Variables.Count + DirectModeEnvironment.Cleared.Count + 1, env.Count);
    }

    [Fact]
    public void Without_a_bundled_layer_folder_the_layer_isnt_found_and_says_so()
    {
        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Environment(WithLayerFolder(null), ""));

        Assert.Contains("direct-mode layer", e.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(@"C:\layer", @"C:\layer")]
    public void A_profiles_layer_folder_is_its_option_when_not_blank(string? option, string? expected)
    {
        Assert.Equal(expected, DirectModeLayer.FolderOf(WithLayerFolder(option)));
    }

    private sealed class Progress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}
