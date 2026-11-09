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
