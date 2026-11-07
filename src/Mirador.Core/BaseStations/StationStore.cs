using System.Text.Json.Serialization;

namespace Mirador.Core.BaseStations;

/// <summary>A base station Mirador has seen, in <c>basestations.json</c>.</summary>
/// <param name="Address">The Bluetooth address, as <see cref="StationProtocol.FormatAddress"/> writes it.</param>
/// <param name="Name">The Bluetooth name the station advertises ("LHB-..." or "HTC BS ...").</param>
/// <param name="Selected">The user chose to have Mirador switch this station.</param>
/// <param name="CustomName">A name the user gave it (at most <see cref="MaxNameLength"/> characters), or null.</param>
/// <param name="Id">A first-generation station's ID (8 hex digits) as the user typed it, or null.</param>
public sealed record KnownStation(string Address, string Name, bool Selected = false, string? CustomName = null, string? Id = null)
{
    /// <summary>The longest custom name kept.</summary>
    public const int MaxNameLength = 40;

    /// <summary>The generation of the station, from its <see cref="Name"/>.</summary>
    /// <exception cref="InvalidOperationException">The name isn't a base station's; the store never keeps such a station.</exception>
    [JsonIgnore]
    public StationVersion Version =>
        StationProtocol.VersionOf(Name) ?? throw new InvalidOperationException($"\"{Name}\" is not the name of a base station.");

    /// <summary>What to show for the station: the custom name, else the Bluetooth name.</summary>
    [JsonIgnore]
    public string DisplayName => CustomName ?? Name;

    // The generated printing would show Version, which throws for a name that isn't a base station's.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Address = {Address}, Name = {Name}, Selected = {Selected}, CustomName = {CustomName}, Id = {Id}");
        return true;
    }
}

/// <summary>The shape of <c>basestations.json</c>.</summary>
public sealed record StationList(IReadOnlyList<KnownStation> Stations);

/// <summary>Keeps the known base stations in one JSON file.</summary>
public sealed class StationStore(string path)
{
    private readonly object _lock = new();

    /// <summary>
    /// The saved stations. A missing file gives none; one that can't be read is set aside as <c>.bad</c> and gives none.
    /// Entries that can't be used are left out (an address that doesn't parse, an empty name, a name that isn't a base
    /// station's, an address already seen) and a bad ID or custom name is put right (an ID that isn't 8 hex digits is
    /// dropped, a custom name is trimmed and cut to <see cref="KnownStation.MaxNameLength"/>); then a copy of the file is
    /// kept as <c>.bad</c>. Addresses come back in the standard form.
    /// </summary>
    public IReadOnlyList<KnownStation> Load()
    {
        lock (_lock)
        {
            var changed = false;
            var list = JsonFile.Load(path, StationsJsonContext.Default.StationList, l => Usable(l, out changed));
            if (list is null)
            {
                return [];
            }

            if (changed)
            {
                JsonFile.KeepCopyAsBad(path);
            }

            return list.Stations;
        }
    }

    /// <summary>Writes the stations, replacing the file. Saves, and loads, from several threads go one after the other.</summary>
    public void Save(IReadOnlyList<KnownStation> stations)
    {
        lock (_lock)
        {
            JsonFile.Save(path, new StationList(stations), StationsJsonContext.Default.StationList);
        }
    }

    private static StationList? Usable(StationList list, out bool changed)
    {
        changed = false;
        if (list.Stations is null)
        {
            return null;
        }

        var seen = new HashSet<ulong>();
        var stations = new List<KnownStation>();
        foreach (var s in list.Stations)
        {
            if (s is null
                || !StationProtocol.TryParseAddress(s.Address, out var address)
                || string.IsNullOrWhiteSpace(s.Name)
                || StationProtocol.VersionOf(s.Name) is null
                || !seen.Add(address))
            {
                changed = true;
                continue;
            }

            var id = StationProtocol.TryParseV1Id(s.Id, out _) ? s.Id : null;
            var customName = CleanName(s.CustomName);
            changed |= id != s.Id || customName != s.CustomName;
            stations.Add(s with { Address = StationProtocol.FormatAddress(address), Id = id, CustomName = customName });
        }

        return new StationList(stations);
    }

    private static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length <= KnownStation.MaxNameLength)
        {
            return trimmed;
        }

        // Don't cut a surrogate pair in half.
        var length = char.IsHighSurrogate(trimmed[KnownStation.MaxNameLength - 1]) ? KnownStation.MaxNameLength - 1 : KnownStation.MaxNameLength;
        return trimmed[..length];
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(StationList))]
internal sealed partial class StationsJsonContext : JsonSerializerContext;
