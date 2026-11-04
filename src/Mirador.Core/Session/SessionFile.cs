using System.Text.Json.Serialization;

namespace Mirador.Core.Session;

/// <summary>
/// <c>session.json</c>: written when Monado starts and deleted when the session has ended, so that a file left behind
/// means Mirador went away mid-session and the headset module's steps still need undoing.
/// </summary>
public sealed class SessionFile(string path)
{
    /// <summary>Records the session's profile, its module (null: none) and that module's recovery data.</summary>
    public void Write(string profileId, string? moduleId, IReadOnlyDictionary<string, string> data) =>
        JsonFile.Save(path, new SavedSession(profileId, moduleId, new Dictionary<string, string>(data)), SessionJsonContext.Default.SavedSession);

    /// <summary>
    /// The session left behind, or null when there is none. A file that can't be understood counts as none and is
    /// kept as <c>.bad</c> next to it; entries of the data without a value are left out.
    /// </summary>
    public (string ProfileId, string? ModuleId, IReadOnlyDictionary<string, string> Data)? Read()
    {
        var saved = JsonFile.Load(path, SessionJsonContext.Default.SavedSession, Usable);
        return saved is null ? null : (saved.ProfileId, saved.ModuleId, saved.Data!);
    }

    /// <summary>Deletes the file; nothing happens when there is none.</summary>
    public void Delete()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static SavedSession? Usable(SavedSession saved) =>
        string.IsNullOrEmpty(saved.ProfileId)
            ? null
            : saved with
            {
                Data = (saved.Data ?? []).Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value),
            };
}

internal sealed record SavedSession(string ProfileId, string? ModuleId, Dictionary<string, string>? Data);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SavedSession))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
