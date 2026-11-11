using System.Text.RegularExpressions;

namespace Mirador.Core.Profiles;

/// <summary>
/// The link an owner pastes for a branch profile (Mirador.md §4.11 item 49), and the checks that keep its URL, ref and
/// options from becoming anything but a clone of a public repository over HTTPS: no local path, no other protocol,
/// nothing git or CMake would read as an option of their own.
/// </summary>
public static partial class BranchLink
{
    /// <summary>
    /// A repository and ref from a pasted link: a repository URL (the ref then <c>main</c>), a GitLab or GitHub branch
    /// page (<c>…/-/tree/&lt;branch&gt;</c>, <c>…/tree/&lt;branch&gt;</c>), a GitLab merge request
    /// (<c>…/-/merge_requests/&lt;n&gt;</c>, built from <c>refs/merge-requests/&lt;n&gt;/head</c>) or a GitHub pull
    /// request (<c>…/pull/&lt;n&gt;</c>, from <c>refs/pull/&lt;n&gt;/head</c>).
    /// </summary>
    /// <exception cref="MiradorException">The link is none of these; the message says what is accepted.</exception>
    public static (string Url, string Ref) Parse(string link)
    {
        var text = (link ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new MiradorException("Paste the https:// link of a repository, a branch or a merge request on GitLab or GitHub.");
        }

        var path = uri.AbsolutePath.Trim('/');
        string repoPath, gitRef;
        if (MergeRequest().Match(path) is { Success: true } mr)
        {
            (repoPath, gitRef) = (mr.Groups[1].Value, $"refs/merge-requests/{mr.Groups[2].Value}/head");
        }
        else if (PullRequest().Match(path) is { Success: true } pr)
        {
            (repoPath, gitRef) = (pr.Groups[1].Value, $"refs/pull/{pr.Groups[2].Value}/head");
        }
        else if (Tree().Match(path) is { Success: true } tree)
        {
            (repoPath, gitRef) = (tree.Groups[1].Value, Uri.UnescapeDataString(tree.Groups[2].Value));
        }
        else if (!path.Contains("/-/", StringComparison.Ordinal) && path.Count(c => c == '/') >= 1
            // On GitHub a repository is owner/name; anything longer is another of its pages (a commit, a file, releases).
            && !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && path.Count(c => c == '/') != 1))
        {
            (repoPath, gitRef) = (path, BuildSource.UpstreamRef);
        }
        else
        {
            throw new MiradorException("That link is no repository, branch or merge request page. Paste the link of the branch's page.");
        }

        if (repoPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repoPath = repoPath[..^4];
        }

        var url = $"https://{uri.Authority}/{repoPath}.git";
        var problem = UrlProblem(url) ?? RefProblem(gitRef);
        return problem is null ? (url, gitRef) : throw new MiradorException(problem);
    }

    /// <summary>Null when <paramref name="url"/> is an HTTPS repository URL Mirador may clone; else why not.</summary>
    public static string? UrlProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "The branch profile needs a repository link.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || url.Any(char.IsWhiteSpace) || url.StartsWith('-'))
        {
            return $"\"{url}\" isn't an https:// repository link. Mirador builds only from repositories on the web, such as GitLab or GitHub.";
        }

        return null;
    }

    /// <summary>
    /// Null when <paramref name="gitRef"/> is a branch or ref git accepts and can't take for an option (no leading
    /// <c>-</c>); else why not. The rules are git's own (<c>git check-ref-format</c>), a little stricter.
    /// </summary>
    public static string? RefProblem(string? gitRef)
    {
        if (string.IsNullOrWhiteSpace(gitRef))
        {
            return "The branch profile needs a branch.";
        }

        if (gitRef.StartsWith('-') || gitRef.StartsWith('/') || gitRef.EndsWith('/') || gitRef.EndsWith('.')
            || gitRef.EndsWith(".lock", StringComparison.Ordinal) || gitRef.Contains("..", StringComparison.Ordinal)
            || gitRef.Contains("//", StringComparison.Ordinal) || gitRef.Contains("@{", StringComparison.Ordinal)
            || gitRef.Contains("/.", StringComparison.Ordinal) || gitRef.StartsWith('.') || !RefCharacters().IsMatch(gitRef))
        {
            return $"\"{gitRef}\" isn't a branch name git accepts.";
        }

        return null;
    }

    /// <summary>
    /// Null when every option is a CMake cache setting (<c>-DNAME=value</c> or <c>-DNAME:TYPE=value</c>); else why not.
    /// Other options (<c>-C</c> loads a script, <c>-G</c> changes the generator) are refused: the recipe sets those.
    /// </summary>
    public static string? OptionsProblem(IEnumerable<string>? options)
    {
        foreach (var option in options ?? [])
        {
            if (!CMakeDefine().IsMatch(option))
            {
                return $"\"{option}\" isn't a CMake setting of the form -DNAME=value.";
            }
        }

        return null;
    }

    /// <summary>The options typed one per line or separated by spaces, each kept whole.</summary>
    public static IReadOnlyList<string> SplitOptions(string text) =>
        (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The problems of a branch source, or null when it can be built.</summary>
    public static string? Problem(BuildSource source) =>
        UrlProblem(source.Url) ?? RefProblem(source.Ref) ?? OptionsProblem(source.Options);

    [GeneratedRegex(@"^(.+?)/-/merge_requests/(\d+)(?:/.*)?$")]
    private static partial Regex MergeRequest();

    [GeneratedRegex(@"^([^/]+/[^/]+)/pull/(\d+)(?:/.*)?$")]
    private static partial Regex PullRequest();

    [GeneratedRegex(@"^(.+?)(?:/-)?/tree/(.+)$")]
    private static partial Regex Tree();

    [GeneratedRegex(@"^[A-Za-z0-9._/+-]+$")]
    private static partial Regex RefCharacters();

    [GeneratedRegex(@"^-D[A-Za-z_][A-Za-z0-9_.+-]*(:[A-Z]+)?=[^\r\n]*$")]
    private static partial Regex CMakeDefine();
}
