using Mirador.Core.DirectMode;
using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Core.Session;
using Mirador.Core.Tests.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Session;

/// <summary>What the fakes were asked to do, in order, from any thread.</summary>
internal sealed class Steps
{
    private readonly List<string> list = [];

    public void Add(string step)
    {
        lock (list)
        {
            list.Add(step);
        }
    }

    public string[] All
    {
        get
        {
            lock (list)
            {
                return [.. list];
            }
        }
    }
}

internal sealed class FakeProcessList(Steps steps) : IProcessList
{
    public List<ProcInfo> Running { get; } = [];

    /// <summary>The program file of each process whose file can be read; the others can't.</summary>
    public Dictionary<int, string> Images { get; } = [];

    /// <summary>Processes that end right after the next snapshot has listed them.</summary>
    public HashSet<int> EndAfterSnapshot { get; } = [];

    public IReadOnlyList<ProcInfo> Snapshot()
    {
        steps.Add("processes");
        ProcInfo[] now = [.. Running];
        Running.RemoveAll(p => EndAfterSnapshot.Remove(p.Id));
        return now;
    }

    public string? ImagePathOf(int id) => Images.GetValueOrDefault(id);

    public void Add(string name, int id, string? image = null)
    {
        Running.Add(new ProcInfo(name, id));
        if (image is not null)
        {
            Images[id] = image;
        }
    }
}

/// <summary>A process that does what the test says. Lines sent before anyone listens go to the first listener, as the real one does.</summary>
internal sealed class FakeProcess(Steps steps) : IRunningProcess
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<string>? listeners;
    private List<string>? early = [];

    public int Id => 4321;

    public Task<int> Exited => exited.Task;

    /// <summary>The exit code a kill leaves.</summary>
    public int KilledExitCode { get; set; } = 1;

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public int Listeners
    {
        get
        {
            lock (gate)
            {
                return listeners?.GetInvocationList().Length ?? 0;
            }
        }
    }

    public event Action<Exception>? ListenerFailed;

    public event Action<string>? Line
    {
        add
        {
            List<Exception> failures = [];
            lock (gate)
            {
                listeners += value;
                if (early is { } missed && value is not null)
                {
                    early = null;
                    foreach (var text in missed)
                    {
                        Deliver(value, text, failures);
                    }
                }
            }

            Report(failures);
        }
        remove
        {
            lock (gate)
            {
                listeners -= value;
            }
        }
    }

    public void Emit(params string[] lines)
    {
        foreach (var text in lines)
        {
            List<Exception> failures = [];
            lock (gate)
            {
                if (listeners is { } handler)
                {
                    Deliver(handler, text, failures);
                }
                else
                {
                    early?.Add(text);
                }
            }

            Report(failures);
        }
    }

    public void Exit(int code) => exited.TrySetResult(code);

    public void Kill()
    {
        steps.Add("kill");
        Killed = true;
        exited.TrySetResult(KilledExitCode);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        exited.TrySetResult(KilledExitCode);
        return ValueTask.CompletedTask;
    }

    private static void Deliver(Action<string> handler, string text, List<Exception> failures)
    {
        foreach (var each in handler.GetInvocationList())
        {
            try
            {
                ((Action<string>)each)(text);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }
    }

    private void Report(List<Exception> failures)
    {
        foreach (var failure in failures)
        {
            ListenerFailed?.Invoke(failure);
        }
    }
}

internal sealed class FakeLauncher(Steps steps, string sessionFile) : IProcessLauncher
{
    private readonly List<ProcessSpec> specs = [];

    /// <summary>Lines the process writes at once, before Mirador can listen.</summary>
    public string[] FirstLines { get; set; } = [];

    public MiradorException? Refusal { get; set; }

    public FakeProcess? Last { get; private set; }

    public bool SessionFileExistedAtLaunch { get; private set; }

    public int Starts
    {
        get
        {
            lock (specs)
            {
                return specs.Count;
            }
        }
    }

    public ProcessSpec Spec
    {
        get
        {
            lock (specs)
            {
                return Assert.Single(specs);
            }
        }
    }

