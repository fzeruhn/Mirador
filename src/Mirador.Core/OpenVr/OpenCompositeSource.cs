using System.Text.RegularExpressions;

namespace Mirador.Core.OpenVr;

/// <summary>
/// Which OpenComposite is installed, from the first line of its <c>SOURCE.txt</c>: the fork's release writes
/// <c>OpenComposite 2026.11.06, commit &lt;40 hex&gt;</c>; a build of the commit alone has the 40 hex alone.
/// </summary>
/// <param name="Version">The release's date version, or null when SOURCE.txt names only the commit.</param>
/// <param name="Commit">The 40-hex commit.</param>
public sealed partial record OpenCompositeSource(string? Version, string? Commit)
{
    /// <summary>The source from a <c>SOURCE.txt</c>'s text, or null when its first line is neither form.</summary>
    public static OpenCompositeSource? Read(string sourceTxt)
    {
        var first = sourceTxt.Split('\n', 2)[0].Trim();

        var release = ReleaseLine().Match(first);
        if (release.Success)
        {
            return new OpenCompositeSource(release.Groups[1].Value, release.Groups[2].Value);
        }

        return CommitLine().IsMatch(first) ? new OpenCompositeSource(null, first) : null;
    }

    /// <summary><c>2026.11.06 (7534249)</c>, or the short commit alone when there is no version.</summary>
    public string Describe()
    {
        var shortCommit = Commit is { Length: > 7 } ? Commit[..7] : Commit;
        return (Version, shortCommit) switch
        {
            (null, null) => "",
            (null, _) => shortCommit!,
            (_, null) => Version,
            _ => $"{Version} ({shortCommit})",
        };
    }

    /// <summary>Orders date versions (<c>2026.11.06</c>) by their numbers, so <c>2027.1.2</c> is newer; null is lowest.</summary>
    public static int Compare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return (a is null ? 0 : 1) - (b is null ? 0 : 1);
        }

        var left = Numbers(a);
        var right = Numbers(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static long[] Numbers(string version) =>
        [.. version.Split('.').Select(part => long.TryParse(part, out var n) ? n : 0)];

    [GeneratedRegex(@"^OpenComposite (\d+(?:\.\d+)+), commit ([0-9a-f]{40})$")]
    private static partial Regex ReleaseLine();

    [GeneratedRegex(@"^[0-9a-f]{40}$")]
    private static partial Regex CommitLine();
}
