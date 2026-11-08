using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Monado;

/// <summary>Builds the environment variables Monado's service is started with.</summary>
public static class MonadoEnvironment
{
    /// <summary>
    /// The variables for <c>monado-service</c>, layered so that a later layer wins: <paramref name="inherited"/>, then the
    /// variables from <paramref name="settings"/>, then <paramref name="moduleFixed"/>, then the profile's own extra
    /// variables. Names are matched ignoring case; a name keeps the spelling of the layer that last set it. A variable
    /// with an empty name is skipped. An empty value removes the variable, as <c>set NAME=</c> does in a command prompt,
    /// so a profile can take away an inherited one; no variable reaches Monado empty (where <c>getenv</c> would see
    /// <c>""</c>, not "unset", and <c>XDG_RUNTIME_DIR=</c> would move Monado's pipe). The result is a new dictionary;
    /// <paramref name="inherited"/> is not changed. Pass an empty <paramref name="inherited"/> to list only what
    /// Mirador sets.
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

        MonadoSettingsEnvironment.Write(
            (name, value) => Set(env, name, value),
            settings.LighthouseWaitMs,
            settings.RenderScalePercent,
            settings.CompositorMinTimeMs,
            settings.StopWhenLastAppCloses);

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

    /// <summary>
    /// Sets a variable, or removes it when <paramref name="value"/> is empty. Removing first makes the new name's
    /// spelling replace an old one that differs only in case.
    /// </summary>
    private static void Set(Dictionary<string, string> env, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        env.Remove(name);
        if (!string.IsNullOrEmpty(value))
        {
            env[name] = value;
        }
    }
}
