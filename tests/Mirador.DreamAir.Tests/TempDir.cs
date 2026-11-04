namespace Mirador.DreamAir.Tests;

/// <summary>A fresh empty folder under the system temp folder, deleted again on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mirador-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    /// <summary>Creates an empty file at <paramref name="relative"/> (folders as needed) and returns its full path.</summary>
    public string Touch(params string[] relative)
    {
        var path = System.IO.Path.Combine([Path, .. relative]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
