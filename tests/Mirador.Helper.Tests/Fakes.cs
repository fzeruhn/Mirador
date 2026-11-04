namespace Mirador.Helper.Tests;

/// <summary>Records what the handler asked of the services; nothing touches Windows.</summary>
internal sealed class FakeServiceControl : IServiceControl
{
    public bool Installed { get; set; } = true;
    public bool Running { get; set; }

    /// <summary>A stop that timed out and hasn't finished: not running, but not stopped either.</summary>
    public bool Stopping { get; set; }
    public Exception? StopFails { get; set; }

    /// <summary>When set, <see cref="Stop"/> waits for it (after raising <see cref="StopEntered"/>).</summary>
    public ManualResetEventSlim? StopGate { get; set; }

    public ManualResetEventSlim StopEntered { get; } = new();
    public List<string> Stopped { get; } = [];
    public List<TimeSpan> StopTimeouts { get; } = [];
    public List<string> Started { get; } = [];

    /// <summary>Pimax's service's <c>ImagePath</c>; its folder is where Pimax's eye runtime may run from.</summary>
    public string? PimaxImagePath { get; set; } = $"\"{Path.Combine(HelperHandlerTests.PimaxRuntime, "PiServiceLauncher.exe")}\"";

    public string? ImagePath(string name) => Installed ? PimaxImagePath : null;

    public bool Exists(string name) => Installed;

    public bool IsRunning(string name) => Installed && (Running || Stopping);

    public void Stop(string name, TimeSpan timeout)
    {
        StopEntered.Set();
        StopGate?.Wait();
        if (StopFails is not null)
        {
            throw StopFails;
        }

        Stopped.Add(name);
        StopTimeouts.Add(timeout);
        Running = false;
    }

    public List<TimeSpan> StartTimeouts { get; } = [];

    /// <summary>Like the real one: a service that runs already is left alone; one still stopping is waited for, then started.</summary>
    public void Start(string name, TimeSpan timeout)
    {
        StartTimeouts.Add(timeout);
        if (!Running)
        {
            Stopping = false;
            Started.Add(name);
            Running = true;
        }
    }
}

/// <summary>Records which process names were ended and answers command-line queries from a table.</summary>
internal sealed class FakeProcessControl : IProcessControl
{
    public List<string> Killed { get; } = [];
    public Exception? KillFails { get; set; }
    public Dictionary<string, string[]> Lines { get; } = [];

    /// <summary>The program file every listed process runs (Pimax's eye runtime in its own folder, unless a test says otherwise).</summary>
    public string? ExecutablePath { get; set; } = HelperHandlerTests.EyeRuntimePath;
    public List<string> Queried { get; } = [];

    public void KillAll(IEnumerable<string> names)
    {
        Killed.AddRange(names);
        if (KillFails is not null)
        {
            throw KillFails;
        }
    }

    public IReadOnlyList<ProcessCommandLine> CommandLines(string processName)
    {
        Queried.Add(processName);
        return Lines.TryGetValue(processName, out var lines) ? [.. lines.Select(l => new ProcessCommandLine(ExecutablePath, l))] : [];
    }
}

/// <summary>The host's lifetime, counting how often the helper asked it to stop.</summary>
internal sealed class FakeLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
{
    private int stopRequests;

    public int StopRequests => Volatile.Read(ref stopRequests);

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => Interlocked.Increment(ref stopRequests);
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
