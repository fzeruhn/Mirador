using System.Text.Json.Serialization;

namespace Mirador.Core.Profiles;

/// <summary>
/// All profiles and which one is selected. Never changed in place: every method returns a new set.
/// New profiles go at the end; names are trimmed and must be unique (ignoring case).
/// </summary>
/// <param name="RunAsAdministratorPerProfile">
/// Whether App settings' old "Run Monado as administrator" was moved onto these profiles' own toggles (once, 2026-11-09;
/// <see cref="Migration.ProfileMigration.MoveConsentOntoProfiles"/>). False only in a file from before then; a new set
/// has nothing to move. Kept with the profiles, so a lost <c>settings.json</c> can't run the move again.
/// </param>
public sealed record ProfileSet(IReadOnlyList<Profile> Profiles, string SelectedId, bool RunAsAdministratorPerProfile = false)
{
    /// <summary>The first-run set: Dream Air (selected) and Custom, both on Mirador's Monado.</summary>
    public static ProfileSet Defaults() => new(
        [
            new Profile("dreamair", "Dream Air", "dreamair", BuildSource.Mirador, [], new Dictionary<string, string>()),
            new Profile("custom", "Custom", null, BuildSource.Mirador, [], new Dictionary<string, string>()),
        ],
        "dreamair",
        RunAsAdministratorPerProfile: true);

    [JsonIgnore]
    public Profile Selected => Find(SelectedId);

    public ProfileSet Select(string id)
    {
        Find(id);
        return this with { SelectedId = id };
    }

    /// <summary>Adds <paramref name="p"/> at the end. Its id must be new.</summary>
    public ProfileSet Add(Profile p)
    {
        if (string.IsNullOrEmpty(p.Id) || Profiles.Any(x => x.Id == p.Id))
        {
            throw new ArgumentException("A profile needs an id that no other profile has.", nameof(p));
        }

        return this with { Profiles = [.. Profiles, p with { Name = CheckName(p.Name, exceptId: null) }] };
    }

    /// <summary>Adds a copy of profile <paramref name="id"/> at the end, with a new id and the name <paramref name="newName"/>.</summary>
    public ProfileSet Duplicate(string id, string newName)
    {
        var source = Find(id);
        var copy = source with
        {
            Id = Profile.NewId(),
            Name = CheckName(newName, exceptId: null),
            ExtraEnvironment = [.. source.ExtraEnvironment],
            ModuleOptions = new Dictionary<string, string>(source.ModuleOptions),
        };
        return this with { Profiles = [.. Profiles, copy] };
    }

    public ProfileSet Rename(string id, string name) => Replace(Find(id) with { Name = name });

    /// <summary>Removes a profile. The last one cannot go; if the selected one goes, the first remaining one is selected.</summary>
    public ProfileSet Delete(string id)
    {
        Find(id);
        if (Profiles.Count == 1)
        {
            throw new MiradorException("Mirador needs at least one profile.");
        }

        var rest = Profiles.Where(p => p.Id != id).ToList();
        return this with { Profiles = rest, SelectedId = SelectedId == id ? rest[0].Id : SelectedId };
    }

    /// <summary>Swaps in <paramref name="p"/> for the profile with the same id, keeping its place in the list.</summary>
    public ProfileSet Replace(Profile p)
    {
        Find(p.Id);
        var named = p with { Name = CheckName(p.Name, exceptId: p.Id) };
        return this with { Profiles = [.. Profiles.Select(x => x.Id == p.Id ? named : x)] };
    }

    private Profile Find(string id) =>
        Profiles.FirstOrDefault(p => p.Id == id) ?? throw new MiradorException("That profile no longer exists.");

    private string CheckName(string name, string? exceptId)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new MiradorException("A profile needs a name.");
        }

        if (Profiles.Any(p => p.Id != exceptId && string.Equals(p.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MiradorException($"A profile called {trimmed} already exists.");
        }

        return trimmed;
    }
}
