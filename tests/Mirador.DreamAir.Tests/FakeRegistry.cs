namespace Mirador.DreamAir.Tests;

/// <summary>A registry in memory: keys with string values, written as <c>HKLM\…</c> like the real one is read.</summary>
public sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<string, Dictionary<string, string>> keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry Set(string hiveAndKey, string name, string value)
    {
        if (!keys.TryGetValue(hiveAndKey, out var values))
        {
            keys[hiveAndKey] = values = new(StringComparer.OrdinalIgnoreCase);
        }

        values[name] = value;
        return this;
    }

    public string? GetString(string hiveAndKey, string name) =>
        keys.TryGetValue(hiveAndKey, out var values) && values.TryGetValue(name, out var value) ? value : null;

    public IReadOnlyList<string> SubKeyNames(string hiveAndKey) =>
        keys.Keys
            .Where(key => key.StartsWith(hiveAndKey + @"\", StringComparison.OrdinalIgnoreCase))
            .Select(key => key[(hiveAndKey.Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
