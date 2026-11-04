using System.Diagnostics;

namespace Mirador.Ipc;

/// <summary>
/// The pipe a running Mirador listens on for its signed-in user: a second <c>Mirador.exe</c> asks it to show its
/// window, and the stand-in (a small C program a game starts in place of <c>monado-service</c>) asks it to start
/// Monado. Plain lines of ASCII ended by <c>\n</c>, one request and one reply per connection, so the C side stays simple.
/// </summary>
public static class AppPipe
{
    /// <summary>Start Monado; the reply is <see cref="Ok"/> once it is up, or <see cref="FailPrefix"/> and why not.</summary>
    public const string Start = "START";

    /// <summary>Show the window; the reply is <see cref="Ok"/>.</summary>
    public const string Show = "SHOW";

    public const string Ok = "OK";

    /// <summary>A refusal or failure: the rest of the line is the message for the user.</summary>
    public const string FailPrefix = "FAIL ";

    /// <summary>The longest line either side sends, not counting its newline.</summary>
    public const int MaxLineBytes = 256;

    /// <summary>The pipe's name for Windows session <paramref name="sessionId"/>; the full name is <c>\\.\pipe\mirador-app-&lt;id&gt;</c>.</summary>
    public static string NameFor(int sessionId) => $"mirador-app-{sessionId}";

    /// <summary>The pipe's name for this process's Windows session.</summary>
    public static string CurrentName()
    {
        using var self = Process.GetCurrentProcess();
        return NameFor(self.SessionId);
    }

    /// <summary>
    /// <paramref name="text"/> as one line that fits: line breaks become spaces, characters outside printable ASCII
    /// become <c>?</c> (the stand-in prints it as it is), and it is cut at <see cref="MaxLineBytes"/>.
    /// </summary>
    public static string OneLine(string text)
    {
        var line = new string(text.Replace("\r\n", " ").Select(c => c is '\r' or '\n' or '\t' ? ' ' : c is >= ' ' and <= '~' ? c : '?').ToArray());
        return line.Length <= MaxLineBytes ? line : line[..(MaxLineBytes - 3)] + "...";
    }
}
