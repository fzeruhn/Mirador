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

    /// <summary>The module option a profile named its own direct-mode layer folder with, before the layer came with Mirador.</summary>
    public const string FormerLayerFolderOption = "layerFolder";

    /// <summary>
    /// Drops the layer folder a profile could name until Mirador's own layer was the only one (2026-11-09): every profile
    /// that uses direct mode now gets the layer that comes with Mirador.
    /// </summary>
    public static (ProfileSet Set, bool Changed) DropLayerFolders(ProfileSet set)
    {
        if (!set.Profiles.Any(p => p.ModuleOptions.ContainsKey(FormerLayerFolderOption)))
        {
            return (set, false);
        }

        return (set with
        {
            Profiles =
            [
                .. set.Profiles.Select(p => p.ModuleOptions.ContainsKey(FormerLayerFolderOption)
                    ? p with { ModuleOptions = p.ModuleOptions.Where(o => o.Key != FormerLayerFolderOption).ToDictionary(o => o.Key, o => o.Value) }
                    : p),
            ],
        }, true);
    }

    /// <summary>
    /// "Run as administrator" on for the profiles the app-wide consent covered (App settings' "Run Monado as
    /// administrator" until it became a per-profile toggle, 2026-11-09): those on Mirador's or the bundled Monado. A
    /// branch or a build folder needed its own approval, so it keeps its toggle as it was. Run once, while consent is on.
    /// </summary>
    public static (ProfileSet Set, bool Changed) RunAsAdministratorFromConsent(ProfileSet set)
    {
        static bool Covered(Profile p) => p.Monado.Kind is SourceKind.Mirador or SourceKind.Bundled && !p.RunAsAdministrator;
        if (!set.Profiles.Any(Covered))
        {
            return (set, false);
        }

        return (set with { Profiles = [.. set.Profiles.Select(p => Covered(p) ? p with { RunAsAdministrator = true } : p)] }, true);
    }

    /// <summary>
    /// Moves App settings' old "Run Monado as administrator" onto the profiles' own toggles, once: the set is marked
    /// (<see cref="ProfileSet.RunAsAdministratorPerProfile"/>), and while the user's consent is on the profiles it
    /// covered get theirs on (<see cref="RunAsAdministratorFromConsent"/>). Nothing changes for a marked set, or when an
    /// install from before the mark kept in the profiles (2026-11-09 to 11-10) already moved it: its
    /// <c>settings.json</c> says so (<see cref="AppSettings.RunAsAdministratorPerProfile"/>), and the set is only marked.
    /// </summary>
    /// <param name="movedBefore">What <c>settings.json</c> says.</param>
    /// <param name="consentOn">Whether the user's consent for Monado as administrator is recorded.</param>
    public static (ProfileSet Set, bool Changed) MoveConsentOntoProfiles(ProfileSet set, bool movedBefore, bool consentOn)
    {
        if (set.RunAsAdministratorPerProfile)
        {
            return (set, false);
        }

        var moved = !movedBefore && consentOn ? RunAsAdministratorFromConsent(set).Set : set;
        return (moved with { RunAsAdministratorPerProfile = true }, true);
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