    public IRunningProcess Start(ProcessSpec spec)
    {
        steps.Add("launch");
        lock (specs)
        {
            specs.Add(spec);
        }

        if (Refusal is not null)
        {
            throw Refusal;
        }

        SessionFileExistedAtLaunch = File.Exists(sessionFile);
        var process = new FakeProcess(steps);
        process.Emit(FirstLines);
        Last = process;
        return process;
    }
}

/// <summary>
/// Monado's pipe, there once the test says so. Names come from the real <see cref="MonadoPipe"/>, without the trip
/// through the ANSI code page (MonadoPipeTests cover that), so that they don't depend on the machine's code page.
/// </summary>
internal sealed class FakePipe(Steps steps) : IMonadoPipe
{
    private readonly ConcurrentQueue<string> asked = new();

    public volatile bool Up;

    public TaskCompletionSource FirstPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyCollection<string> Asked => asked;

    public string NameFor(IReadOnlyDictionary<string, string> env) => new MonadoPipe(folder => folder).NameFor(env);

    public bool Exists(string name)
    {
        steps.Add("pipe");
        asked.Enqueue(name);
        FirstPoll.TrySetResult();
        return Up;
    }
}

internal sealed class FakeRuntimeReader : IRuntimeRegistryReader
{
    public string? Active { get; set; }

    public string? ActiveRuntime() => Active;
}

internal sealed class FakeHelper : IHelperClient
{
    public Task<HelperReply> SendAsync(HelperCommand command, CancellationToken ct) => Task.FromResult(new HelperReply(true));
}

/// <summary>The USB devices "plugged in": the fake headset unless a test unplugs it.</summary>
internal sealed class FakeUsb : IUsbDevices
{
    public HashSet<UsbId> Devices { get; } = [FakeModule.HeadsetId];

    public IReadOnlySet<UsbId> Present() => new HashSet<UsbId>(Devices);
}

internal sealed class FakeModuleSession(
    Steps steps,
    IReadOnlyDictionary<string, string> recoveryData,
    Func<string, Exception>? lineFailure,
    Exception? endFailure,
    bool endHangs) : ModuleSession
{
    private readonly ConcurrentQueue<string> lines = new();
    private int ends;

    public override IReadOnlyList<EnvVar> Environment { get; } = [new("FAKE_HEADSET", "1")];

    public override IReadOnlyDictionary<string, string> RecoveryData => recoveryData;

    public override IReadOnlyList<StatusLine> Status => [new("fake", "Headset", StatusLevel.Ok, "Ready")];

    public IReadOnlyCollection<string> Lines => lines;

    public int Ends => Volatile.Read(ref ends);

    public TaskCompletionSource HasEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void OnServiceLine(string line)
    {
        lines.Enqueue(line);
        if (lineFailure is not null)
        {
            throw lineFailure(line);
        }
    }

    public override Task EndAsync(CancellationToken ct)
    {
        steps.Add("end");
        Interlocked.Increment(ref ends);
        HasEnded.TrySetResult();
        if (endHangs)
        {
            return new TaskCompletionSource().Task; // never finishes, and ignores ct: the worst case
        }

        return endFailure is null ? Task.CompletedTask : Task.FromException(endFailure);
    }

    public void Warn(SessionWarning warning) => RaiseWarning(warning);
}

internal sealed class FakeModule(Steps steps) : IHeadsetModule
{
    private int beforeStartCalls;

    public string Id => "fake";

    public string DisplayName => "Fake headset";

    public bool UsesSteamVrLighthouse { get; set; }

    public bool UsesNvidiaDirectMode { get; set; }

    public string? ElevatedModuleId { get; set; } = "fake";

    /// <summary>When set, BeforeStart throws it (after waiting at <see cref="Gate"/>).</summary>
    public Exception? Refusal { get; set; }

    /// <summary>When set, BeforeStart waits for it, honouring its token.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, BeforeStart reports it as progress.</summary>
    public string? Progress { get; set; }

