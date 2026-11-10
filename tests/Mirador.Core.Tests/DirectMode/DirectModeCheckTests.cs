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
                throw new MiradorException("Couldn't start mirador-display-check.exe: Access is denied.");
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

    private static Profile Profile() => new("p1", "P", null, BuildSource.Mirador, [], new Dictionary<string, string>());

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

        var env = DirectModeLayer.Environment(dir.File("bundled"));

        Assert.Equal(
            [
                .. DirectModeEnvironment.Variables.Select(v => new EnvVar(v.Key, v.Value)),
                .. DirectModeEnvironment.Cleared.Select(name => new EnvVar(name, "")),
            ],
            env);
    }

    [Fact]
    public void The_layers_five_variables_are_these()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["MIRADOR_DIRECT_MODE"] = "1",
                ["DISABLE_RTSS_LAYER"] = "1",
                ["VK_LOADER_LAYERS_ALLOW"] = "VK_LAYER_MIRADOR_direct_mode",
                ["XRT_COMPOSITOR_FORCE_VK_DISPLAY"] = "0",
                ["XRT_COMPOSITOR_USE_PRESENT_WAIT"] = "1",
            },
            DirectModeEnvironment.Variables.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void Variables_a_shell_may_have_left_that_change_the_layer_are_taken_out_of_monados_environment()
    {
        // As start-monado.ps1 did: an empty value removes the variable from what Monado inherits.
        // Under both names: the layer still reads the Dream Air ones for one release.
        Assert.Equal(
            [
                "DISABLE_MIRADOR_DIRECT_MODE", "IPC_RELAXED_CONNECTION_SECURITY", "XRT_COMPOSITOR_FORCE_NVAPI_DIRECT",
                "MIRADOR_DIRECT_MODE_TEST_NO_EDID", "MIRADOR_DIRECT_MODE_IGNORE_HOLDERS",
                "DISABLE_DREAMAIR_VK_LAYER", "DREAMAIR_VK_LAYER_TEST_NO_EDID", "DREAMAIR_VK_LAYER_IGNORE_HOLDERS",
            ],
            DirectModeEnvironment.Cleared);
        var inherited = new Dictionary<string, string> { ["IPC_RELAXED_CONNECTION_SECURITY"] = "1", ["PATH"] = @"C:\Windows" };
        Touch("bundled", DirectModeLayer.ManifestName);
        Touch("bundled", DirectModeLayer.DllName);
        Touch("bundled", DirectModeLayer.CheckExeName);
        var env = Mirador.Core.Monado.MonadoEnvironment.Build(inherited, new Mirador.Core.Monado.MonadoSettings(), Profile(), DirectModeLayer.Environment(dir.File("bundled")));

        Assert.False(env.ContainsKey("IPC_RELAXED_CONNECTION_SECURITY"));
        Assert.Equal("1", env["MIRADOR_DIRECT_MODE"]);
        Assert.Equal(@"C:\Windows", env["PATH"]);
    }

    [Fact]
    public void Without_a_bundled_layer_folder_the_layer_isnt_found_and_says_so()
    {
        var e = Assert.Throws<MiradorException>(() => DirectModeLayer.Environment(""));

        Assert.Contains("direct-mode layer", e.Message);
    }

    private sealed class Progress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}
