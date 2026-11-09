using System.Net.Http.Headers;
using System.Text.Json;

namespace Mirador.Ipc;

/// <summary>One of Mirador's Monado builds on GitHub: a nightly prerelease and its zip.</summary>
/// <param name="Version">The tag, which is the version: <c>monado-&lt;yyyy.mm.dd&gt;-&lt;sha&gt;</c>.</param>
/// <param name="ZipUrl">The zip's download, on github.com under Mirador's releases.</param>
/// <param name="ZipSha256">The zip's SHA-256 from GitHub's <c>digest</c>, lower case.</param>
/// <param name="PublishedAt">When it was published, as GitHub says.</param>
public sealed record MonadoRelease(string Version, string ZipUrl, string ZipSha256, DateTimeOffset PublishedAt);

/// <summary>
/// Mirador's Monado builds as GitHub lists them (Mirador.md §4.11 item 55): the app reads the list for "Update Monado",
/// and the helper reads it again itself before installing one, never trusting a URL or hash the app hands over.
/// </summary>
public static class MonadoReleaseList
{
    /// <summary>Mirador's repository, where the nightly is published.</summary>
    public const string Repository = "fzeruhn/Mirador";

    /// <summary>The releases list, newest first.</summary>
    public const string ApiUrl = $"https://api.github.com/repos/{Repository}/releases?per_page=100";

    private const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";
    private const string DigestPrefix = "sha256:";

    /// <summary>
    /// The usable Monado builds in GitHub's releases list, newest first: prereleases tagged with a version, not drafts,
    /// with a <c>&lt;version&gt;.zip</c> under Mirador's own download address and its SHA-256 digest. Anything else is
    /// left out; text that isn't the list gives none.
    /// </summary>
    public static IReadOnlyList<MonadoRelease> Parse(string json)
    {
        var found = new List<MonadoRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || Text(release, "tag_name") is not { } version
                    || !MiradorMonado.IsVersion(version)
                    || !Flag(release, "prerelease")
                    || Flag(release, "draft")
                    || !release.TryGetProperty("assets", out var assets)
                    || assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var published = DateTimeOffset.TryParse(Text(release, "published_at"), out var at) ? at : DateTimeOffset.MinValue;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind == JsonValueKind.Object
                        && Text(asset, "name") == version + ".zip"
                        && Text(asset, "browser_download_url") is { } url
                        && url == $"{DownloadPrefix}{version}/{version}.zip"
                        && Text(asset, "digest") is { } digest
                        && digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)
                        && digest.Length == DigestPrefix.Length + 64
                        && digest[DigestPrefix.Length..].All(Uri.IsHexDigit))
                    {
                        found.Add(new MonadoRelease(version, url, digest[DigestPrefix.Length..].ToLowerInvariant(), published));
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [.. found.OrderByDescending(r => r.PublishedAt).ThenByDescending(r => r.Version, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Fetches the list. Null when GitHub didn't answer or answered with an error; an empty list when there is no build.
    /// </summary>
    public static async Task<IReadOnlyList<MonadoRelease>?> FetchAsync(HttpClient http, string userAgentVersion, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mirador", userAgentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
