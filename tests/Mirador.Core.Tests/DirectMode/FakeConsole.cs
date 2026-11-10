using Mirador.Core.DirectMode;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Monado;

namespace Mirador.Core.Tests.DirectMode;

/// <summary>A console program that writes the lines it is given and ends when told to.</summary>
internal sealed class FakeConsole : IConsoleProcess
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> written = [];
    private Action<string>? listeners;
    private List<string>? early = [];

    /// <summary>The lines to write at once, before any handler is added (they are replayed to the first).</summary>
    public FakeConsole(params string[] firstLines) => early = [.. firstLines];

    /// <summary>Lines written when the program reads the empty line, then it ends with this exit code; null: it doesn't end.</summary>
    public string[] LinesAtEnter { get; set; } = [];

    public int? ExitCodeAtEnter { get; set; } = 0;

    public IReadOnlyList<string> Written { get { lock (gate) { return [.. written]; } } }

    public bool InputClosed { get; private set; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<string>? Line
    {
        add
        {
            lock (gate)
            {
                listeners += value;
                if (early is { } replay)
                {
                    early = null;
                    replay.ForEach(line => value?.Invoke(line));
                }
            }
        }

        remove { lock (gate) { listeners -= value; } }
    }

    public bool HasExited => exited.Task.IsCompleted;

    public Task<int> Exited => exited.Task;

    public void Emit(string line)
    {
        lock (gate)
        {
            if (early is not null)
            {
                early.Add(line);
            }
            else
            {
                listeners?.Invoke(line);
            }
        }
    }

    public void End(int code) => exited.TrySetResult(code);

    public void WriteLine(string s)
    {
        lock (gate)
        {
            written.Add(s);
        }

        if (s.Length == 0)
        {
            foreach (var line in LinesAtEnter)
            {
                Emit(line);
            }

            if (ExitCodeAtEnter is { } code)
            {
                End(code);
            }
        }
    }

    public void CloseInput() => InputClosed = true;

    public bool WaitForExit(TimeSpan t) => exited.Task.Wait(t);

    public void Kill()
    {
        Killed = true;
        End(-1);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Starts <see cref="FakeConsole"/>s, recording what it was asked to run.</summary>
internal sealed class FakeConsoleStarter(Func<ProcessSpec, FakeConsole> make)
{
    public List<ProcessSpec> Specs { get; } = [];

    public FakeConsole? Last { get; private set; }

    public MiradorException? Refusal { get; set; }

    public IConsoleProcess Start(ProcessSpec spec)
    {
        Specs.Add(spec);
        if (Refusal is not null)
        {
            throw Refusal;
        }

        return Last = make(spec);
    }
}

internal sealed class FakeUsbDevices(params UsbId[] ids) : IUsbDevices
{
    public HashSet<UsbId> Ids { get; } = [.. ids];

    public IReadOnlySet<UsbId> Present() => Ids.ToHashSet();
}

internal sealed class FakeProcesses : IProcessList
{
    private readonly List<ProcInfo> running = [];

    public void Add(string name, int id = 42)
    {
        lock (running)
        {
            running.Add(new ProcInfo(name, id));
        }
    }

    public void Remove(string name)
    {
        lock (running)
        {
            running.RemoveAll(p => p.Name == name);
        }
    }

    public IReadOnlyList<ProcInfo> Snapshot()
    {
        lock (running)
        {
            return [.. running];
        }
    }
}
