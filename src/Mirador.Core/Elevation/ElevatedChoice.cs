using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.Elevation;

/// <summary>
/// Whether a Start may run Monado as administrator, for a user who allowed it (Mirador.md section 4.10 items 45 and 46):
/// the one test behind both the session's choice of launcher and Home's "Runs as you" line, so the two can't drift.
/// </summary>
public static class ElevatedChoice
{
    public const string NotInstalledCopy = "Runs as you: this copy of Mirador isn't the installed one.";
    public const string BuildFolder = "Runs as you: this profile uses a build folder or a branch built here.";
    public const string ExtraVariables = "Runs as you: this profile sets extra environment variables.";
    public const string NoElevatedTable = "Runs as you: Mirador has no administrator settings for this headset yet.";
    public const string NotApproved = "Runs as you: the new build runs as you until you approve it on the Profiles page.";

    /// <summary>
    /// Whether the user's consent for Monado as administrator is still needed: while a profile has "Run as administrator"
    /// on. Without one it is taken back, so it never outlives the profiles that asked for it.
    /// </summary>
    public static bool ConsentNeeded(ProfileSet set) => set.Profiles.Any(p => p.RunAsAdministrator);

    /// <summary>
    /// Why this profile runs as the user although it has "Run as administrator" on and the user allowed it, as Home's line; null when
    /// it may run as administrator. In order: this Mirador isn't the installed one (the helper starts Monado only for
    /// <c>Mirador.exe</c> in its own install folder: <see cref="Mirador.Ipc.InstalledMirador"/>), the profile uses a
    /// build folder or a branch built here (anything but Mirador's Monado), it sets extra environment variables, or its
    /// module has no administrator settings for the headset.
    /// </summary>
    /// <param name="installedCopy">Whether this Mirador is the installed one, as the helper judges it.</param>
    /// <param name="approvedBuild">Whether the profile's current build is the one approved to run as administrator.</param>
    public static string? RunsAsYouReason(Profile profile, IHeadsetModule? module, bool installedCopy, bool approvedBuild = false)
    {
        if (!installedCopy)
        {
            return NotInstalledCopy;
        }

        // The bundled Monado lives in the admin-only install folder (Mirador.md §4.11 item 55); a branch built here or
        // a build folder is where the user can write, until it is approved.
        if (profile.Monado.Kind != SourceKind.Mirador && !(profile.RunAsAdministrator && approvedBuild))
        {
            return profile.RunAsAdministrator ? NotApproved : BuildFolder;
        }

        if (profile.ExtraEnvironment.Count > 0)
        {
            return ExtraVariables;
        }

        return module is not null && module.ElevatedModuleId is null ? NoElevatedTable : null;
    }
}
