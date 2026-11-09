using Mirador.Core.Profiles;

namespace Mirador.Core.Migration;

/// <summary>
/// Moves the profiles of an older install to the new Monado sources (Mirador_Plan.md Task 69): a profile on the
/// Monado that came with Mirador runs Mirador's Monado once that bundled build is gone (an install made by CI has none;
/// a local build with -Monado still has it, and its profiles stay).
/// </summary>
public static class ProfileMigration
{
    /// <summary>The set with its profiles moved, and whether any was.</summary>
    /// <param name="set">The profiles as loaded.</param>
    /// <param name="bundleGone">Whether this install has no bundled Monado (<c>{app}\monado\runtime</c>).</param>
    public static (ProfileSet Set, bool Changed) Migrate(ProfileSet set, bool bundleGone)
    {
        if (!bundleGone || set.Profiles.All(p => p.Monado.Kind != SourceKind.Bundled))
        {
            return (set, false);
        }

        return (set with
        {
            Profiles = [.. set.Profiles.Select(p => p.Monado.Kind == SourceKind.Bundled ? p with { Monado = BuildSource.Mirador } : p)],
        }, true);
    }
}
