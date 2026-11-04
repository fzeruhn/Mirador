using System.Text.Json;

namespace Mirador.Core.Monado;

/// <summary>Reading an OpenXR runtime manifest (the small JSON file that names the runtime DLL).</summary>
public static class RuntimeManifest
{
    /// <summary>
    /// The full path of the runtime DLL named by <c>runtime.library_path</c>; a relative path is taken from the folder
    /// of the manifest. Does not check that the DLL exists (see <see cref="Validate"/>).
    /// </summary>
    /// <exception cref="MiradorException">The manifest is missing, can't be read, is not JSON, or does not name a usable library path.</exception>
    public static string ReadLibraryPath(string manifestPath)
    {
        var (library, problem) = Read(manifestPath);
        return library ?? throw new MiradorException(problem!);
    }

    /// <summary>Null when the manifest is readable, names a library and that library exists; else the problem in plain words.</summary>
    public static string? Validate(string manifestPath)
    {
        var (library, problem) = Read(manifestPath);
        if (library is null)
        {
            return problem;
        }

        return File.Exists(library) ? null : $"The file {manifestPath} points to {library}, which doesn't exist.";
    }

    private static (string? Library, string? Problem) Read(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return (null, $"The file {manifestPath} doesn't exist.");
        }

        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"The file {manifestPath} can't be read ({e.Message.TrimEnd('.')}). Close any program that has it open, or check that you may read it.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return (null, $"The file {manifestPath} isn't valid JSON.");
        }

        using (document)
        {
            var libraryPath = LibraryPathOf(document.RootElement);
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                return (null, $"The file {manifestPath} doesn't say where the runtime DLL is: it has no runtime.library_path.");
            }

            try
            {
                var manifestFolder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
                return (Path.GetFullPath(libraryPath, manifestFolder), null);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (null, $"The file {manifestPath} has a runtime.library_path that isn't a valid path.");
            }
        }
    }

    private static string? LibraryPathOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("runtime", out var runtime)
        && runtime.ValueKind == JsonValueKind.Object
        && runtime.TryGetProperty("library_path", out var library)
        && library.ValueKind == JsonValueKind.String
            ? library.GetString()
            : null;
}