    /// <summary>The recovery data of the sessions BeforeStart makes from now on.</summary>
    public IReadOnlyDictionary<string, string> RecoveryData { get; set; } = new Dictionary<string, string> { ["wasRunning"] = "true" };

    /// <summary>When set, BeforeStart saves it as recovery data first thing, before waiting at <see cref="Gate"/>.</summary>
    public IReadOnlyDictionary<string, string>? SavedDuringStart { get; set; }

    /// <summary>When set, BeforeStart saves it as recovery data just before it throws <see cref="Refusal"/>: an empty set says it undid its steps.</summary>
    public IReadOnlyDictionary<string, string>? SavedBeforeRefusal { get; set; }

    /// <summary>The last save-recovery callback BeforeStart was given.</summary>
    public Action<IReadOnlyDictionary<string, string>>? SaveRecovery { get; private set; }

    /// <summary>When set, the session throws what it makes of each line it is given.</summary>
    public Func<string, Exception>? SessionLineFailure { get; set; }

    /// <summary>When set, the session's EndAsync fails with it.</summary>
    public Exception? SessionEndFailure { get; set; }

    /// <summary>When true, the session's EndAsync never finishes.</summary>
    public bool SessionEndHangs { get; set; }

    /// <summary>When set, RecoverAsync throws it.</summary>
    public Exception? RecoverFailure { get; set; }

    /// <summary>When set, RecoverAsync waits for it, honouring its token.</summary>
    public TaskCompletionSource? RecoverGate { get; set; }

    public TaskCompletionSource RecoverEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int BeforeStartCalls => Volatile.Read(ref beforeStartCalls);

    public FakeModuleSession? Session { get; private set; }

    public IReadOnlyDictionary<string, string>? Recovered { get; private set; }

    /// <summary>The fake headset's USB id; <see cref="FakeUsb"/> has it plugged in by default.</summary>
    public static readonly UsbId HeadsetId = new(0x1234, 0x5678);

    public bool IsPresent(IReadOnlySet<UsbId> devices) => devices.Contains(HeadsetId);

    public Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CheckResult>>([]);

    public async Task<ModuleSession> BeforeStartAsync(
        ModuleContext ctx, Profile profile, IProgress<string> progress, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct)
    {
        steps.Add("before-start");
        Interlocked.Increment(ref beforeStartCalls);
        SaveRecovery = saveRecovery;
        if (SavedDuringStart is not null)
        {
            saveRecovery(SavedDuringStart);
        }

        if (Progress is not null)
        {
            progress.Report(Progress);
        }

        Entered.TrySetResult();
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        if (Refusal is not null)
        {
            if (SavedBeforeRefusal is not null)
            {
                saveRecovery(SavedBeforeRefusal);
            }

            throw Refusal;
        }

        return Session = new FakeModuleSession(steps, RecoveryData, SessionLineFailure, SessionEndFailure, SessionEndHangs);
    }

    public async Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        steps.Add("recover");
        RecoverEntered.TrySetResult();
        if (RecoverGate is not null)
        {
            await RecoverGate.Task.WaitAsync(ct);
        }

        if (RecoverFailure is not null)
        {
            throw RecoverFailure;
        }

        Recovered = data;
    }

    public void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink)
    {
    }
}

/// <summary>An <see cref="ISessionHook"/> that writes down what it is told, in order, from any thread.</summary>
internal sealed class FakeHook : ISessionHook
{
    private readonly List<string> calls = [];

    /// <summary>When set, both calls throw it (after writing themselves down).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Run inside <see cref="Started"/>, before it writes itself down: tests look at the session from there.</summary>
    public Action? OnStarted { get; set; }

    /// <summary>Run inside <see cref="Ended"/>, before it writes itself down.</summary>
    public Action? OnEnded { get; set; }

    /// <summary>The calls so far: <c>started:profile-id:module-id</c> or <c>ended:profile-id:module-id</c> (module-id empty without a module).</summary>
    public string[] Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public void Started(Profile profile, IHeadsetModule? module)
    {
        OnStarted?.Invoke();
        Add($"started:{profile.Id}:{module?.Id}");
    }

