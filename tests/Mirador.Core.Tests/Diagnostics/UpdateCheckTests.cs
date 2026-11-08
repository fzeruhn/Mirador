using System.Net;
using Mirador.Core.Diagnostics;

namespace Mirador.Core.Tests.Diagnostics;

public class UpdateCheckTests
{
    // Trimmed from a real answer of api.github.com/repos/{owner}/{repo}/releases/latest.
    private const string Latest = """
        {
          "url": "https://api.github.com/repos/fzeruhn/mirador/releases/123",
          "html_url": "https://github.com/fzeruhn/mirador/releases/tag/v0.2.0",
          "id": 123,
          "tag_name": "v0.2.0",
          "name": "Mirador 0.2.0",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-12-01T10:00:00Z",
          "assets": []
        }
        """;

    [Fact]
    public void Parses_the_tag_and_page()
    {
        Assert.Equal(new Release("v0.2.0", "https://github.com/fzeruhn/mirador/releases/tag/v0.2.0", null, null), UpdateCheck.ParseLatest(Latest));
    }

    private const string WithAsset = """
        {
          "html_url": "https://github.com/fzeruhn/OpenComposite/releases/tag/v2026.11.20",
          "tag_name": "v2026.11.20",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "SOURCE.txt",
              "browser_download_url": "https://github.com/fzeruhn/OpenComposite/releases/download/v2026.11.20/SOURCE.txt",
              "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
            },
            {
              "name": "OpenComposite-windows.zip",
              "browser_download_url": "https://github.com/fzeruhn/OpenComposite/releases/download/v2026.11.20/OpenComposite-windows.zip",
              "digest": "sha256:ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789"
            }
          ]
        }
        """;

    [Fact]
    public void Parses_the_windows_zip_and_its_digest()
    {
        var release = UpdateCheck.ParseLatest(WithAsset);

        Assert.Equal(
            new Release(
                "v2026.11.20",
                "https://github.com/fzeruhn/OpenComposite/releases/tag/v2026.11.20",
                "https://github.com/fzeruhn/OpenComposite/releases/download/v2026.11.20/OpenComposite-windows.zip",
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"),
            release);
    }

    [Fact]
    public void No_digest_no_sha()
    {
        var json = WithAsset.Replace("\"digest\": \"sha256:ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789\"", "\"digest\": null");

        // Without a digest the zip can't be checked, so neither part is offered.
        Assert.Equal(new Release("v2026.11.20", "https://github.com/fzeruhn/OpenComposite/releases/tag/v2026.11.20", null, null), UpdateCheck.ParseLatest(json));
    }

    [Fact]
    public void Prerelease_is_not_offered()
    {
        Assert.Null(UpdateCheck.ParseLatest(WithAsset.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateCheck.ParseLatest(WithAsset.Replace("\"draft\": false", "\"draft\": true")));
    }

    [Fact]
    public void Date_versions_are_newer()
    {
        Assert.True(UpdateCheck.IsNewer("2026.11.06", "v2026.11.20"));
        Assert.False(UpdateCheck.IsNewer("2026.11.20", "v2026.11.20"));
        Assert.False(UpdateCheck.IsNewer("2027.1.2", "v2026.11.20"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "message": "Not Found" }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""{ "tag_name": "v9.0.0", "html_url": "https://x", "prerelease": true }""")]
    public void Anything_else_is_null(string json)
    {
        Assert.Null(UpdateCheck.ParseLatest(json));
    }

    [Theory]
    [InlineData("0.1.0", "v0.2.0", true)]
    [InlineData("0.2.0", "v0.2.0", false)]
    [InlineData("0.3.0", "v0.2.0", false)]
    [InlineData("0.1.0", "garbage", false)]
    [InlineData("0.1.0.0", "v0.1.1", true)]
    [InlineData("1.0.0", "v1", false)]
    [InlineData("0.9.9", "V1.0.0-beta", true)]
    public void Newer(string current, string tag, bool newer)
    {
        Assert.Equal(newer, UpdateCheck.IsNewer(current, tag));
    }

    [Fact]
    public async Task Latest_returns_the_release_and_asks_as_mirador()
    {
        var handler = new Answer(HttpStatusCode.OK, Latest);

        var (answered, latest) = await UpdateCheck.LatestAsync(new HttpClient(handler), UpdateCheck.MiradorRepository, "0.1.0", CancellationToken.None);

        Assert.True(answered);
        Assert.Equal("v0.2.0", latest!.Tag);
        Assert.Equal("https://api.github.com/repos/fzeruhn/mirador/releases/latest", handler.Asked!.RequestUri!.ToString());
        Assert.Equal("Mirador/0.1.0", handler.Asked.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Latest_not_found_is_answered_with_nothing()
    {
        var handler = new Answer(HttpStatusCode.NotFound, "{}");

        Assert.Equal((true, null), await UpdateCheck.LatestAsync(new HttpClient(handler), UpdateCheck.OpenCompositeRepository, "0.1.0", CancellationToken.None));
        Assert.Equal("https://api.github.com/repos/fzeruhn/OpenComposite/releases/latest", handler.Asked!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Server_error_is_not_answered()
    {
        Assert.Equal((false, null), await UpdateCheck.LatestAsync(new HttpClient(new Answer(HttpStatusCode.InternalServerError, "{}")), UpdateCheck.MiradorRepository, "0.1.0", CancellationToken.None));
        Assert.Equal((false, null), await UpdateCheck.LatestAsync(new HttpClient(new Answer(HttpStatusCode.Forbidden, "{}")), UpdateCheck.MiradorRepository, "0.1.0", CancellationToken.None));
        Assert.Equal((false, null), await UpdateCheck.LatestAsync(new HttpClient(new Answer(null, "")), UpdateCheck.MiradorRepository, "0.1.0", CancellationToken.None));
    }

    private sealed class Answer(HttpStatusCode? status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request;
            return status is { } s
                ? Task.FromResult(new HttpResponseMessage(s) { Content = new StringContent(body) })
                : throw new HttpRequestException("No network.");
        }
    }
}
