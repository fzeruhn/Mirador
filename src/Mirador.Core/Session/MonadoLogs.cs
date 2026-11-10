using Mirador.Ipc;
namespace Mirador.Core.Session;

/// <summary>The <c>monado-*.log</c> files a session writes into Mirador's log folder, one per Start.</summary>
internal static class MonadoLogs
{
    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> Monado logs, counting <paramref name="newLog"/> (which need not
    /// exist yet), each with its rolled-over <c>.1</c> file; a <c>.1</c> whose log is gone counts as that log. The names
    /// hold the start time, so they sort by age. A file that can't be deleted now (open elsewhere) goes at a later Start.
    /// </summary>
    public static void Prune(string logsDir, string newLog, int keep)
    {
        if (!Directory.Exists(logsDir))
        {
            return;
        }

        var logs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(newLog) };
        try
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "monado-*"))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase))
                {
                    logs.Add(name[..^2]);
                }
                else if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                {
                    logs.Add(name);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // the folder can't be listed now; the next Start tidies up
        }

        foreach (var old in logs.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).Skip(keep))
        {
            Cleanup.TryDelete(Path.Combine(logsDir, old));
            Cleanup.TryDelete(Path.Combine(logsDir, old + ".1"));
        }
    }

}
