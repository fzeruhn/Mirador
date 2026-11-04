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
/// </summary>
public sealed record Profile(
    string Id,
    string Name,
    string? ModuleId,
    BuildSource Monado,
    IReadOnlyList<EnvVar> ExtraEnvironment,
    IReadOnlyDictionary<string, string> ModuleOptions)
{
    /// <summary>A new id, unique among profiles.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}
