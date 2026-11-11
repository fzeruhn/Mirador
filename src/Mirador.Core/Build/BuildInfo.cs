using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mirador.Core.Build;

/// <summary>
/// What a Monado build came from, as <c>tools\build-monado.ps1</c> writes it into the install folder
/// (<c>build-info.json</c>): shown on the profile and in the diagnostics zip (Mirador.md §4.11 item 49).
/// </summary>
public sealed record BuildInfo(
    string? SourceUrl,
    string? Commit,
    string? Describe,
    string? Compiler,
    string? MsvcVersion,
    string? VcpkgCommit,
    IReadOnlyList<string>? Options,
    string? BuiltAt)
{
    /// <summary>The file name, in the install folder.</summary>
    public const string FileName = "build-info.json";

    /// <summary>The build info in <paramref name="installDir"/>, or null when there is none or it can't be read.</summary>
    public static BuildInfo? Load(string installDir)
    {
        var path = Path.Combine(installDir, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), BuildInfoJsonContext.Default.BuildInfo) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The commit shortened to 10 characters, as on the profile.</summary>
    [JsonIgnore]
    public string ShortCommit => Commit is { Length: > 10 } c ? c[..10] : Commit ?? "unknown";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BuildInfo))]
internal sealed partial class BuildInfoJsonContext : JsonSerializerContext;
