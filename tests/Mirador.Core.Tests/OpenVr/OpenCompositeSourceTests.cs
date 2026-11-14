using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class OpenCompositeSourceTests
{
    private const string Commit = "75342495640501953fc2c17f73eeb4d124e8528e";

    [Fact]
    public void Reads_a_release_source_txt()
    {
        var source = OpenCompositeSource.Read($"OpenLamina 2026.11.06, commit {Commit}\nOpenComposite from the fork ...\n");

        Assert.NotNull(source);
        Assert.Equal("2026.11.06", source.Version);
        Assert.Equal(Commit, source.Commit);
        Assert.Equal("2026.11.06 (7534249)", source.Describe());
    }

    [Fact]
    public void Reads_a_release_source_txt_from_before_the_rename()
    {
        var source = OpenCompositeSource.Read($"OpenComposite 2026.11.06, commit {Commit}\nFrom https://github.com/fzeruhn/OpenComposite\n");

        Assert.NotNull(source);
        Assert.Equal("2026.11.06", source.Version);
        Assert.Equal(Commit, source.Commit);
    }

    [Fact]
    public void Reads_an_old_commit_only_source_txt()
    {
        var source = OpenCompositeSource.Read($"{Commit}\r\nOpenComposite from Mirador's fork ...\r\n");

        Assert.NotNull(source);
        Assert.Null(source.Version);
        Assert.Equal(Commit, source.Commit);
        Assert.Equal("7534249", source.Describe());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("something else")]
    [InlineData("OpenLamina 2026.11.06, commit 7534249")]
    [InlineData("7534249")]
    public void Other_text_is_null(string text)
    {
        Assert.Null(OpenCompositeSource.Read(text));
    }

    [Fact]
    public void Date_versions_compare_as_numbers()
    {
        Assert.True(OpenCompositeSource.Compare("2026.11.06", "2026.11.20") < 0);
        Assert.True(OpenCompositeSource.Compare("2026.11.20", "2027.1.2") < 0);
        Assert.True(OpenCompositeSource.Compare("2027.1.2", "2026.11.20") > 0);
        Assert.Equal(0, OpenCompositeSource.Compare("2026.11.06", "2026.11.6"));
        Assert.True(OpenCompositeSource.Compare(null, "2026.11.06") < 0);
        Assert.True(OpenCompositeSource.Compare("2026.11.06", null) > 0);
        Assert.Equal(0, OpenCompositeSource.Compare(null, null));
    }
}
