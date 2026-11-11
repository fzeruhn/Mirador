using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

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
