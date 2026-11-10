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

    /// <summary>
    /// Profiles under a module's former id get its current one (a renamed module, Mirador_Plan.md Task 67), so the
    /// Profiles page shows their headset and saving keeps it.
    /// </summary>
    /// <param name="renames">Each former id with the module's id now (<c>IHeadsetModule.FormerIds</c>).</param>
    public static (ProfileSet Set, bool Changed) MigrateModuleIds(ProfileSet set, IEnumerable<(string Former, string Current)> renames)
    {
        var map = renames.ToDictionary(r => r.Former, r => r.Current, StringComparer.Ordinal);
        if (!set.Profiles.Any(p => p.ModuleId is { } id && map.ContainsKey(id)))
        {
            return (set, false);
        }

        return (set with
        {
            Profiles = [.. set.Profiles.Select(p => p.ModuleId is { } id && map.TryGetValue(id, out var current) ? p with { ModuleId = current } : p)],
        }, true);
    }
}
