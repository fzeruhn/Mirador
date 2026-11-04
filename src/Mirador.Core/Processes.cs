using System.ComponentModel;
using System.Diagnostics;

namespace Mirador.Core;

/// <summary>A running process: its name without <c>.exe</c>, as <see cref="Process.ProcessName"/> gives it, and its id.</summary>
public sealed record ProcInfo(string Name, int Id);

/// <summary>Lists the processes that are running now. The seam that lets checks be tested without the real system.</summary>
public interface IProcessList
{
    IReadOnlyList<ProcInfo> Snapshot();
}

/// <summary>The real list, taken from Windows each time.</summary>
public sealed class SystemProcessList : IProcessList
{
    public IReadOnlyList<ProcInfo> Snapshot()
    {
        var found = new List<ProcInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    found.Add(new ProcInfo(process.ProcessName, process.Id));
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    // It ended while we were listing; it is not running any more.
                }
            }
        }

        return found;
    }
}
