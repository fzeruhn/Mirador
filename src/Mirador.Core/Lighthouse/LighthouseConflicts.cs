using Mirador.Core.Profiles;

namespace Mirador.Core.Lighthouse;

/// <summary>
/// Which programs hold the Lighthouse receiver, so that Monado can't use it, and when a profile needs the
/// receiver at all.
/// </summary>
public static class LighthouseConflicts
{
    /// <summary>The process names (without <c>.exe</c>) of SteamVR and OVR Lighthouse Manager, in the case they are known by.</summary>
    public static IReadOnlyList<string> ProcessNames { get; } = ["vrserver", "vrmonitor", "vrcompositor", "OVRLighthouseManager"];

    private const string EnableVariable = "STEAMVR_LH_ENABLE";

    private static readonly string[] TrueValues = ["1", "true", "on", "yes"];

    /// <summary>Which of <see cref="ProcessNames"/> are running, each once, under its own name, sorted without regard to case.</summary>
    public static IReadOnlyList<string> Find(IReadOnlyList<ProcInfo> running) =>
        ProcessNames
            .Where(name => running.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>What to tell the user when <paramref name="found"/> (what <see cref="Find"/> returned) is in the way.</summary>
    /// <exception cref="ArgumentException"><paramref name="found"/> is empty: there is nothing in the way to name.</exception>
    public static string Message(IReadOnlyList<string> found)
    {
        if (found.Count == 0)
        {
            throw new ArgumentException("There is no program to name.", nameof(found));
        }

        return found.Count == 1
            ? $"Close {found[0]} first: it uses the Lighthouse receiver."
            : $"Close {string.Join(", ", found)} first: they use the Lighthouse receiver.";
    }

    /// <summary>
    /// Whether Monado would use the Lighthouse receiver with this profile: the headset module says so, or the profile
    /// turns on Monado's SteamVR Lighthouse driver itself (<c>STEAMVR_LH_ENABLE</c>, the last entry counts).
    /// </summary>
    public static bool UsesLighthouse(Profile p, bool moduleUsesLighthouse)
    {
        if (moduleUsesLighthouse)
        {
            return true;
        }

        var setting = p.ExtraEnvironment.LastOrDefault(
            e => string.Equals(e.Name, EnableVariable, StringComparison.OrdinalIgnoreCase));
        return setting is not null && TrueValues.Contains(setting.Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
