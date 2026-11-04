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
        Assert.Equal(("v0.2.0", "https://github.com/fzeruhn/mirador/releases/tag/v0.2.0"), UpdateCheck.ParseLatest(Latest));
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
    public async Task Check_returns_a_newer_release_and_asks_as_mirador()
    {
        var handler = new Answer(HttpStatusCode.OK, Latest);

        var (answered, newer) = await UpdateCheck.CheckAsync(new HttpClient(handler), "0.1.0", CancellationToken.None);

        Assert.True(answered);
        Assert.Equal(("v0.2.0", "https://github.com/fzeruhn/mirador/releases/tag/v0.2.0"), newer);
        Assert.Equal(UpdateCheck.Url, handler.Asked!.RequestUri!.ToString());
        Assert.Equal("Mirador/0.1.0", handler.Asked.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Nothing_newer_and_no_release_yet_are_answers()
    {
        Assert.Equal((true, null), await UpdateCheck.CheckAsync(new HttpClient(new Answer(HttpStatusCode.OK, Latest)), "0.2.0", CancellationToken.None));
        Assert.Equal((true, null), await UpdateCheck.CheckAsync(new HttpClient(new Answer(HttpStatusCode.NotFound, "{}")), "0.1.0", CancellationToken.None));
    }

    [Fact]
    public async Task Failures_arent_answers()
    {
        Assert.Equal((false, null), await UpdateCheck.CheckAsync(new HttpClient(new Answer(HttpStatusCode.Forbidden, "{}")), "0.1.0", CancellationToken.None));
        Assert.Equal((false, null), await UpdateCheck.CheckAsync(new HttpClient(new Answer(null, "")), "0.1.0", CancellationToken.None));
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
