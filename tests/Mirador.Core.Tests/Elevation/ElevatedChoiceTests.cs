using Mirador.Core.Elevation;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Elevation;

public class ElevatedChoiceTests
{
    private static Profile Custom(BuildSource? monado = null, params EnvVar[] extra) =>
        new("p", "Custom", null, monado ?? BuildSource.Mirador, extra, new Dictionary<string, string>());

    [Fact]
    public void Installed_copy_with_a_plain_bundled_profile_may_run_elevated() =>
        Assert.Null(ElevatedChoice.RunsAsYouReason(Custom(), null, installedCopy: true));

    [Fact]
    public void Another_copy_runs_as_you_whatever_the_profile()
    {
        Assert.Equal(ElevatedChoice.NotInstalledCopy, ElevatedChoice.RunsAsYouReason(Custom(), null, installedCopy: false));
        Assert.Equal("Runs as you: this copy of Mirador isn't the installed one.", ElevatedChoice.NotInstalledCopy);
    }

    [Fact]
    public void Build_folder_and_extra_variables_run_as_you()
    {
        Assert.Equal(
            ElevatedChoice.BuildFolder,
            ElevatedChoice.RunsAsYouReason(Custom(new BuildSource(SourceKind.Folder, @"C:\monado\build")), null, installedCopy: true));
        Assert.Equal(
            ElevatedChoice.ExtraVariables,
            ElevatedChoice.RunsAsYouReason(Custom(null, new EnvVar("XRT_LOG", "debug")), null, installedCopy: true));
    }

    [Fact]
    public void The_consent_is_kept_only_while_a_profile_runs_as_administrator()
    {
        Assert.False(ElevatedChoice.ConsentNeeded(new ProfileSet([Custom()], "p")));
        Assert.True(ElevatedChoice.ConsentNeeded(new ProfileSet([Custom(), Custom() with { Id = "q", RunAsAdministrator = true }], "p")));
    }
}
