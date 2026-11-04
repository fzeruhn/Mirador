using System.Text.Json.Serialization;

namespace Mirador.Core.Profiles;

/// <summary>Keeps the <see cref="ProfileSet"/> in one JSON file.</summary>
public sealed class ProfileStore(string path)
{
    /// <summary>
    /// The saved profiles. A missing file gives <see cref="ProfileSet.Defaults"/>; a file that cannot be read or has
    /// no usable profiles is kept as <c>.bad</c> next to it and the defaults are returned.
    /// </summary>
    public ProfileSet Load() =>
        JsonFile.Load(path, ProfilesJsonContext.Default.ProfileSet, Usable) ?? ProfileSet.Defaults();

    public void Save(ProfileSet set) =>
        JsonFile.Save(path, set, ProfilesJsonContext.Default.ProfileSet);

    /// <summary>
    /// The set filled in where an older or hand-edited file left things out, or null when it has no usable profiles.
    /// A selected id that matches no profile selects the first one.
    /// </summary>
    private static ProfileSet? Usable(ProfileSet set)
    {
        if (set.Profiles is not { Count: > 0 })
        {
            return null;
        }

        var ids = new HashSet<string>();
        var profiles = new List<Profile>();
        foreach (var p in set.Profiles)
        {
            if (p is null || string.IsNullOrEmpty(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Monado is null || !ids.Add(p.Id))
            {
                return null;
            }

            profiles.Add(p with
            {
                ExtraEnvironment = p.ExtraEnvironment ?? [],
                ModuleOptions = p.ModuleOptions ?? new Dictionary<string, string>(),
            });
        }

        return new ProfileSet(profiles, set.SelectedId is not null && ids.Contains(set.SelectedId) ? set.SelectedId : profiles[0].Id);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ProfileSet))]
internal sealed partial class ProfilesJsonContext : JsonSerializerContext;
