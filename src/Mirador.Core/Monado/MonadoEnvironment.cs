using System.Globalization;
using Mirador.Core.Profiles;

namespace Mirador.Core.Monado;

/// <summary>Builds the environment variables Monado's service is started with.</summary>
public static class MonadoEnvironment
{
    /// <summary>
    /// The variables for <c>monado-service</c>, layered so that a later layer wins: <paramref name="inherited"/>, then the
    /// variables from <paramref name="settings"/>, then <paramref name="moduleFixed"/>, then the profile's own extra
    /// variables. Names are matched ignoring case; a name keeps the spelling of the layer that last set it. A variable
    /// with an empty name is skipped. The result is a new dictionary; <paramref name="inherited"/> is not changed.
    /// Pass an empty <paramref name="inherited"/> to list only what Mirador sets.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> inherited,
        MonadoSettings settings,
        Profile profile,
        IReadOnlyList<EnvVar> moduleFixed)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in inherited)
        {
            Set(env, name, value);
        }

        Set(env, "LH_DISCOVER_WAIT_MS", Number(settings.LighthouseWaitMs));
        Set(env, "XRT_COMPOSITOR_SCALE_PERCENTAGE", Number(settings.RenderScalePercent));
        Set(env, "U_PACING_COMP_MIN_TIME_MS", Number(settings.CompositorMinTimeMs));
        if (settings.StopWhenLastAppCloses)
        {
            Set(env, "IPC_EXIT_ON_DISCONNECT", "1");
        }

        foreach (var v in moduleFixed)
        {
            Set(env, v.Name, v.Value);
        }

        foreach (var v in profile.ExtraEnvironment)
        {
            Set(env, v.Name, v.Value);
        }

        return env;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Sets a variable. Removing first makes the new name's spelling replace an old one that differs only in case.</summary>
    private static void Set(Dictionary<string, string> env, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        env.Remove(name);
        env[name] = value;
    }
}
