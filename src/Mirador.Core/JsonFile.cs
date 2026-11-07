using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mirador.Core;

/// <summary>Reading and writing the small JSON files Mirador keeps, so that no store ever loses or half-writes one.</summary>
public static class JsonFile
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

        // Shared with writers and deleters, so that a save can move a new file over this one while it is being read.
        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

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

    /// <summary>
    /// Copies <paramref name="path"/> to <c>&lt;path&gt;.bad</c> (replacing an older one) and keeps the file: for a file
    /// that loaded only in part, so that what was left out isn't lost when the file is next saved.
    /// </summary>
    public static void KeepCopyAsBad(string path) => File.Copy(path, path + ".bad", overwrite: true);

    private const int MoveAttempts = 10;
    private const int MoveRetryMilliseconds = 20;

    /// <summary>
    /// Writes <paramref name="value"/> to <c>&lt;path&gt;.tmp</c> and onto the disk, then moves it over
    /// <paramref name="path"/>: a crash or power cut leaves the old file or the new one, never one half written or empty,
    /// and a reader never finds the file missing. If a reader holds the old file the move is tried up to 10 times, 20 ms
    /// apart; after that the exception is thrown.
    /// </summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Write(stream, value, typeInfo);
            stream.Flush(flushToDisk: true);
        }

        // The move is atomic, but Windows refuses it for a moment while a reader has the old file open, so it is
        // tried again a few times before the failure is passed on.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryMilliseconds);
            }
        }
    }

    /// <summary><paramref name="value"/> as the JSON <see cref="Save"/> writes.</summary>
    public static string Text<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var stream = new MemoryStream();
        Write(stream, value, typeInfo);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes the JSON for people to read too: "Finn's builds" and "Zoë" stay as they are instead of becoming
    /// escaped character codes (only quotes, backslashes and control characters are escaped). The files are Mirador's own, never
    /// put into a web page.
    /// </summary>
    private static void Write<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = typeInfo.Options.WriteIndented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        JsonSerializer.Serialize(writer, value, typeInfo);
    }
}
