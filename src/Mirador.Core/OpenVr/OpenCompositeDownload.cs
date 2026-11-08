using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mirador.Core.Diagnostics;

namespace Mirador.Core.OpenVr;

/// <summary>
/// Fetches an OpenComposite release from the owner's fork and unpacks it for <see cref="OpenCompositeCopy.Install"/>
/// (Mirador.md §4.9 item 42). Nothing is unpacked before the zip's SHA-256 (from GitHub's release) matches, no entry may
/// leave its folder, and a failure leaves nothing behind but the download folder itself.
/// </summary>
public static partial class OpenCompositeDownload
{
    /// <summary>The most a release's zip, and what it unpacks to, may be.</summary>
    public const long MaxBytes = 64 * 1024 * 1024;

    private const string Host = "github.com";

    /// <summary>
    /// Downloads <paramref name="release"/>'s Windows zip, checks it and unpacks it to <c>{downloadRoot}\&lt;version&gt;\OpenComposite</c>.
    /// </summary>
    /// <returns>The unpacked <c>OpenComposite</c> folder.</returns>
    /// <exception cref="MiradorException">The release can't be trusted or fetched; the message says why in plain words.</exception>
    public static Task<string> FetchAsync(HttpClient http, Release release, string downloadRoot, CancellationToken ct) =>
        FetchAsync(http, release, downloadRoot, ct, IdleTimeout);

    /// <summary>How long GitHub may say nothing (no answer, or no more of the body) before the download is given up.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary><see cref="FetchAsync(HttpClient, Release, string, CancellationToken)"/> with its own idle timeout (for tests).</summary>
    internal static async Task<string> FetchAsync(HttpClient http, Release release, string downloadRoot, CancellationToken ct, TimeSpan idle)
    {
        if (release.WindowsZipUrl is null || release.WindowsZipSha256 is null)
        {
            throw new MiradorException($"OpenComposite {Plain(release.Tag)} has no Windows download that Mirador can check.");
        }

        // The requested address, not wherever GitHub redirects to: GitHub serves its release files from its own file host.
        if (!Uri.TryCreate(release.WindowsZipUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new MiradorException("OpenComposite's download isn't on GitHub, so Mirador won't fetch it.");
        }

        var version = Plain(release.Tag);
        if (!SafeName().IsMatch(version))
        {
            throw new MiradorException($"OpenComposite's release name \"{release.Tag}\" isn't a version Mirador understands.");
        }

        Directory.CreateDirectory(downloadRoot);
        var zipFile = Path.Combine(downloadRoot, version + ".zip.tmp");
        var staging = Path.Combine(downloadRoot, version + ".part");
        try
        {
            await DownloadAsync(http, url, zipFile, release.WindowsZipSha256, ct, idle);

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Unpack(zipFile, staging);
            var unpacked = Path.Combine(staging, "OpenComposite");
            Check(unpacked, version);

            var final = Path.Combine(downloadRoot, version);
            if (Directory.Exists(final))
            {
                Directory.Delete(final, recursive: true);
            }

            Directory.Move(staging, final);
            return Path.Combine(final, "OpenComposite");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new MiradorException($"Mirador couldn't unpack OpenComposite {version} ({e.Message.Trim().TrimEnd('.')}).");
        }
        finally
        {
            TryDelete(zipFile);
            TryDeleteDirectory(staging);
        }
    }

    private static async Task DownloadAsync(HttpClient http, Uri url, string file, string expectedSha, CancellationToken userCt, TimeSpan idle)
    {
        // The HttpClient's own timeout stops at the headers; this one restarts at every bit of the body that arrives.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        var ct = linked.Token;
        try
        {
            linked.CancelAfter(idle);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new MiradorException($"GitHub didn't give the OpenComposite download ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new MiradorException("OpenComposite's download is larger than Mirador allows, so it was not fetched.");
            }

            using var hash = SHA256.Create();
            await using (var network = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await network.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        throw new MiradorException("OpenComposite's download is larger than Mirador allows, so it was stopped.");
                    }

                    linked.CancelAfter(idle);
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            if (!string.Equals(Convert.ToHexString(hash.Hash!), expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new MiradorException("OpenComposite's download doesn't match the SHA-256 GitHub lists for it, so it was not used.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !userCt.IsCancellationRequested))
        {
            var why = e is OperationCanceledException ? "GitHub stopped answering" : e.Message.Trim().TrimEnd('.');
            throw new MiradorException($"Mirador couldn't download OpenComposite ({why}).");
        }
    }

    private static void Unpack(string zipFile, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipFile);

        // Every path is checked, and the size added up, before anything is written.
        long size = 0;
        var targets = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new MiradorException($"OpenComposite's download has a file (\"{entry.FullName}\") that would land outside its folder, so it was not used.");
            }

            size += entry.Length;
            if (size > MaxBytes)
            {
                throw new MiradorException("OpenComposite's download unpacks to more than Mirador allows, so it was not used.");
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

    private static void Check(string unpacked, string version)
    {
        var dll = Path.Combine(unpacked, "bin", "vrclient_x64.dll");
        var sourceFile = Path.Combine(unpacked, "SOURCE.txt");
        if (!File.Exists(dll) || !File.Exists(sourceFile))
        {
            throw new MiradorException("OpenComposite's download isn't a release Mirador knows: its DLL or SOURCE.txt is missing.");
        }

        var source = OpenCompositeSource.Read(File.ReadAllText(sourceFile));
        if (source?.Version != version)
        {
            throw new MiradorException(
                $"OpenComposite's download says it is version {source?.Version ?? "unknown"}, not {version}, so it was not used.");
        }
    }

    private static string Plain(string tag) => tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string folder)
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

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]*$")]
    private static partial Regex SafeName();
}
