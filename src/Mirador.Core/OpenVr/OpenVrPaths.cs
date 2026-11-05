using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mirador.Core.OpenVr;

/// <summary>Where OpenVR games go: the first folder in the paths file's <c>runtime</c> list.</summary>
public enum OpenVrTarget
{
    /// <summary>No file, an empty list, or a file Mirador can't read.</summary>
    None,

    /// <summary>Mirador's OpenComposite copy, so OpenXR and Monado.</summary>
    Monado,

    /// <summary>SteamVR's own folder.</summary>
    SteamVr,

    /// <summary>Any other folder.</summary>
    Other,
}

/// <summary>What the paths file says now.</summary>
/// <param name="Target">Where OpenVR games go.</param>
/// <param name="First">The first <c>runtime</c> entry as the file has it; null when there is none.</param>
/// <param name="Problem">Why the file couldn't be read, in plain words; then <paramref name="Target"/> is None.</param>
public sealed record OpenVrState(OpenVrTarget Target, string? First, string? Problem);

/// <summary>
/// The user's <c>openvrpaths.vrpath</c>, which Valve's <c>openvr_api.dll</c> in every OpenVR game reads to find the
/// runtime: the first folder of its <c>runtime</c> list, whose <c>bin\vrclient_x64.dll</c> it loads (Mirador.md §4.6).
/// Switching keeps every other key and entry, as OpenComposite's own switcher does, and never writes over a file it
/// couldn't read.
/// </summary>
/// <param name="file">The paths file.</param>
/// <param name="ourRuntimeDir">Mirador's OpenComposite copy: the runtime folder, with <c>bin\</c> below it.</param>
public sealed class OpenVrPaths(string file, string ourRuntimeDir)
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary><c>%LOCALAPPDATA%\openvr\openvrpaths.vrpath</c>.</summary>
    public static string DefaultFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");

    /// <summary>Reads where OpenVR games go now. A missing file is None, with no problem.</summary>
    public OpenVrState Read()
    {
        var (doc, problem) = Load();
        if (problem is not null)
        {
            return new OpenVrState(OpenVrTarget.None, null, problem);
        }

        var first = doc is null ? null : RuntimeList(doc).FirstOrDefault();
        return first is null ? new OpenVrState(OpenVrTarget.None, null, null) : new OpenVrState(Classify(first), first, null);
    }

    /// <summary>Whether Mirador's copy is anywhere in the <c>runtime</c> list; false when the file can't be read.</summary>
    public bool NamesOurs()
    {
        var (doc, problem) = Load();
        return problem is null && doc is not null && RuntimeList(doc).Any(entry => SamePath(entry, ourRuntimeDir));
    }

    /// <summary>Puts Mirador's copy first; every other entry follows in its order.</summary>
    /// <returns>Null when written, else why nothing was written.</returns>
    public string? UseMonado()
    {
        var (doc, problem) = Load();
        if (problem is not null)
        {
            return problem;
        }

        doc ??= new JsonObject { ["jsonid"] = "vrpathreg", ["runtime"] = new JsonArray(), ["version"] = 1 };
        var others = RuntimeList(doc).Where(entry => !SamePath(entry, ourRuntimeDir));
        return Save(doc, [Path.TrimEndingDirectorySeparator(Full(ourRuntimeDir)), .. others]);
    }

    /// <summary>Removes Mirador's copy and puts SteamVR's folder first, when the list has one.</summary>
    /// <returns>Null when written (or with no file, nothing to do), else why nothing was written.</returns>
    public string? UseSteamVr()
    {
        var (doc, problem) = Load();
        if (problem is not null || doc is null)
        {
            return problem;
        }

        var rest = RuntimeList(doc).Where(entry => !SamePath(entry, ourRuntimeDir)).ToList();
        var steamVr = rest.FindIndex(entry => Classify(entry) == OpenVrTarget.SteamVr);
        if (steamVr > 0)
        {
            var entry = rest[steamVr];
            rest.RemoveAt(steamVr);
            rest.Insert(0, entry);
        }

        return Save(doc, rest);
    }

    /// <summary>
    /// Whether two folders are the same: full paths, without regard to case, <c>/</c> and <c>\</c> alike, trailing
    /// separators ignored.
    /// </summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Full(a)),
            Path.TrimEndingDirectorySeparator(Full(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string Full(string path)
    {
        var trimmed = path.Trim().Replace('/', '\\');
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }

    private OpenVrTarget Classify(string entry)
    {
        if (SamePath(entry, ourRuntimeDir))
        {
            return OpenVrTarget.Monado;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Full(entry)));
        return string.Equals(name, "SteamVR", StringComparison.OrdinalIgnoreCase) ? OpenVrTarget.SteamVr : OpenVrTarget.Other;
    }

    private static List<string> RuntimeList(JsonObject doc) =>
        doc["runtime"] is JsonArray list ? list.Select(n => n!.GetValue<string>()).ToList() : [];

    /// <summary>The file as JSON (null when missing), or why it can't be used: never both.</summary>
    private (JsonObject? Doc, string? Problem) Load()
    {
        string text;
        try
        {
            if (!File.Exists(file))
            {
                return (null, null);
            }

            text = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, Problem(e.Message.Trim().TrimEnd('.')));
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return (null, Problem("it isn't valid JSON"));
        }

        if (node is not JsonObject doc)
        {
            return (null, Problem("it isn't a JSON object"));
        }

        if (doc["runtime"] is { } runtime
            && (runtime is not JsonArray list || list.Any(entry => entry?.GetValueKind() != JsonValueKind.String)))
        {
            return (null, Problem("its \"runtime\" isn't a list of folders"));
        }

        return (doc, null);
    }

    private string Problem(string why) =>
        $"Mirador couldn't read the OpenVR paths file {file} ({why}), so it left it as it is. Fix or delete that file, then try again.";

    private string? Save(JsonObject doc, IEnumerable<string> runtimes)
    {
        doc["runtime"] = new JsonArray(runtimes.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        var temp = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    doc.WriteTo(writer);
                }

                stream.Write("\n"u8);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The temp file stays; the next write replaces it.
            }

            return $"Mirador couldn't write the OpenVR paths file {file} ({e.Message.Trim().TrimEnd('.')}); it is unchanged.";
        }
    }
}
