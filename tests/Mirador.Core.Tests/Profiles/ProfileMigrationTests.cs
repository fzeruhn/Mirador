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

    [Fact]
    public void A_layer_folder_from_before_the_layer_came_with_mirador_is_dropped()
    {
        var old = new ProfileSet(
            [
                new Profile("dev", "Dev", "pimax", BuildSource.Mirador, [], new Dictionary<string, string> { ["layerFolder"] = @"C:\layer\build", ["other"] = "x" }),
                Old.Profiles[1],
            ],
            "dev");

        var (set, changed) = ProfileMigration.DropLayerFolders(old);

        Assert.True(changed);
        Assert.Equal(new Dictionary<string, string> { ["other"] = "x" }, set.Profiles[0].ModuleOptions);
        Assert.Same(Old.Profiles[1], set.Profiles[1]);
        Assert.Equal("dev", set.SelectedId);
    }

    [Fact]
    public void A_set_without_layer_folders_is_unchanged()
    {
        Assert.Same(Old, ProfileMigration.DropLayerFolders(Old).Set);
    }

    [Fact]
    public void The_app_wide_consent_turns_on_run_as_administrator_for_the_profiles_it_covered()
    {
        // It covered Mirador's Monado (and the bundled one): a branch or a build folder needed its own approval.
        var old = new ProfileSet(
            [
                Old.Profiles[0],
                new Profile("mirador", "Mirador's", null, BuildSource.Mirador, [], new Dictionary<string, string>()),
                Old.Profiles[1],
            ],
            "dreamair");

        var (set, changed) = ProfileMigration.RunAsAdministratorFromConsent(old);

        Assert.True(changed);
        Assert.True(set.Profiles[0].RunAsAdministrator);
        Assert.True(set.Profiles[1].RunAsAdministrator);
        Assert.Same(Old.Profiles[1], set.Profiles[2]);
    }

    [Fact]
    public void Without_a_profile_the_consent_covered_nothing_changes()
    {
        var set = new ProfileSet([Old.Profiles[1]], "dev");
        Assert.Same(set, ProfileMigration.RunAsAdministratorFromConsent(set).Set);
    }

    [Fact]
    public void The_consent_moves_onto_an_unmarked_set_once_and_marks_it()
    {
        var (set, changed) = ProfileMigration.MoveConsentOntoProfiles(Old, movedBefore: false, consentOn: true);

        Assert.True(changed);
        Assert.True(set.RunAsAdministratorPerProfile);
        Assert.True(set.Profiles[0].RunAsAdministrator);
        Assert.False(set.Profiles[1].RunAsAdministrator);

        // Marked: a profile turned off since stays off, whatever settings.json says (or if it was lost).
        var turnedOff = set.Replace(set.Profiles[0] with { RunAsAdministrator = false });
        Assert.Same(turnedOff, ProfileMigration.MoveConsentOntoProfiles(turnedOff, movedBefore: false, consentOn: true).Set);
    }

    [Fact]
    public void Without_the_consent_an_unmarked_set_is_only_marked()
    {
        var (set, changed) = ProfileMigration.MoveConsentOntoProfiles(Old, movedBefore: false, consentOn: false);

        Assert.True(changed);
        Assert.True(set.RunAsAdministratorPerProfile);
        Assert.Equal(Old.Profiles, set.Profiles);
    }

    [Fact]
    public void A_move_settings_json_already_records_is_not_made_again()
    {
        // An install of 2026-11-09 kept the mark in settings.json: the user may have turned a toggle off since.
        var (set, changed) = ProfileMigration.MoveConsentOntoProfiles(Old, movedBefore: true, consentOn: true);

        Assert.True(changed);
        Assert.True(set.RunAsAdministratorPerProfile);
        Assert.Equal(Old.Profiles, set.Profiles);
    }

    [Fact]
    public void A_new_set_has_nothing_to_move()
    {
        var set = ProfileSet.Defaults();

        Assert.True(set.RunAsAdministratorPerProfile);
        Assert.False(ProfileMigration.MoveConsentOntoProfiles(set, movedBefore: false, consentOn: true).Changed);
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
