using System.Net.Http.Headers;
using System.Text.Json;

namespace Mirador.Core.Diagnostics;

/// <summary>Asks GitHub for Mirador's latest release, to say so on Home. It never installs anything (Mirador.md §4.4 item 13).</summary>
public static class UpdateCheck
{
    public const string Url = "https://api.github.com/repos/fzeruhn/mirador/releases/latest";

    /// <summary>The release's tag and page from GitHub's answer; null when it isn't one.</summary>
    public static (string Tag, string Url)? ParseLatest(string json)
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
                return (tag.GetString()!, url.GetString()!);
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

    /// <summary>The latest release when it is newer than <paramref name="current"/>; null otherwise, and for any failure.</summary>
    public static async Task<(string Tag, string Url)?> CheckAsync(HttpClient http, string current, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mirador", Parse(current)?.ToString() ?? "0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var latest = ParseLatest(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return latest is { } l && IsNewer(current, l.Tag) ? latest : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

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
