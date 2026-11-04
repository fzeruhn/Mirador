using Mirador.Core.Monado;
using Mirador.Core.Runtime;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Runtime;

public class RuntimeSwitchTests
{
    private const string Manifest = @"C:\Monado\build\openxr_monado-dev.json";

    private sealed class FakeHelper(HelperReply reply) : IHelperClient
    {
        public List<HelperCommand> Sent { get; } = [];

        public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct)
        {
            Sent.Add(command);
            return Task.FromResult(reply);
        }
    }

    private sealed class FakeElevated(int exitCode)
    {
        public List<string> Manifests { get; } = [];

        public Task<int> Run(string manifest)
        {
            Manifests.Add(manifest);
            return Task.FromResult(exitCode);
        }
    }

    private static MonadoInstall Install(bool bundled) =>
        new(Manifest, @"C:\Monado\build\openxr_monado.dll", @"C:\Monado\build\monado-service.exe", @"C:\Monado\build\service", bundled);

    [Fact]
    public async Task The_bundled_monado_goes_through_the_helper_and_never_asks_for_a_prompt()
    {
        var helper = new FakeHelper(new HelperReply(true));
        var elevated = new FakeElevated(0);

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: true), helper, elevated.Run);

        Assert.Null(problem);
        Assert.Equal([HelperCommand.SetRuntimeBundled], helper.Sent);
        Assert.Empty(elevated.Manifests);
    }

    [Fact]
    public async Task A_helper_that_refuses_gives_its_message_back()
    {
        var helper = new FakeHelper(new HelperReply(false, "Mirador's helper service isn't running. Reinstall Mirador."));

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: true), helper, new FakeElevated(0).Run);

        Assert.Equal("Mirador's helper service isn't running. Reinstall Mirador.", problem);
    }

    [Fact]
    public async Task A_helper_that_refuses_without_a_message_still_gives_a_problem()
    {
        var helper = new FakeHelper(new HelperReply(false));

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: true), helper, new FakeElevated(0).Run);

        Assert.False(string.IsNullOrWhiteSpace(problem));
        Assert.Contains("unchanged", problem);
    }

    [Fact]
    public async Task A_build_folder_runs_the_elevated_step_with_its_manifest_and_never_asks_the_helper()
    {
        var helper = new FakeHelper(new HelperReply(true));
        var elevated = new FakeElevated(0);

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: false), helper, elevated.Run);

        Assert.Null(problem);
        Assert.Equal([Manifest], elevated.Manifests);
        Assert.Empty(helper.Sent);
    }

    [Fact]
    public async Task A_cancelled_prompt_leaves_the_runtime_unchanged()
    {
        var problem = await RuntimeSwitch.SetAsync(Install(bundled: false), new FakeHelper(new HelperReply(true)), new FakeElevated(1223).Run);

        Assert.Equal("You cancelled the prompt; the runtime is unchanged.", problem);
    }

    [Fact]
    public async Task A_refused_manifest_says_the_build_is_not_usable()
    {
        var problem = await RuntimeSwitch.SetAsync(Install(bundled: false), new FakeHelper(new HelperReply(true)), new FakeElevated(1).Run);

        Assert.Equal("That Monado build's runtime manifest isn't usable, so the runtime is unchanged.", problem);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task Any_other_exit_code_is_reported_with_the_code(int exitCode)
    {
        var problem = await RuntimeSwitch.SetAsync(Install(bundled: false), new FakeHelper(new HelperReply(true)), new FakeElevated(exitCode).Run);

        Assert.Equal($"Setting the OpenXR runtime failed (code {exitCode}); the runtime is unchanged.", problem);
    }
}
