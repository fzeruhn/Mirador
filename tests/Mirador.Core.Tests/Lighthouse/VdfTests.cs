using Mirador.Ipc;

namespace Mirador.Core.Tests.Lighthouse;

public class VdfTests
{
    [Fact]
    public void Reads_nested_objects_and_strings()
    {
        var root = Vdf.Parse("\"outer\" { \"name\" \"value\" \"inner\" { \"deep\" \"1\" } }");

        var outer = root!.Get("outer")!;
        Assert.True(outer.IsObject);
        Assert.Equal("value", outer.Get("name")!.Text);
        Assert.Equal("1", outer.Get("inner")!.Get("deep")!.Text);
    }

    [Fact]
    public void Members_keep_the_order_of_the_file_and_repeated_keys_stay()
    {
        var root = Vdf.Parse("\"b\" \"1\" \"a\" \"2\" \"b\" \"3\"")!;

        Assert.Equal(["b", "a", "b"], root.Members.Select(m => m.Key));
        Assert.Equal("1", root.Get("b")!.Text); // the first one answers
    }

    [Fact]
    public void A_missing_key_or_a_key_asked_of_a_string_gives_null()
    {
        var root = Vdf.Parse("\"a\" \"1\" \"o\" { }")!;

        Assert.Null(root.Get("missing"));
        Assert.Null(root.Get("a")!.Get("anything"));
        Assert.True(root.Get("o")!.IsObject);
        Assert.Empty(root.Get("o")!.Members);
    }

    [Fact]
    public void Keys_are_matched_exactly()
    {
        Assert.Null(Vdf.Parse("\"Path\" \"x\"")!.Get("path"));
    }

    [Fact]
    public void Backslash_and_quote_escapes_are_undone()
    {
        var root = Vdf.Parse("\"path\" \"C:\\\\Program Files (x86)\\\\Steam\" \"said\" \"a \\\"quoted\\\" word\"")!;

        Assert.Equal(@"C:\Program Files (x86)\Steam", root.Get("path")!.Text);
        Assert.Equal("a \"quoted\" word", root.Get("said")!.Text);
    }

    [Fact]
    public void Any_other_backslash_is_kept_as_written()
    {
        var root = Vdf.Parse("\"path\" \"D:\\SteamLibrary\"")!;

        Assert.Equal(@"D:\SteamLibrary", root.Get("path")!.Text);
    }

    [Fact]
    public void Tabs_spaces_and_line_breaks_all_separate_tokens()
    {
        var root = Vdf.Parse("\r\n\"a\"\t\t\"1\"\r\n\t\"o\"\r\n\t{\r\n\t\t\"b\"   \"2\"\r\n\t}\r\n")!;

        Assert.Equal("1", root.Get("a")!.Text);
        Assert.Equal("2", root.Get("o")!.Get("b")!.Text);
    }

    [Fact]
    public void An_empty_value_is_a_string()
    {
        var label = Vdf.Parse("\"label\" \"\"")!.Get("label")!;

        Assert.False(label.IsObject);
        Assert.Equal("", label.Text);
    }

    [Fact]
    public void An_empty_document_has_no_members()
    {
        Assert.Empty(Vdf.Parse("  \n")!.Members);
    }

    [Theory]
    [InlineData("\"a\"")] // key with no value
    [InlineData("\"a\" \"unterminated")] // string never closes
    [InlineData("\"a\" \"ends in a backslash\\")] // escape at the end
    [InlineData("\"a\" {")] // object never closes
    [InlineData("\"a\" { \"b\" \"1\"")] // object never closes, after a member
    [InlineData("\"a\" \"1\" }")] // closing brace with nothing open
    [InlineData("{ \"a\" \"1\" }")] // object with no key
    [InlineData("a \"1\"")] // unquoted key
    [InlineData("\"a\" 1")] // unquoted value
    [InlineData("\"a\" { \"b\" } ")] // key inside an object with no value
    public void Malformed_text_gives_null(string text)
    {
        Assert.Null(Vdf.Parse(text));
    }

    [Fact]
    public void Objects_nested_absurdly_deep_give_null_instead_of_overflowing_the_stack()
    {
        var text = string.Concat(Enumerable.Repeat("\"a\" { ", 10_000)) + string.Concat(Enumerable.Repeat("} ", 10_000));

        Assert.Null(Vdf.Parse(text));
    }

    [Fact]
    public void Ordinary_nesting_is_not_limited()
    {
        var text = string.Concat(Enumerable.Repeat("\"a\" { ", 20)) + "\"leaf\" \"1\" " + string.Concat(Enumerable.Repeat("} ", 20));

        var node = Vdf.Parse(text)!;
        for (var i = 0; i < 20; i++)
        {
            node = node.Get("a")!;
        }

        Assert.Equal("1", node.Get("leaf")!.Text);
    }
}
