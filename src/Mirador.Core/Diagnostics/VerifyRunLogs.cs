namespace Mirador.Core.Diagnostics;

/// <summary>The <c>verify-run-*.log</c> files "Verify build for Windows" writes into Mirador's log folder, one per run.</summary>
public static class VerifyRunLogs
{
    /// <summary>How many run logs stay, counting the one a run is about to write.</summary>
    public const int Kept = 10;

    /// <summary>
    /// Before a run writes its log: deletes all but the newest <see cref="Kept"/> − 1 run logs, never one a profile's
    /// saved result still names (<paramref name="inUse"/>: Diagnostics shows it). The names hold the run's time, so
    /// they sort by age. A file that can't be deleted now (open elsewhere) goes at a later run. Never throws for the
    /// folder or a file.
    /// </summary>
    public static void Prune(string logsDir, IEnumerable<string?> inUse)
    {
        var keep = new HashSet<string>(
            inUse.Where(f => f is not null).Select(f => Path.GetFileName(f!)),
            StringComparer.OrdinalIgnoreCase);
        string[] logs;
        try
        {
            if (!Directory.Exists(logsDir))
            {
                return;
            }

            logs = Directory.GetFiles(logsDir, DiagnosticsZip.VerifyLogPrefix + "*.log");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // the folder can't be listed now; the next run tidies up
        }

        foreach (var old in logs.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Skip(Kept - 1))
        {
            if (keep.Contains(Path.GetFileName(old)))
            {
                continue;
            }

            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Open in another program; it goes at a later run.
            }
        }
    }
}
