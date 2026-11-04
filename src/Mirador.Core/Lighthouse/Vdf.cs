using System.Text;

namespace Mirador.Core.Lighthouse;

/// <summary>
/// A value in a Valve KeyValues ("VDF") file: either a string (<see cref="Text"/>) or an object that holds more
/// keys (<see cref="Members"/>, in the order of the file).
/// </summary>
internal sealed class VdfNode
{
    public VdfNode(string text)
    {
        Text = text;
        Members = [];
    }

    public VdfNode(IReadOnlyList<KeyValuePair<string, VdfNode>> members) => Members = members;

    /// <summary>The string, or null when this is an object.</summary>
    public string? Text { get; }

    public bool IsObject => Text is null;

    /// <summary>The keys of an object, in file order; none for a string. A key may repeat.</summary>
    public IReadOnlyList<KeyValuePair<string, VdfNode>> Members { get; }

    /// <summary>The first member called <paramref name="key"/> (matched exactly), or null.</summary>
    public VdfNode? Get(string key)
    {
        foreach (var member in Members)
        {
            if (member.Key == key)
            {
                return member.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// A small reader for the KeyValues text Steam writes (<c>libraryfolders.vdf</c> and its kin): quoted keys and values,
/// <c>{ }</c> for nesting, and the escapes <c>\\</c> and <c>\"</c> inside a string. Any other backslash stays as written.
/// </summary>
internal static class Vdf
{
    /// <summary>Deeper than any Steam file goes; stops hostile text from overflowing the stack.</summary>
    private const int MaxDepth = 64;

    /// <summary>Reads <paramref name="text"/> as the members of an unnamed top-level object.</summary>
    /// <returns>That object, or null when the text is not well-formed. Never throws.</returns>
    public static VdfNode? Parse(string text)
    {
        var position = 0;
        var members = ReadMembers(text, ref position, depth: 0);
        return members is null ? null : new VdfNode(members);
    }

    /// <summary>Reads keys and values until the closing brace (or, at depth 0, the end of the text).</summary>
    private static List<KeyValuePair<string, VdfNode>>? ReadMembers(string text, ref int position, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        var members = new List<KeyValuePair<string, VdfNode>>();
        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position == text.Length)
            {
                return depth == 0 ? members : null; // an object that never closes
            }

            if (text[position] == '}')
            {
                if (depth == 0)
                {
                    return null; // closes something that was never opened
                }

                position++;
                return members;
            }

            var key = ReadString(text, ref position);
            if (key is null)
            {
                return null;
            }

            SkipWhitespace(text, ref position);
            if (position == text.Length)
            {
                return null; // a key with no value
            }

            VdfNode value;
            if (text[position] == '{')
            {
                position++;
                var inner = ReadMembers(text, ref position, depth + 1);
                if (inner is null)
                {
                    return null;
                }

                value = new VdfNode(inner);
            }
            else
            {
                var str = ReadString(text, ref position);
                if (str is null)
                {
                    return null;
                }

                value = new VdfNode(str);
            }

            members.Add(new KeyValuePair<string, VdfNode>(key, value));
        }
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    /// <summary>Reads a quoted string starting at the opening quote; null when there is none or it never closes.</summary>
    private static string? ReadString(string text, ref int position)
    {
        if (position >= text.Length || text[position] != '"')
        {
            return null;
        }

        position++;
        var result = new StringBuilder();
        while (position < text.Length)
        {
            var c = text[position++];
            if (c == '"')
            {
                return result.ToString();
            }

            if (c == '\\' && position < text.Length && text[position] is '\\' or '"')
            {
                c = text[position++];
            }

            result.Append(c);
        }

        return null;
    }
}
