using System.Text.RegularExpressions;
using Mirador.Core.Diagnostics;

using Mirador.Ipc;

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
    public static readonly TimeSpan IdleTimeout = CheckedDownload.IdleTimeout;

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
            await CheckedDownload.ToFileAsync(http, url, zipFile, MaxBytes, release.WindowsZipSha256, "OpenComposite", m => new MiradorException(m), ct, idle);

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            CheckedDownload.Unpack(zipFile, staging, MaxBytes, "OpenComposite", m => new MiradorException(m));
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
            Cleanup.TryDelete(zipFile);
            Cleanup.TryDeleteDirectory(staging);
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

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]*$")]
    private static partial Regex SafeName();
}
