using System.Text;

namespace Mirador.Core.Tests;

public class TextLogTests
{
    /// <summary>Reads a log that may still be open for writing.</summary>
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Writes_each_line_to_the_file_at_once()
    {
        using var dir = new TempDir();
        using var log = new TextLog(dir.File("a.log"));

        log.Write("one");
        log.Write("two");

        // The log is still open; each line was flushed as it was written.
        Assert.Equal(["one", "two"], ReadLines(dir.File("a.log")));
    }

    [Fact]
    public void Creates_the_folder_when_it_is_missing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deep", "er", "a.log");
        using var log = new TextLog(path);

        log.Write("hello");

        Assert.Equal(["hello"], ReadLines(path));
    }

    [Fact]
    public void Adds_nothing_to_the_line()
    {
        using var dir = new TempDir();
        var log = new TextLog(dir.File("a.log"));

        log.Write("  exactly this ");
        log.Dispose();

        Assert.Equal("  exactly this \r\n", File.ReadAllText(dir.File("a.log")));
    }

    [Fact]
    public void Keeps_text_that_is_not_ascii()
    {
        using var dir = new TempDir();
        var log = new TextLog(dir.File("a.log"));

        log.Write("Zoë — ok");
        log.Dispose();

        Assert.Equal("Zoë — ok\r\n", File.ReadAllText(dir.File("a.log"), Encoding.UTF8));
        Assert.Equal(Encoding.UTF8.GetBytes("Zoë — ok\r\n"), File.ReadAllBytes(dir.File("a.log"))); // no byte order mark
    }

    [Fact]
    public void Text_log_keeps_a_tail_and_rolls_over()
    {
        using var dir = new TempDir();
        var path = dir.File("a.log");
        using var log = new TextLog(path, maxBytes: 100);

        for (var i = 10; i < 40; i++)
        {
            log.Write($"line {i}"); // 7 bytes plus the line break: 11 lines fit in 100
        }

        var current = ReadLines(path);
        var rolled = ReadLines(path + ".1");
        Assert.Equal("line 39", current[^1]);
        Assert.NotEmpty(rolled);
        Assert.Equal(current[0], $"line {int.Parse(rolled[^1][5..]) + 1}"); // no line lost between the two files
        Assert.False(File.Exists(path + ".2"));
        Assert.True(new FileInfo(path).Length <= 100);
        Assert.True(new FileInfo(path + ".1").Length <= 100);
        Assert.Equal(30, log.Tail.Count); // the tail is not cut by the roll
    }

    [Fact]
    public void Rolling_replaces_the_older_rolled_file()
    {
        using var dir = new TempDir();
        var path = dir.File("a.log");
        using var log = new TextLog(path, maxBytes: 20);

        log.Write("aaaaaaaaa"); // 11 bytes with the line break
        log.Write("bbbbbbbbb"); // 22 would be over the limit: rolls, .1 holds the a line
        log.Write("ccccccccc"); // rolls again, .1 holds the b line

        Assert.Equal(["bbbbbbbbb"], ReadLines(path + ".1"));
        Assert.Equal(["ccccccccc"], ReadLines(path));
    }

    [Fact]
    public void A_line_longer_than_the_limit_is_written_whole()
    {
        using var dir = new TempDir();
        var path = dir.File("a.log");
        using var log = new TextLog(path, maxBytes: 10);

        log.Write("this line is far longer than ten bytes");

        Assert.Equal(["this line is far longer than ten bytes"], ReadLines(path));
        Assert.False(File.Exists(path + ".1"));
    }

    [Fact]
    public void The_tail_holds_the_last_200_lines()
    {
        using var dir = new TempDir();
        using var log = new TextLog(dir.File("a.log"));

        for (var i = 1; i <= 250; i++)
        {
            log.Write($"line {i}");
        }

        var tail = log.Tail;
        Assert.Equal(200, tail.Count);
        Assert.Equal("line 51", tail[0]);
        Assert.Equal("line 250", tail[^1]);
    }

    [Fact]
    public void The_tail_is_a_snapshot()
    {
        using var dir = new TempDir();
        using var log = new TextLog(dir.File("a.log"));
        log.Write("one");
        var before = log.Tail;

        log.Write("two");

        Assert.Equal(["one"], before);
        Assert.Equal(["one", "two"], log.Tail);
    }

    [Fact]
    public void Nothing_is_created_before_the_first_write()
    {
        using var dir = new TempDir();
        using var log = new TextLog(dir.File("a.log"));

        Assert.Empty(log.Tail);
        Assert.False(File.Exists(dir.File("a.log")));
    }

    [Fact]
    public void Opening_again_appends_and_counts_what_is_already_there()
    {
        using var dir = new TempDir();
        var path = dir.File("a.log");
        using (var first = new TextLog(path, maxBytes: 30))
        {
            first.Write("aaaaaaaaa"); // 11 bytes
            first.Write("bbbbbbbbb"); // 22 bytes
        }

        using var second = new TextLog(path, maxBytes: 30);
        second.Write("ccccccccc"); // 33 would be over the limit: rolls first

        Assert.Equal(["aaaaaaaaa", "bbbbbbbbb"], ReadLines(path + ".1"));
        Assert.Equal(["ccccccccc"], ReadLines(path));
    }

    [Fact]
    public void Many_threads_can_write_at_once_without_mixing_lines()
    {
        using var dir = new TempDir();
        var path = dir.File("a.log");
        var log = new TextLog(path, maxBytes: 100_000_000);

        Parallel.For(0, 8, thread =>
        {
            for (var i = 0; i < 200; i++)
            {
                log.Write($"thread {thread} line {i} " + new string('x', 50));
            }
        });
        log.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.Equal(1600, lines.Length);
        Assert.Equal(1600, lines.Distinct().Count());
        Assert.All(lines, line => Assert.EndsWith(new string('x', 50), line));
    }

    [Fact]
    public void Writing_after_dispose_throws_and_disposing_twice_is_fine()
    {
        using var dir = new TempDir();
        var log = new TextLog(dir.File("a.log"));
        log.Write("one");

        log.Dispose();
        log.Dispose();

        Assert.Throws<ObjectDisposedException>(() => log.Write("two"));
    }

    [Fact]
    public void The_file_can_be_deleted_after_dispose()
    {
        using var dir = new TempDir();
        var log = new TextLog(dir.File("a.log"));
        log.Write("one");

        log.Dispose();

        File.Delete(dir.File("a.log"));
        Assert.False(File.Exists(dir.File("a.log")));
    }
}
