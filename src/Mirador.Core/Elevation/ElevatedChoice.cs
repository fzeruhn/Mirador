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
    /// Why this profile runs as the user although the user allowed Monado as administrator, as Home's line; null when
    /// it may run as administrator. In order: this Mirador isn't the installed one (the helper starts Monado only for
    /// <c>Mirador.exe</c> in its own install folder: <see cref="Mirador.Ipc.InstalledMirador"/>), the profile uses a
    /// build folder or a branch built here (anything but Mirador's Monado), it sets extra environment variables, or its module points at a folder of its own.
    /// </summary>
    /// <param name="installedCopy">Whether this Mirador is the installed one, as the helper judges it.</param>
    /// <param name="approvedBuild">Whether the profile's current build is the one approved to run as administrator.</param>
    public static string? RunsAsYouReason(Profile profile, IHeadsetModule? module, bool installedCopy, bool approvedBuild = false)
    {
        if (!installedCopy)
        {
            return NotInstalledCopy;
        }

        // Mirador's Monado lives in the admin-only install folder, as the bundled one did (Mirador.md §4.11 item 55); a
        // branch built here or a build folder is where the user can write (approving one is Task 65).
        if (profile.Monado.Kind is not (SourceKind.Bundled or SourceKind.Mirador) && !(profile.RunAsAdministrator && approvedBuild))
        {
            return profile.RunAsAdministrator ? NotApproved : BuildFolder;
        }

        if (profile.ExtraEnvironment.Count > 0)
        {
            return ExtraVariables;
        }

        if (module is not null && module.ElevatedModuleId is null)
        {
            return NoElevatedTable;
        }

        return module?.UsesExtraFolder(profile) == true ? BuildFolder : null;
    }
}
