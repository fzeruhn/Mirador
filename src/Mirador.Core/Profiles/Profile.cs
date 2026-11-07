namespace Mirador.Core.Profiles;

/// <summary>Where a Monado build comes from.</summary>
public enum SourceKind
{
    /// <summary>The build shipped with Mirador.</summary>
    Bundled,

    /// <summary>A build in a folder the user chose.</summary>
    Folder,
}

/// <summary>A Monado build: the bundled one, or the one in <see cref="Folder"/>.</summary>
public sealed record BuildSource(SourceKind Kind, string? Folder = null)
{
    public static BuildSource Bundled { get; } = new(SourceKind.Bundled);
}

/// <summary>One environment variable set for Monado.</summary>
public sealed record EnvVar(string Name, string Value);

/// <summary>
/// A named way of running Monado. <paramref name="ModuleId"/> says which headset module goes with it (null: none);
/// <paramref name="ModuleOptions"/> holds that module's own settings by key.
/// <paramref name="Lighthouse"/> is the user's choice whether Mirador switches the base stations with this profile
/// (null: decide from the profile, see <see cref="UsesBaseStations"/>).
/// </summary>
public sealed record Profile(
    string Id,
    string Name,
    string? ModuleId,
    BuildSource Monado,
    IReadOnlyList<EnvVar> ExtraEnvironment,
    IReadOnlyDictionary<string, string> ModuleOptions,
    bool? Lighthouse = null)
{
    /// <summary>A new id, unique among profiles.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Whether Mirador switches the base stations for this profile: the user's choice if there is one, else whether
    /// Monado would use the Lighthouse receiver with it (<paramref name="moduleUsesSteamVrLighthouse"/> is what its
    /// headset module says).
    /// </summary>
    public bool UsesBaseStations(bool moduleUsesSteamVrLighthouse) =>
        Lighthouse ?? Mirador.Core.Lighthouse.LighthouseConflicts.UsesLighthouse(this, moduleUsesSteamVrLighthouse);
}
