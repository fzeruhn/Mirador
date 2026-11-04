using System.Text.Json.Serialization;

namespace Mirador.Core.Profiles;

/// <summary>Keeps the <see cref="ProfileSet"/> in one JSON file.</summary>
public sealed class ProfileStore(string path)
{
    /// <summary>
    /// The saved profiles. A missing file gives <see cref="ProfileSet.Defaults"/>; a file that cannot be read or has
    /// no usable profiles is kept as <c>.bad</c> next to it and the defaults are returned. A file with some unusable
    /// entries (a hand edit gone wrong) loads without them, and a copy of it is kept as <c>.bad</c>, so that one broken
    /// profile doesn't cost the others.
    /// </summary>
    public ProfileSet Load()
    {
        var leftOut = false;
        var set = JsonFile.Load(path, ProfilesJsonContext.Default.ProfileSet, s => Usable(s, out leftOut));
        if (set is null)
        {
            return ProfileSet.Defaults();
        }

        if (leftOut)
        {
            JsonFile.KeepCopyAsBad(path);
        }

        return set;
    }

    public void Save(ProfileSet set) =>
        JsonFile.Save(path, set, ProfilesJsonContext.Default.ProfileSet);

    /// <summary>
    /// The set filled in where an older or hand-edited file left things out, or null when it has no usable profiles.
    /// Unusable entries are left out (<paramref name="leftOut"/> says so): a profile without an id, a name or a Monado
    /// build, a second profile with an id already used, a variable without a name, an option without a value. A
    /// variable without a value gets an empty one. A selected id that matches no profile selects the first one.
    /// </summary>
    private static ProfileSet? Usable(ProfileSet set, out bool leftOut)
    {
        leftOut = false;
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
                leftOut = true;
                continue;
            }

            var variables = (p.ExtraEnvironment ?? []).Where(v => v?.Name is not null).Select(v => v with { Value = v.Value ?? "" }).ToList();
            var options = (p.ModuleOptions ?? new Dictionary<string, string>()).Where(o => o.Value is not null).ToDictionary(o => o.Key, o => o.Value);
            leftOut |= variables.Count != (p.ExtraEnvironment?.Count ?? 0) || options.Count != (p.ModuleOptions?.Count ?? 0);
            profiles.Add(p with { ExtraEnvironment = variables, ModuleOptions = options });
        }

        if (profiles.Count == 0)
        {
            return null;
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
