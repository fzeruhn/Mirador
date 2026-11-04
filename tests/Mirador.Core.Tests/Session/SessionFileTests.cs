using Mirador.Core.Session;

namespace Mirador.Core.Tests.Session;

public class SessionFileTests
{
    [Fact]
    public void Reads_back_what_was_written()
    {
        using var dir = new TempDir();
        var file = new SessionFile(dir.File("session.json"));
        IReadOnlyDictionary<string, string> data = new Dictionary<string, string> { ["wasRunning"] = "true", ["other"] = "Zoë" };

        file.Write("p1", "dreamair", data);
        var read = file.Read();

        Assert.Equal("p1", read?.ProfileId);
        Assert.Equal("dreamair", read?.ModuleId);
        Assert.Equal(data, read?.Data);
    }

    [Fact]
    public void A_profile_without_a_module_reads_back_with_none()
    {
        using var dir = new TempDir();
        var file = new SessionFile(dir.File("session.json"));

        file.Write("custom", null, new Dictionary<string, string>());
        var read = file.Read();

        Assert.Equal("custom", read?.ProfileId);
        Assert.Null(read?.ModuleId);
        Assert.Empty(read?.Data ?? throw new Xunit.Sdk.XunitException("no session read"));
    }

    [Fact]
    public void No_file_is_no_session()
    {
        using var dir = new TempDir();

        Assert.Null(new SessionFile(dir.File("session.json")).Read());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"moduleId": "dreamair"}""")]
    [InlineData("[]")]
    public void A_corrupt_file_is_no_session_and_is_kept_aside(string text)
    {
        using var dir = new TempDir();
        var path = dir.File("session.json");
        File.WriteAllText(path, text);

        Assert.Null(new SessionFile(path).Read());
        Assert.False(File.Exists(path));
        Assert.Equal(text, File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void Missing_values_in_the_data_are_left_out()
    {
        using var dir = new TempDir();
        var path = dir.File("session.json");
        File.WriteAllText(path, """{"profileId": "p1", "data": {"kept": "yes", "gone": null}}""");

        var read = new SessionFile(path).Read();

        IReadOnlyDictionary<string, string> expected = new Dictionary<string, string> { ["kept"] = "yes" };
        Assert.Equal(expected, read?.Data);
    }

    [Fact]
    public void Delete_removes_the_file_and_is_fine_without_one()
    {
        using var dir = new TempDir();
        var file = new SessionFile(dir.File("session.json"));
        file.Write("p1", null, new Dictionary<string, string>());

        file.Delete();
        file.Delete();
        new SessionFile(Path.Combine(dir.Path, "no folder", "session.json")).Delete();

        Assert.False(File.Exists(dir.File("session.json")));
    }
}
