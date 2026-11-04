using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mirador.Core;

/// <summary>Reading and writing the small JSON files Mirador keeps, so that no store ever loses or half-writes one.</summary>
internal static class JsonFile
{
    /// <summary>
    /// Reads <paramref name="path"/>. Returns null when the file is missing. A file that is not valid JSON of the
    /// right shape, or that <paramref name="accept"/> turns down (by returning null), is renamed to
    /// <c>&lt;path&gt;.bad</c> (replacing an older one) and null is returned too, so the caller can start from defaults.
    /// </summary>
    public static T? Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T, T?>? accept = null) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(text, typeInfo);
        }
        catch (JsonException)
        {
            value = null;
        }

        if (value is not null && accept is not null)
        {
            value = accept(value);
        }

        if (value is null)
        {
            File.Move(path, path + ".bad", overwrite: true);
        }

        return value;
    }

    /// <summary>Writes <paramref name="value"/> to <c>&lt;path&gt;.tmp</c>, then moves it over <paramref name="path"/>.</summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, typeInfo));
        File.Move(temp, path, overwrite: true);
    }
}
