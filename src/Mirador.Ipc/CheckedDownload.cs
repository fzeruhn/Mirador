using System.IO.Compression;
using System.Security.Cryptography;

namespace Mirador.Ipc;

/// <summary>
/// The one way Mirador fetches and unpacks a file from the internet (the bundled Monado, OpenLamina, the build tools):
/// a size cap while it streams, the SHA-256 checked before anything uses it, a timeout that restarts with every bit of
/// the body, and every zip entry checked to stay in its folder before anything is written. A failure's message is meant
/// for the user as it is; <c>fail</c> makes the caller's own exception from it.
/// </summary>
public static class CheckedDownload
{
    /// <summary>How long the server may say nothing before a download is given up.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="file"/>, at most <paramref name="maxBytes"/>, and checks its
    /// SHA-256 when one is given; any failure deletes the file.
    /// </summary>
    /// <param name="what">What is downloaded, as a sentence names it: "the bundled Monado", "CMake".</param>
    /// <param name="hashFrom">Where <paramref name="sha256"/> comes from, as the mismatch's message says it.</param>
    public static async Task ToFileAsync(
        HttpClient http, Uri url, string file, long maxBytes, string? sha256, string what, Func<string, Exception> fail,
        CancellationToken userCt, TimeSpan? idle = null, string hashFrom = "GitHub lists for it")
    {
        var wait = idle ?? IdleTimeout;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        var ct = linked.Token;
        try
        {
            linked.CancelAfter(wait);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw fail($"{Its(what)} download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw fail($"{Its(what)} download is larger than Mirador allows, so it was not fetched.");
            }

            using var hash = SHA256.Create();
            await using (var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await network.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw fail($"{Its(what)} download is larger than Mirador allows, so it was stopped.");
                    }

                    linked.CancelAfter(wait);
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            if (sha256 is not null && !string.Equals(Convert.ToHexString(hash.Hash!), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw fail($"{Its(what)} download doesn't match the SHA-256 {hashFrom}, so it was not used.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !userCt.IsCancellationRequested))
        {
            Cleanup.TryDelete(file);
            var why = e is OperationCanceledException ? "the server stopped answering" : e.Message.Trim().TrimEnd('.');
            throw fail($"Mirador couldn't download {what} ({why}).");
        }
        catch
        {
            Cleanup.TryDelete(file);
            throw;
        }
    }

    /// <summary>
    /// Unpacks <paramref name="zipFile"/> into <paramref name="folder"/>: every entry's path is checked to stay inside it,
    /// and the sizes added up to at most <paramref name="maxBytes"/>, before anything is written.
    /// </summary>
    /// <param name="stripTopFolderHolding">
    /// A file (relative, such as <c>bin\cmake.exe</c>): when every entry is in one top folder that holds it (CMake's zips),
    /// that folder is left out.
    /// </param>
    public static void Unpack(string zipFile, string folder, long maxBytes, string what, Func<string, Exception> fail, string? stripTopFolderHolding = null)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipFile);

        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        var strip = "";
        if (stripTopFolderHolding is not null)
        {
            var first = names.Select(n => n.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
            if (first.Count == 1 && names.All(n => n.Contains('/'))
                && names.Contains(first[0] + "/" + stripTopFolderHolding.Replace('\\', '/'), StringComparer.Ordinal))
            {
                strip = first[0] + "/";
            }
        }

        long size = 0;
        var targets = new List<(ZipArchiveEntry Entry, string Path)>();
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            var entry = zip.Entries[i];
            var relative = names[i][strip.Length..];
            if (relative.Length == 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative))
            {
                throw fail($"{Its(what)} download has a file (\"{entry.FullName}\") that would land outside its folder, so it was not used.");
            }

            size += entry.Length;
            if (size > maxBytes)
            {
                throw fail($"{Its(what)} download unpacks to more than Mirador allows, so it was not used.");
            }

            targets.Add((entry, target));
        }

        Directory.CreateDirectory(root);
        foreach (var (entry, target) in targets)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>"The bundled Monado's", "CMake's": <paramref name="what"/> as a sentence starts with it.</summary>
    private static string Its(string what) => char.ToUpperInvariant(what[0]) + what[1..] + "'s";
}

/// <summary>Deletes what Mirador made for itself when it can; what a program still holds goes at a later try.</summary>
public static class Cleanup
{
    public static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
