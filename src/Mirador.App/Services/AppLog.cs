using System.Globalization;
using Mirador.Core;

namespace Mirador.App.Services;

/// <summary>Mirador's own log, <c>logs\mirador.log</c>: what it did and what went wrong, with the time of each line.</summary>
public static class AppLog
{
    private static TextLog? log;

    /// <summary>The log's lines so far, oldest first (the last 200).</summary>
    public static IReadOnlyList<string> Tail => log?.Tail ?? [];

    /// <summary>Raised, on the writing thread, after each line.</summary>
    public static event Action<string>? LineWritten;

    public static void Open(AppPaths paths) => log ??= new TextLog(paths.AppLogFile);

    public static void Write(string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}";
        try
        {
            log?.Write(line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is for finding out what happened; Mirador goes on without it.
        }

        LineWritten?.Invoke(line);
    }

    public static void Write(string what, Exception e) => Write($"{what}: {e}");

    public static void Close()
    {
        log?.Dispose();
        log = null;
    }
}
