using System.Net.Http.Headers;
using System.Text.Json;

namespace Mirador.Core.Diagnostics;

/// <summary>A full release from GitHub's <c>releases/latest</c>.</summary>
/// <param name="Tag">The release's tag, such as <c>v0.2.0</c>.</param>
/// <param name="Url">The release's page.</param>
/// <param name="WindowsZipUrl">The download of the asset <c>OpenComposite-windows.zip</c>; null when the release has none or no digest.</param>
/// <param name="WindowsZipSha256">That asset's SHA-256 from GitHub's <c>digest</c>, lower case without <c>sha256:</c>; null likewise.</param>
public sealed record Release(string Tag, string Url, string? WindowsZipUrl, string? WindowsZipSha256);

/// <summary>Asks GitHub for the latest release of Mirador and of OpenComposite, to say so on Home. It never installs anything (Mirador.md §4.4 item 13).</summary>
public static class UpdateCheck
{
    public const string MiradorRepository = "fzeruhn/mirador";

    public const string OpenCompositeRepository = "fzeruhn/OpenComposite";

    private const string WindowsZip = "OpenComposite-windows.zip";

    private const string DigestPrefix = "sha256:";

    /// <summary>The latest full release from GitHub's answer; null when it isn't one (a draft, a pre-release, an error).</summary>
    public static Release? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
                && root.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
                && !(root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                && !(root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True))
            {
                var (zip, sha) = FindZip(root);
                return new Release(tag.GetString()!, url.GetString()!, zip, sha);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>Whether release <paramref name="tag"/> (<c>v0.2.0</c>) is newer than <paramref name="current"/> (<c>0.1.0</c>); false when either isn't a version.</summary>
    public static bool IsNewer(string current, string tag) =>
        Parse(current) is { } mine && Parse(tag) is { } theirs && theirs > mine;

    /// <summary>
    /// Asks GitHub for <paramref name="repository"/>'s latest full release. <c>Answered</c> is false when it couldn't be
    /// asked (offline, refused, too slow), so that the caller tries again soon rather than in a day; a repository without
    /// a release is answered with no release.
    /// </summary>
    public static async Task<(bool Answered, Release? Latest)> LatestAsync(HttpClient http, string repository, string userAgentVersion, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mirador", Parse(userAgentVersion)?.ToString() ?? "0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (true, null); // no release yet
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            return (true, ParseLatest(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return (false, null);
        }
    }

    private static (string? Url, string? Sha) FindZip(JsonElement release)
    {
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && Text(asset, "name") == WindowsZip
                    && Text(asset, "browser_download_url") is { Length: > 0 } url
                    && Text(asset, "digest") is { } digest
                    && digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)
                    && digest.Length > DigestPrefix.Length)
                {
                    return (url, digest[DigestPrefix.Length..].ToLowerInvariant());
                }
            }
        }

        return (null, null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A version from <c>0.2.0</c> or <c>v0.2.0</c>, missing parts as 0 (<c>v1</c> is 1.0); null for anything else.</summary>
    private static Version? Parse(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
        {
            t = t[1..];
        }

        var plus = t.IndexOfAny(['+', '-']);
        if (plus >= 0)
        {
            t = t[..plus];
        }

        if (!t.Contains('.'))
        {
            t += ".0";
        }

        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }
}
