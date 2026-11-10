using System.Text.Json.Serialization;

namespace Mirador.Core.Profiles;

/// <summary>Where a profile's Monado comes from (Mirador.md §4.11 item 55).</summary>
public enum SourceKind
{
    /// <summary>
    /// The build an older Mirador installed with itself (<c>{app}\monado\runtime</c>). Read from older profile files
    /// only, and moved to <see cref="Mirador"/> when Mirador first runs after the update (Mirador_Plan.md Task 69).
    /// </summary>
    Bundled,

    /// <summary>A build in a folder the user chose: a CMake build folder or an install folder.</summary>
    Folder,

    /// <summary>
    /// Mirador's Monado: the weekly build of upstream <c>main</c> from Mirador's releases, installed by the helper in
    /// <c>{app}\monado\&lt;version&gt;</c>. The default.
    /// </summary>
    Mirador,

    /// <summary>
    /// A branch or merge request Mirador builds itself: <see cref="BuildSource.Url"/>, <see cref="BuildSource.Ref"/> and
    /// the extra <see cref="BuildSource.CMakeOptions"/> (Mirador.md §4.11 item 49).
    /// </summary>
    Branch,
}

/// <summary>A profile's Monado: one of the kinds of <see cref="SourceKind"/> and what that kind needs.</summary>
/// <param name="Kind">Where it comes from.</param>
/// <param name="Folder">For <see cref="SourceKind.Folder"/>: the build or install folder.</param>
/// <param name="Url">For <see cref="SourceKind.Branch"/>: the repository's HTTPS URL.</param>
/// <param name="Ref">For <see cref="SourceKind.Branch"/>: the branch, or a ref such as <c>refs/merge-requests/12/head</c>.</param>
/// <param name="CMakeOptions">For <see cref="SourceKind.Branch"/>: extra <c>-D</c> options, after Mirador's own.</param>
public sealed record BuildSource(
    SourceKind Kind,
    string? Folder = null,
    string? Url = null,
    string? Ref = null,
    IReadOnlyList<string>? CMakeOptions = null)
{
    /// <summary>Upstream Monado's repository.</summary>
    public const string UpstreamUrl = "https://gitlab.freedesktop.org/monado/monado.git";

    /// <summary>Upstream's main branch.</summary>
    public const string UpstreamRef = "main";

    /// <summary>The build an older Mirador installed (<see cref="SourceKind.Bundled"/>).</summary>
    public static BuildSource Bundled { get; } = new(SourceKind.Bundled);

    /// <summary>Mirador's Monado (<see cref="SourceKind.Mirador"/>), the default.</summary>
    public static BuildSource Mirador { get; } = new(SourceKind.Mirador);

    /// <summary>"Build upstream main": a branch source with upstream's URL and <c>main</c>.</summary>
    public static BuildSource UpstreamMain { get; } = new(SourceKind.Branch, Url: UpstreamUrl, Ref: UpstreamRef);

    /// <summary>A branch source.</summary>
    public static BuildSource FromBranch(string url, string gitRef, IReadOnlyList<string>? options = null) =>
        new(SourceKind.Branch, Url: url, Ref: gitRef, CMakeOptions: options is { Count: > 0 } ? [.. options] : null);

    /// <summary>The extra CMake options, none when unset.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Options => CMakeOptions ?? [];

    /// <summary>Whether this is upstream's <c>main</c>, built here ("Build upstream main").</summary>
    [JsonIgnore]
    public bool IsUpstreamMain =>
        Kind == SourceKind.Branch && Url == UpstreamUrl && Ref == UpstreamRef && Options.Count == 0;

    /// <summary>What it is, in a few words, for the profile and the diagnostics.</summary>
    public string Describe() => Kind switch
    {
        SourceKind.Mirador => "the bundled Monado",
        SourceKind.Bundled => "the Monado an older Mirador installed",
        SourceKind.Folder => $"build folder {Folder}",
        SourceKind.Branch when IsUpstreamMain => "upstream main, built here",
        SourceKind.Branch => $"{Url} {Ref}, built here",
        _ => Kind.ToString(),
    };

    /// <summary>Equal when every field is: the options compared item by item, not as lists.</summary>
    public bool Equals(BuildSource? other) =>
        other is not null && Kind == other.Kind && Folder == other.Folder && Url == other.Url && Ref == other.Ref
        && Options.SequenceEqual(other.Options);

    public override int GetHashCode() => HashCode.Combine(Kind, Folder, Url, Ref, Options.Count);
}

/// <summary>One environment variable set for Monado.</summary>
public sealed record EnvVar(string Name, string Value);

/// <summary>
/// A named way of running Monado. <paramref name="ModuleId"/> says which headset module goes with it (null: none);
/// <paramref name="ModuleOptions"/> holds that module's own settings by key.
/// <paramref name="Lighthouse"/> is the user's choice whether Mirador switches the base stations with this profile
/// (null: decide from the profile, see <see cref="UsesBaseStations"/>). <paramref name="RunAsAdministrator"/> is "Run as
/// administrator" for a branch or build folder (Mirador.md §4.11 item 51): its approved build may run elevated and be the
/// OpenXR runtime.
/// </summary>
public sealed record Profile(
    string Id,
    string Name,
    string? ModuleId,
    BuildSource Monado,
    IReadOnlyList<EnvVar> ExtraEnvironment,
    IReadOnlyDictionary<string, string> ModuleOptions,
    bool? Lighthouse = null,
    bool RunAsAdministrator = false)
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
