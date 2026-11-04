namespace Mirador.Helper.Tests;

/// <summary>Records what the handler asked of the services; nothing touches Windows.</summary>
internal sealed class FakeServiceControl : IServiceControl
{
    public bool Installed { get; set; } = true;
    public bool Running { get; set; }
    public Exception? StopFails { get; set; }
    public List<string> Stopped { get; } = [];
    public List<TimeSpan> StopTimeouts { get; } = [];
    public List<string> Started { get; } = [];

    public bool Exists(string name) => Installed;

    public bool IsRunning(string name) => Installed && Running;

    public void Stop(string name, TimeSpan timeout)
    {
        if (StopFails is not null)
        {
            throw StopFails;
        }

        Stopped.Add(name);
        StopTimeouts.Add(timeout);
        Running = false;
    }

    public void Start(string name)
    {
        Started.Add(name);
        Running = true;
    }
}

/// <summary>Records which process names were ended and answers command-line queries from a table.</summary>
internal sealed class FakeProcessControl : IProcessControl
{
    public List<string> Killed { get; } = [];
    public Dictionary<string, string[]> Lines { get; } = [];
    public List<string> Queried { get; } = [];

    public void KillAll(IEnumerable<string> names) => Killed.AddRange(names);

    public IReadOnlyList<string> CommandLines(string processName)
    {
        Queried.Add(processName);
        return Lines.TryGetValue(processName, out var lines) ? lines : [];
    }
}

/// <summary>The two registry values the helper manages, held in memory.</summary>
internal sealed class FakeRuntimeRegistry : IRuntimeRegistry
{
    public string? Active { get; set; }
    public string? Recorded { get; set; }
    public Exception? Fails { get; set; }
    public int Writes { get; private set; }

    /// <summary>The most calls to <see cref="GetActive"/> that were inside it at the same moment.</summary>
    public int MaxConcurrent { get; private set; }

    private int inside;

    public string? GetActive()
    {
        var now = Interlocked.Increment(ref inside);
        lock (this)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, now);
        }

        Thread.Sleep(15);
        Interlocked.Decrement(ref inside);
        return Active;
    }

    public void SetActive(string path)
    {
        Write();
        Active = path;
    }

    public string? GetRecorded() => Recorded;

    public void SetRecorded(string? path)
    {
        Write();
        Recorded = path;
    }

    private void Write()
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Writes++;
    }
}
