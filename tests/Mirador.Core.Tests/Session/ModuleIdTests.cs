using Mirador.Core.Migration;
using Mirador.Core.Profiles;
using Mirador.Core.Session;

namespace Mirador.Core.Tests.Session;

/// <summary>A module's former ids (a renamed module, Mirador_Plan.md Task 67) and the table it runs elevated with.</summary>
public class ModuleIdTests
{
    private static readonly TimeSpan Limit = Harness.Limit;

    [Fact]
    public async Task What_a_session_left_under_a_modules_former_id_is_recovered_by_that_module()
    {
        await using var h = new Harness();
        h.Module.FormerIds = ["fake-before"];
        IReadOnlyDictionary<string, string> data = new Dictionary<string, string> { ["wasRunning"] = "true" };
        new SessionFile(h.Paths.SessionFile).Write("p1", "fake-before", data);

        await h.Controller.RecoverAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal(data, h.Module.Recovered);
        Assert.False(File.Exists(h.Paths.SessionFile));
    }

    [Fact]
    public async Task A_profile_with_a_modules_former_id_starts_with_that_module()
    {
        await using var h = new Harness();
        h.Module.FormerIds = ["fake-before"];
        h.UseProfile(Harness.DreamAir with { ModuleId = "fake-before" });

        var outcome = await h.StartRunningAsync();

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(1, h.Module.BeforeStartCalls);
    }

    [Fact]
    public async Task The_helper_gets_the_modules_elevated_table_id()
    {
        await using var h = new Harness(installedCopy: true);
        h.Module.ElevatedModuleId = "fake-table";

        await h.StartRunningAsync();

        Assert.Equal("fake-table", Assert.Single(h.ElevatedStarts).Module);
    }

    [Fact]
    public async Task A_module_with_no_elevated_table_for_this_headset_runs_as_you()
    {
        await using var h = new Harness(installedCopy: true);
        h.Module.ElevatedModuleId = null;

        var outcome = await h.StartRunningAsync();

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Empty(h.ElevatedStarts);
        Assert.Equal(
            Mirador.Core.Elevation.ElevatedChoice.NoElevatedTable,
            Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Harness.DreamAir, h.Module, installedCopy: true));
    }

    [Fact]
    public void Profiles_under_a_former_module_id_move_to_the_current_one()
    {
        var set = new ProfileSet(
            [
                new Profile("a", "A", "dreamair", BuildSource.Mirador, [], new Dictionary<string, string>()),
                new Profile("b", "B", null, BuildSource.Mirador, [], new Dictionary<string, string>()),
                new Profile("c", "C", "other", BuildSource.Mirador, [], new Dictionary<string, string>()),
            ],
            "a");

        var (moved, changed) = ProfileMigration.MigrateModuleIds(set, [("dreamair", "pimax")]);

        Assert.True(changed);
        Assert.Equal(["pimax", null, "other"], moved.Profiles.Select(p => p.ModuleId));
        Assert.False(ProfileMigration.MigrateModuleIds(moved, [("dreamair", "pimax")]).Changed);
    }
}
