using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mirador.Core.Monado;

/// <summary>Which of Mirador's Monado builds is in use, and the one kept for "Roll back".</summary>
/// <param name="Current">The version profiles on Mirador's Monado run, such as <c>monado-2026.11.10-1a2b3c4d5e</c>.</param>
/// <param name="Previous">The version before it, kept for "Roll back"; null when there is none.</param>
public sealed record MiradorMonadoVersions(string? Current, string? Previous);

/// <summary>
/// Mirador's Monado (Mirador.md §4.11 item 55): the nightly builds of upstream <c>main</c> the helper installs in
/// <c>{app}\monado\&lt;version&gt;\</c>, an admin-only folder, with <c>current.json</c> beside them saying which is in use.
/// Only the helper writes there; the app reads.
/// </summary>
public static partial class MiradorMonado
{
    /// <summary>The file in the root that names the current and previous versions.</summary>
    public const string CurrentFile = "current.json";

    /// <summary>The releases' tag prefix: Mirador's nightly prereleases are <c>monado-&lt;yyyy.mm.dd&gt;-&lt;sha&gt;</c>.</summary>
    public const string TagPrefix = "monado-";

    /// <summary>Whether <paramref name="version"/> is a nightly's name, and so a safe folder name.</summary>
    public static bool IsVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    /// <summary>The versions in <paramref name="root"/>, or none when nothing is installed or the file is unusable.</summary>
    public static MiradorMonadoVersions Read(string root)
    {
        var path = Path.Combine(root, CurrentFile);
        try
        {
            if (!File.Exists(path))
            {
                return new MiradorMonadoVersions(null, null);
            }

            var versions = JsonSerializer.Deserialize(File.ReadAllText(path), MiradorMonadoJsonContext.Default.MiradorMonadoVersions);
            return new MiradorMonadoVersions(
                IsVersion(versions?.Current) ? versions!.Current : null,
                IsVersion(versions?.Previous) ? versions!.Previous : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MiradorMonadoVersions(null, null);
        }
    }

    /// <summary>Writes the versions (the helper only).</summary>
    public static void Write(string root, MiradorMonadoVersions versions) =>
        JsonFile.Save(Path.Combine(root, CurrentFile), versions, MiradorMonadoJsonContext.Default.MiradorMonadoVersions);

    /// <summary>The install folder of <paramref name="version"/>.</summary>
    /// <exception cref="ArgumentException">It isn't a version's name.</exception>
    public static string FolderOf(string root, string version) =>
        IsVersion(version) ? Path.Combine(root, version) : throw new ArgumentException($"Not a version of Mirador's Monado: {version}", nameof(version));

    [GeneratedRegex(@"^monado-\d{4}\.\d{2}\.\d{2}-[0-9a-f]{7,40}$")]
    private static partial Regex VersionPattern();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(MiradorMonadoVersions))]
internal sealed partial class MiradorMonadoJsonContext : JsonSerializerContext;
