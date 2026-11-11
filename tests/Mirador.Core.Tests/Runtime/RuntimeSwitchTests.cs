using Mirador.Core.Monado;
using Mirador.Core.Runtime;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Runtime;

public class RuntimeSwitchTests
{
    // Full paths on any system, so that OpenXrRuntime takes them as such.
    private static readonly string Build = Path.Combine(Path.GetTempPath(), "Monado", "build");
    private static readonly string Manifest = Path.Combine(Build, "openxr_monado-dev.json");

    private readonly FakeRegistry registry = new() { Active = Manifest };

    private OpenXrRuntime Runtime => new(registry);

    private sealed class FakeRegistry : IRuntimeRegistryReader
    {
        public string? Active { get; set; }

        public string? ActiveRuntime() => Active;
    }

    private sealed class FakeHelper(HelperReply reply) : IHelperClient
    {
        public List<HelperCommand> Sent { get; } = [];

        public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct)
        {
            Sent.Add(command);
            return Task.FromResult(reply);
        }
    }

    private static MonadoInstall Install(bool bundled) =>
        new(Manifest, Path.Combine(Build, "openxr_monado.dll"), Path.Combine(Build, "monado-service.exe"), Path.Combine(Build, "service"), bundled);

    [Fact]
    public async Task The_bundled_monado_goes_through_the_helper_and_never_asks_for_a_prompt()
    {
        var helper = new FakeHelper(new HelperReply(true));

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: true), helper, Runtime);

        Assert.Null(problem);
        Assert.Equal([HelperCommand.SetRuntimeBundled], helper.Sent);
    }

    [Fact]
    public async Task A_helper_that_refuses_gives_its_message_back()
    {
        var helper = new FakeHelper(new HelperReply(false, "Mirador's helper service isn't running. Reinstall Mirador."));

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: true), helper, Runtime);

        Assert.Equal("Mirador's helper service isn't running. Reinstall Mirador.", problem);
    }

    [Fact]
    public async Task A_helper_that_refuses_without_a_message_still_gives_a_problem()
    {
        var helper = new FakeHelper(new HelperReply(false));

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: true), helper, Runtime);

        Assert.False(string.IsNullOrWhiteSpace(problem));
        Assert.Contains("unchanged", problem);
    }

    [Fact]
    public async Task A_helper_of_another_install_that_set_its_own_monado_is_a_problem()
    {
        var other = Path.Combine(Path.GetTempPath(), "Program Files", "Mirador", "monado", "runtime", "openxr_monado.json");
        registry.Active = other;

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: true), new FakeHelper(new HelperReply(true)), Runtime);

        Assert.NotNull(problem);
        Assert.Contains(other, problem);
        Assert.Contains(Manifest, problem);
    }

    [Fact]
    public async Task A_build_folder_without_an_approval_is_never_made_the_runtime()
    {
        var helper = new FakeHelper(new HelperReply(true));

        var problem = await RuntimeSwitch.SetAsync(Install(bundled: false), helper, Runtime);

        Assert.Contains("Run as administrator", problem);
        Assert.Empty(helper.Sent);
    }

    [Fact]
    public async Task An_approved_build_goes_through_the_helper()
    {
        var helper = new FakeHelper(new HelperReply(true));

        Assert.Null(await RuntimeSwitch.SetAsync(Install(bundled: false), helper, Runtime, approvedProfileId: "p1"));
        Assert.Equal([HelperCommand.SetRuntimeApproved], helper.Sent);

        var refusing = new FakeHelper(new HelperReply(false, "The approved build changed since it was approved."));
        Assert.Equal("The approved build changed since it was approved.", await RuntimeSwitch.SetAsync(Install(bundled: false), refusing, Runtime, "p1"));
    }
}
