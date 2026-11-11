using Mirador.Core.Profiles;
using Mirador.Core.Session;

namespace Mirador.Core.Tests.Session;

/// <summary>The table a module runs elevated with.</summary>
public class ModuleIdTests
{
    private static readonly TimeSpan Limit = Harness.Limit;

    [Fact]
    public async Task The_helper_gets_the_modules_elevated_table_id()
    {
        await using var h = new Harness(installedCopy: true);
        h.UseProfile(Harness.DreamAir with { RunAsAdministrator = true });
        h.Module.ElevatedModuleId = "fake-table";

        await h.StartRunningAsync();

        Assert.Equal("fake-table", Assert.Single(h.ElevatedStarts).Module);
    }

    [Fact]
    public async Task A_module_with_no_elevated_table_for_this_headset_runs_as_you()
    {
        await using var h = new Harness(installedCopy: true);
        h.UseProfile(Harness.DreamAir with { RunAsAdministrator = true });
        h.Module.ElevatedModuleId = null;

        var outcome = await h.StartRunningAsync();

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Empty(h.ElevatedStarts);
        Assert.Equal(
            Mirador.Core.Elevation.ElevatedChoice.NoElevatedTable,
            Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Harness.DreamAir, h.Module, installedCopy: true));
    }
}