    public void Ended(Profile profile, IHeadsetModule? module)
    {
        OnEnded?.Invoke();
        Add($"ended:{profile.Id}:{module?.Id}");
    }

    private void Add(string call)
    {
        lock (calls)
        {
            calls.Add(call);
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>
/// A <see cref="SessionController"/> on fakes, with a bundled Monado and a stand-in on disk in a temp folder whose
/// path has a space and a non-ASCII letter. The selected profile is "Dream Air" on the bundled Monado with the fake module.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly TempDir dir = new();

    /// <param name="installedCopy">With a value, the user allowed Monado as administrator, and this says whether this
    /// Mirador is the installed one; the elevated launcher then records its starts in <see cref="ElevatedStarts"/>
    /// and runs them through <see cref="Launcher"/>.</param>
    /// <param name="approvedCopy">The locator's lookup of a build's approved copy; null: no build is approved.</param>
    public Harness(ISessionHook? hook = null, bool? installedCopy = null, Func<Profile, MonadoInstall, Mirador.Ipc.ApprovedBuild?>? approvedCopy = null)
    {
        Root = Path.Combine(dir.Path, "Zoë's data");
        Paths = new AppPaths(Path.Combine(Root, "Mirador"));

        var bundledRoot = Path.Combine(Root, "Mirador App", "monado");
        const string version = "monado-2026.11.10-1a2b3c4d5e";
        var dll = Touch(bundledRoot, version, "bin", "openxr_monado.dll");
        BundledManifest = Path.Combine(bundledRoot, version, "openxr_monado.json");
        File.WriteAllText(BundledManifest, TestManifest.Json(dll));
        BundledServer = Touch(bundledRoot, version, "bin", "monado-service.exe");
        MiradorMonado.Write(bundledRoot, new MiradorMonadoVersions(version, null));

        BundledLayer = Path.Combine(Root, "Mirador App", "layer");
        Touch(BundledLayer, DirectModeLayer.ManifestName);
        Touch(BundledLayer, DirectModeLayer.DllName);
        Touch(BundledLayer, DirectModeLayer.CheckExeName);

        StandInExe = Path.Combine(Root, "Mirador App", "standin", "monado-service.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(StandInExe)!);
        File.WriteAllBytes(StandInExe, [0x4D, 0x5A, 1, 2, 3]);

        Timers = new TimerWatch(Time);
        Processes = new FakeProcessList(Steps);
        Module = new FakeModule(Steps);
        Launcher = new FakeLauncher(Steps, Paths.SessionFile);
        Pipe = new FakePipe(Steps);
        Runtime = new FakeRuntimeReader { Active = BundledManifest };
        Profiles = new ProfileStore(Paths.ProfilesFile);
        Settings = new MonadoSettingsStore(Paths.MonadoSettingsFile);
        Usb = new FakeUsb();
        UseProfile(DreamAir);

        Controller = new SessionController(
            new SessionDeps(
                Profiles,
                Settings,
                new Dictionary<string, IHeadsetModule> { [Module.Id] = Module },
                new ModuleContext(Paths, new FakeHelper(), Processes, Usb, Timers),
                new MonadoLocator(bundledRoot, approvedCopy: approvedCopy),
                Launcher,
                Pipe,
                new OpenXrRuntime(Runtime),
                StandInExe,
                Inherited,
                hook,
                ElevatedConsented: installedCopy is null ? null : () => true,
                ElevatedLauncher: installedCopy is null ? null : start =>
                {
                    ElevatedStarts.Enqueue(start);
                    return Launcher;
                },
                ElevatedInstalledCopy: installedCopy is { } installed ? () => installed : null,
                BundledLayerDir: BundledLayer),
            isStandIn: path => path.StartsWith(StandInImages, StringComparison.OrdinalIgnoreCase));

        Controller.Changed += () => Changes.Enqueue(Controller.State);
        Controller.Warning += w => Warnings.Enqueue(w);
        Controller.LogLine += l => LogLines.Enqueue(l);
        Controller.Ended += m =>
        {
            EndedMessages.Enqueue(m);
            HasEnded.TrySetResult(m);
        };
    }

    public static Profile DreamAir { get; } =
        new("p1", "Dream Air", "fake", BuildSource.Mirador, [], new Dictionary<string, string>());

    /// <summary>Program files under this folder count as Mirador's stand-in, nothing else does.</summary>
    public const string StandInImages = @"C:\Games\stand-ins\";

    /// <summary>A real Monado's program file, outside Mirador.</summary>
    public const string OtherMonado = @"C:\Users\Zoë\monado\build\src\xrt\targets\service\monado-service.exe";

    public static IReadOnlyDictionary<string, string> Inherited { get; } = new Dictionary<string, string>
    {
        ["USERPROFILE"] = @"C:\Users\Zoë",
        ["TEMP"] = @"C:\Users\Zoë\AppData\Local\Temp",
    };

    public string Root { get; }

    public AppPaths Paths { get; }

    public string BundledManifest { get; }

    public string BundledServer { get; }

    /// <summary>The bundled Monado's runtime DLL folder, with the stand-in's <c>service</c> folder below it.</summary>
    public string RuntimeDir => Path.GetDirectoryName(BundledServer)!;

    public string StandInExe { get; }

    /// <summary>The direct-mode layer that "comes with Mirador" here.</summary>
    public string BundledLayer { get; }

    public Steps Steps { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    /// <summary>The clock the controller is given: <see cref="Time"/>, saying when a timer is set on it.</summary>
    public TimerWatch Timers { get; }

    public FakeProcessList Processes { get; }

    public FakeModule Module { get; }

    public FakeLauncher Launcher { get; }

    /// <summary>The Starts that went to the elevated launcher (with <c>installedCopy</c> given).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<ElevatedStart> ElevatedStarts { get; } = new();

    /// <summary>What is plugged in: the fake headset, until a test clears it.</summary>
    public FakeUsb Usb { get; }

    public FakePipe Pipe { get; }

    public FakeRuntimeReader Runtime { get; }

    public ProfileStore Profiles { get; }

    public MonadoSettingsStore Settings { get; }

    public SessionController Controller { get; }

    public ConcurrentQueue<SessionState> Changes { get; } = new();

    public ConcurrentQueue<SessionWarning> Warnings { get; } = new();

    public ConcurrentQueue<string> LogLines { get; } = new();

    public ConcurrentQueue<string> EndedMessages { get; } = new();

    public TaskCompletionSource<string> HasEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The log file a Start at the fake clock's time writes.</summary>
    public string MonadoLog => Paths.NewMonadoLogFile(Time.GetLocalNow().DateTime);

    public void UseProfile(Profile p) => Profiles.Save(new ProfileSet([p], p.Id));

    /// <summary>Creates an empty file (and its folders) and returns its path.</summary>
    public static string Touch(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>A Monado build folder as a dev build leaves it; returns the folder of its runtime DLL.</summary>
    public static string MakeBuild(string folder)
    {
        var dll = Touch(folder, "src", "xrt", "targets", "openxr", "openxr_monado.dll");
        File.WriteAllText(Path.Combine(folder, "openxr_monado-dev.json"), TestManifest.Json(dll));
        Touch(folder, "src", "xrt", "targets", "service", "monado-service.exe");
        return Path.GetDirectoryName(dll)!;
    }

    /// <summary>Starts with the pipe there at the first look.</summary>
    public async Task<StartOutcome> StartRunningAsync()
    {
        Pipe.Up = true;
        var outcome = await Controller.StartAsync(StartReason.Button, CancellationToken.None).WaitAsync(Limit);
        Assert.True(outcome.Ok, outcome.Message);
        return outcome;
    }

    public async ValueTask DisposeAsync()
    {
        await Controller.StopAsync(CancellationToken.None).WaitAsync(Limit);
        dir.Dispose();
    }
}
