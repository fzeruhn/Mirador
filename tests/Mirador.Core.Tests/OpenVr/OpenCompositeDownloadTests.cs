using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mirador.Core.Diagnostics;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class OpenCompositeDownloadTests
{
    private const string Url = "https://github.com/fzeruhn/OpenComposite/releases/download/v2026.11.06/OpenComposite-windows.zip";

    private const string Source = "OpenComposite 2026.11.06, commit 75342495640501953fc2c17f73eeb4d124e8528e\n";

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return memory.ToArray();
    }

    private static byte[] GoodZip(string source = Source) => Zip(
        ("OpenComposite/bin/vrclient_x64.dll", "dll"),
        ("OpenComposite/bin/version.txt", "v"),
        ("OpenComposite/SOURCE.txt", source),
        ("OpenComposite/LICENSE.txt", "licence"));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Release Rel(byte[] zip, string? url = Url, string tag = "v2026.11.06", string? sha = null) =>
        new(tag, "https://github.com/fzeruhn/OpenComposite/releases/tag/" + tag, url, sha ?? Sha(zip));

    private sealed class Answer(byte[] body, bool drop = false, long? declared = null) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            Stream stream = drop ? new Dropping(body) : new MemoryStream(body);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            if (declared is { } length)
            {
                response.Content.Headers.ContentLength = length;
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>Gives half of its bytes, then the connection drops.</summary>
    private sealed class Dropping(byte[] body) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= body.Length / 2)
            {
                throw new IOException("The connection was closed.");
            }

            var n = Math.Min(count, body.Length / 2 - position);
            Array.Copy(body, position, buffer, offset, n);
            position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Gives half of its bytes, then says nothing more (until cancelled).</summary>
    private sealed class Stalling(byte[] body) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position >= body.Length / 2)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var n = Math.Min(buffer.Length, body.Length / 2 - position);
            body.AsMemory(position, n).CopyTo(buffer);
            position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Stall(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Stalling(body)) });
    }

    private static async Task<MiradorException> Refused(Release release, Answer answer, string root)
    {
        using var http = new HttpClient(answer);
        return await Assert.ThrowsAsync<MiradorException>(() => OpenCompositeDownload.FetchAsync(http, release, root, CancellationToken.None));
    }

    private static string[] Left(string root) =>
        Directory.Exists(root) ? [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)] : [];

    [Fact]
    public async Task Unpacks_a_release()
    {
        using var t = new TempDir();
        var root = t.File("download");
        var zip = GoodZip();
        var answer = new Answer(zip);
        using var http = new HttpClient(answer);

        var folder = await OpenCompositeDownload.FetchAsync(http, Rel(zip), root, CancellationToken.None);

        Assert.Equal(Path.Combine(root, "2026.11.06", "OpenComposite"), folder);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(folder, "bin", "vrclient_x64.dll")));
        Assert.Equal(Source, File.ReadAllText(Path.Combine(folder, "SOURCE.txt")));
        Assert.Equal("licence", File.ReadAllText(Path.Combine(folder, "LICENSE.txt")));
        Assert.Equal(new Uri(Url), Assert.Single(answer.Requested));
        Assert.Equal([Path.Combine(root, "2026.11.06")], Directory.GetDirectories(root));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task Refuses_a_wrong_sha()
    {
        using var t = new TempDir();
        var zip = GoodZip();

        var e = await Refused(Rel(zip, sha: new string('0', 64)), new Answer(zip), t.File("download"));

        Assert.Contains("SHA-256", e.Message);
        Assert.Empty(Left(t.File("download")));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("C:/x")]
    [InlineData("OpenComposite/../../x")]
    public async Task Refuses_an_entry_outside_its_folder(string name)
    {
        using var t = new TempDir();
        var zip = Zip(
            ("OpenComposite/bin/vrclient_x64.dll", "dll"),
            ("OpenComposite/SOURCE.txt", Source),
            (name, "evil"));

        await Refused(Rel(zip), new Answer(zip), t.File("download"));

        Assert.Empty(Left(t.File("download")));
        Assert.False(File.Exists(t.File("x")));
        Assert.False(File.Exists(@"C:\x"));
    }

    [Fact]
    public async Task Refuses_a_sibling_with_the_same_prefix()
    {
        using var t = new TempDir();
        var root = t.File("download");

        // The staging folder is "<root>\2026.11.06.part"; "..\2026.11.06.part-evil\x" starts with the same text.
        var zip = Zip(("OpenComposite/bin/vrclient_x64.dll", "dll"), ("OpenComposite/SOURCE.txt", Source), ("../2026.11.06.part-evil/x", "evil"));

        await Refused(Rel(zip), new Answer(zip), root);

        Assert.Empty(Left(root));
    }

    [Fact]
    public async Task Refuses_a_zip_without_the_dll()
    {
        using var t = new TempDir();
        var noDll = Zip(("OpenComposite/SOURCE.txt", Source));
        var noSource = Zip(("OpenComposite/bin/vrclient_x64.dll", "dll"));

        await Refused(Rel(noDll), new Answer(noDll), t.File("download"));
        await Refused(Rel(noSource), new Answer(noSource), t.File("download"));

        Assert.Empty(Left(t.File("download")));
    }

    [Theory]
    [InlineData("http://github.com/fzeruhn/OpenComposite/releases/download/v2026.11.06/a.zip")]
    [InlineData("https://example.com/a.zip")]
    [InlineData("https://github.com.evil.example/a.zip")]
    [InlineData("https://evil.example/github.com/a.zip")]
    [InlineData("not a url")]
    [InlineData(null)]
    public async Task Refuses_a_url_off_github(string? url)
    {
        using var t = new TempDir();
        var zip = GoodZip();
        var answer = new Answer(zip);

        await Refused(Rel(zip, url), answer, t.File("download"));

        Assert.Empty(answer.Requested);
        Assert.Empty(Left(t.File("download")));
    }

    [Fact]
    public async Task Refuses_a_release_without_a_sha()
    {
        using var t = new TempDir();
        var answer = new Answer(GoodZip());

        await Refused(new Release("v2026.11.06", "https://github.com/x", Url, null), answer, t.File("download"));

        Assert.Empty(answer.Requested);
    }

    [Fact]
    public async Task Refuses_a_version_other_than_the_tag()
    {
        using var t = new TempDir();
        var zip = GoodZip();

        var e = await Refused(Rel(zip, tag: "v2026.12.01"), new Answer(zip), t.File("download"));

        Assert.Contains("2026.11.06", e.Message);
        Assert.Empty(Left(t.File("download")));
    }

    [Fact]
    public async Task Refuses_a_tag_that_is_not_a_folder_name()
    {
        using var t = new TempDir();
        var zip = GoodZip();

        await Refused(Rel(zip, tag: @"v..\..\x"), new Answer(zip), t.File("download"));

        Assert.Empty(Left(t.File("download")));
    }

    [Fact]
    public async Task Refuses_more_than_the_limit()
    {
        using var t = new TempDir();
        var zip = GoodZip();
        var declared = new Answer(zip, declared: OpenCompositeDownload.MaxBytes + 1);

        await Refused(Rel(zip), declared, t.File("download"));

        Assert.Empty(Left(t.File("download")));
    }

    [Fact]
    public async Task A_stalled_body_times_out_and_leaves_nothing_but_the_folder()
    {
        using var t = new TempDir();
        var root = t.File("download");
        var zip = GoodZip();
        using var http = new HttpClient(new Stall(zip));

        var e = await Assert.ThrowsAsync<MiradorException>(() =>
            OpenCompositeDownload.FetchAsync(http, Rel(zip), root, CancellationToken.None, TimeSpan.FromMilliseconds(200)));

        Assert.Contains("couldn't download", e.Message);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Left(root));
    }

    [Fact]
    public async Task A_dropped_connection_leaves_nothing_but_the_folder()
    {
        using var t = new TempDir();
        var root = t.File("download");
        var zip = GoodZip();

        var e = await Refused(Rel(zip), new Answer(zip, drop: true), root);

        Assert.True(Directory.Exists(root));
        Assert.Empty(Left(root));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }
}
