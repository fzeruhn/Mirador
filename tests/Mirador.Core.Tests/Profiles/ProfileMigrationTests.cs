using Mirador.Core.Migration;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

public class ProfileMigrationTests
{
    private static readonly ProfileSet Old = new(
        [
            new Profile("dreamair", "Dream Air", "dreamair", BuildSource.Bundled, [], new Dictionary<string, string>()),
            new Profile("dev", "Dev", null, new BuildSource(SourceKind.Folder, @"C:\monado\build"), [], new Dictionary<string, string>()),
        ],
        "dreamair");

    [Fact]
    public void Profiles_on_the_bundled_monado_move_to_miradors_once_the_bundle_is_gone()
    {
        var (set, changed) = ProfileMigration.Migrate(Old, bundleGone: true);

        Assert.True(changed);
        Assert.Equal(BuildSource.Mirador, set.Profiles[0].Monado);
        Assert.Equal(Old.Profiles[1], set.Profiles[1]);
        Assert.Equal("dreamair", set.SelectedId);
    }

    [Fact]
    public void While_the_bundle_is_there_nothing_moves()
    {
        Assert.False(ProfileMigration.Migrate(Old, bundleGone: false).Changed);
    }

    [Fact]
    public void A_set_without_bundled_profiles_is_unchanged()
    {
        var set = ProfileSet.Defaults();
        Assert.Same(set, ProfileMigration.Migrate(set, bundleGone: true).Set);
    }
}

public class ApprovedBuildChoiceTests
{
    private static Profile Branch(bool runAsAdmin) =>
        new("p1", "Fork", null, BuildSource.UpstreamMain, [], new Dictionary<string, string>(), RunAsAdministrator: runAsAdmin);

    [Fact]
    public void A_branch_runs_as_you_unless_run_as_administrator_is_on_and_its_build_approved()
    {
        Assert.Equal(Mirador.Core.Elevation.ElevatedChoice.BuildFolder, Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Branch(false), null, true, approvedBuild: true));
        Assert.Equal(Mirador.Core.Elevation.ElevatedChoice.NotApproved, Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Branch(true), null, true, approvedBuild: false));
        Assert.Null(Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(Branch(true), null, true, approvedBuild: true));
    }

    [Fact]
    public void Miradors_monado_may_run_as_administrator_without_an_approval()
    {
        var profile = new Profile("p", "P", null, BuildSource.Mirador, [], new Dictionary<string, string>());
        Assert.Null(Mirador.Core.Elevation.ElevatedChoice.RunsAsYouReason(profile, null, true));
    }

    [Fact]
    public void The_approval_step_reads_its_three_values()
    {
        var args = AppArgs.Parse(["--approve-build", "p1", "abc1234", @"C:\Users\finn\AppData\Local\Mirador\Builds\p1\install"]);
        Assert.Equal(("p1", "abc1234", @"C:\Users\finn\AppData\Local\Mirador\Builds\p1\install"), args.ApproveBuild);
        Assert.Null(AppArgs.Parse(["--approve-build", "p1"]).ApproveBuild);
    }
}
