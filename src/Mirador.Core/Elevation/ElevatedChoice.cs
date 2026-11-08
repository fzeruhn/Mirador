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
    public const string BuildFolder = "Runs as you: this profile uses a build folder.";
    public const string ExtraVariables = "Runs as you: this profile sets extra environment variables.";

    /// <summary>
    /// Why this profile runs as the user although the user allowed Monado as administrator, as Home's line; null when
    /// it may run as administrator. In order: this Mirador isn't the installed one (the helper starts Monado only for
    /// <c>Mirador.exe</c> in its own install folder: <see cref="Mirador.Ipc.InstalledMirador"/>), the profile uses a
    /// build folder, it sets extra environment variables, or its module points at a folder of its own.
    /// </summary>
    /// <param name="installedCopy">Whether this Mirador is the installed one, as the helper judges it.</param>
    public static string? RunsAsYouReason(Profile profile, IHeadsetModule? module, bool installedCopy)
    {
        if (!installedCopy)
        {
            return NotInstalledCopy;
        }

        if (profile.Monado.Kind != SourceKind.Bundled)
        {
            return BuildFolder;
        }

        if (profile.ExtraEnvironment.Count > 0)
        {
            return ExtraVariables;
        }

        return module?.UsesExtraFolder(profile) == true ? BuildFolder : null;
    }
}
