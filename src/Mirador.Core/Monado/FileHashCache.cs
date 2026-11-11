using System.Collections.Concurrent;

namespace Mirador.Core.Monado;

/// <summary>
/// The SHA-256 of files, kept while a file's size and last write time stay the same: Home asks whether a profile's build
/// is the approved one at every refresh, on the UI thread, and a build's DLL and service are megabytes each.
/// </summary>
/// <param name="hashOf">The real hash of a file (lower case).</param>
public sealed class FileHashCache(Func<string, string> hashOf)
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hash of <paramref name="path"/>: the kept one while the file looks unchanged, else hashed again.</summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    public string HashOf(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        var (length, written) = (info.Length, info.LastWriteTimeUtc);
        if (known.TryGetValue(full, out var kept) && kept.Length == length && kept.Written == written)
        {
            return kept.Hash;
        }

        var hash = hashOf(full);
        known[full] = (length, written, hash);
        return hash;
    }
}
