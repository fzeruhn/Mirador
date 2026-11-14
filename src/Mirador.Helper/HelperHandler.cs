using System.Reflection;
using System.Security.Principal;
using Mirador.Helper.ElevatedMonado;
using Mirador.Ipc;

// The helper runs as LocalSystem beside the app's files: every DLL it loads by name is Windows' own, from System32 only.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace Mirador.Helper;

/// <summary>Services on this machine: just what the helper needs to know and do with Pimax's.</summary>
public interface IServiceControl
{
    /// <summary>Whether a service of this name is installed.</summary>
    bool Exists(string name);

    /// <summary>Whether the service is running or hasn't finished stopping, that is, anything but stopped.</summary>
    bool IsRunning(string name);

    /// <summary>Stops the service and waits until it has stopped; throws a plain message when it doesn't in time.</summary>
    void Stop(string name, TimeSpan timeout);

    /// <summary>
    /// Makes sure the service runs: starts it when it is stopped, and first waits (at most <paramref name="timeout"/>)
    /// for one that is still stopping; one that runs or is starting already is left as it is. Throws a plain message
    /// when it can't.
    /// </summary>
    void Start(string name, TimeSpan timeout);

    /// <summary>The service's <c>ImagePath</c> (variables expanded), or null when it isn't installed.</summary>
    string? ImagePath(string name);

    /// <summary>The running service's process id and start time; null when it doesn't run.</summary>
    (int Id, DateTime Started)? Process(string name);
}

/// <summary>A running program's file, command line, parent and start time.</summary>
/// <param name="ExecutablePath">Its program file; null when Windows doesn't say.</param>
/// <param name="ParentId">The process that started it; null when Windows doesn't say.</param>
/// <param name="Started">When it started; null when Windows doesn't say.</param>
public sealed record ProcessCommandLine(string? ExecutablePath, string CommandLine, int? ParentId = null, DateTime? Started = null);

/// <summary>Processes on this machine, named without <c>.exe</c>.</summary>
public interface IProcessControl
{
    /// <summary>Ends every process of each name. A process that is already gone is fine.</summary>
    void KillAll(IEnumerable<string> names);

    /// <summary>The program files and command lines of the running processes of this name; none when it isn't running.</summary>
    IReadOnlyList<ProcessCommandLine> CommandLines(string processName);
}

/// <summary>The two registry values the helper changes, in the 64-bit view of <c>HKEY_LOCAL_MACHINE</c>.</summary>
public interface IRuntimeRegistry
{
    /// <summary>The OpenXR <c>ActiveRuntime</c> manifest path, or null when there is none.</summary>
    string? GetActive();

    /// <summary>Makes <paramref name="path"/> the OpenXR active runtime; null leaves no runtime set.</summary>
    void SetActive(string? path);

    /// <summary>The runtime Mirador replaced (<c>PreviousActiveRuntime</c>), or null when nothing is recorded.</summary>
    string? GetRecorded();

    /// <summary>Records the replaced runtime; null forgets it.</summary>
    void SetRecorded(string? path);

    /// <summary>Lists <paramref name="manifest"/> under OpenXR's <c>AvailableRuntimes</c>, or takes it off.</summary>
    void SetAvailable(string manifest, bool available);
}

/// <summary>Who is on the other end of the pipe, as Windows reports it; nothing in it comes from a request.</summary>
/// <param name="Sid">The user's SID, from the impersonated client's token.</param>
/// <param name="SessionId">The caller's Windows session.</param>
/// <param name="ProcessId">The caller's process.</param>
/// <param name="ImagePath">The caller's program file; null when Windows doesn't say.</param>
public sealed record CallerInfo(SecurityIdentifier Sid, int SessionId, int ProcessId, string? ImagePath);

/// <summary>
/// Does what <see cref="HelperCommand"/> says, and nothing else. A request carries only the command, so nothing a
/// caller sends reaches the registry, a service or a process. Requests are handled one at a time: they change
/// machine-wide state.
/// </summary>
public sealed class HelperHandler
{
    private const string BundledMissing = "The bundled Monado isn't installed yet. Install it on the Profiles page.";
    private const string NoMiradorsMonado = "This helper can't install the bundled Monado.";
    private const string NotApproved = "This profile's build isn't approved. Approve it on the Profiles page.";
    private const string NoCaller = "Mirador's helper service couldn't tell who asked, so it changed nothing.";
    private const string NotInstalledMirador = "Only the installed Mirador may change which version of the bundled Monado is current.";
    private const string NoConsent = "Mirador's helper service has no way to reach the consent for Monado as administrator.";
    private const string NoElevated = "Mirador's helper service can't start Monado as administrator.";
    private const string NoStart = "Mirador's helper service was asked to start Monado as administrator without any settings.";
    private const string UnknownCommand = "Mirador's helper service doesn't know that command.";
    // A message goes into one protocol line, where JSON writes each character outside ASCII as six bytes (\uXXXX).
    private const int MaxErrorLength = 600;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    private readonly IServiceControl services;
    private readonly IProcessControl processes;
    private readonly IRuntimeRegistry registry;
    private readonly ElevatedConsent? consent;
    private readonly IElevatedMonado? elevated;
    private readonly string version;
    private readonly string installDir;
    private readonly IMonadoInstaller? monado;
    private readonly IApprovedBuildStore? approved;
    private readonly object gate = new();

    /// <param name="version">What <see cref="HelperCommand.Ping"/> answers; the helper's own version when null.</param>
    /// <param name="consent">Where users' consent to run Monado as administrator is; without it, revoking fails with a message.</param>
    /// <param name="elevated">Starts and stops the elevated Monado; without it, the elevated commands fail with a message.</param>
    /// <param name="installDir">What <see cref="HelperCommand.GetInstallFolder"/> answers; <see cref="DefaultInstallDir"/> when null.</param>
    /// <param name="monado">The bundled Monado; without it, installing and setting it as the runtime fail with a message.</param>
    public HelperHandler(
        IServiceControl services, IProcessControl processes, IRuntimeRegistry registry,
        string? version = null, ElevatedConsent? consent = null, IElevatedMonado? elevated = null, string? installDir = null,
        IMonadoInstaller? monado = null, IApprovedBuildStore? approved = null)
    {
        this.monado = monado;
        this.approved = approved;
        this.services = services;
        this.processes = processes;
        this.registry = registry;
        this.consent = consent;
        this.elevated = elevated;
        this.version = version ?? OwnVersion();
        this.installDir = installDir ?? DefaultInstallDir;
    }

    /// <summary>
    /// The helper's install folder, its own: the installed <c>Mirador.exe</c> is beside it (they share one .NET runtime),
    /// the only caller the elevated Monado is started for (<see cref="ElevatedMonado.ElevatedRequestCheck"/>).
    /// </summary>
    public static string DefaultInstallDir { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    /// <summary>
    /// Carries out one request. Any failure comes back as an error reply, never as an exception.
    /// </summary>
    /// <param name="request">What the caller sent.</param>
    /// <param name="caller">Who the caller is, from the pipe (never from the request); null when that couldn't be learned.</param>
    public Task<HelperReply> HandleAsync(HelperRequest request, CallerInfo? caller = null) =>
        Task.Run(() => Handle(request, caller));

    /// <summary>
    /// For the uninstaller only (<c>--restore-runtime</c>), never over the pipe: puts back the OpenXR runtime that
    /// <see cref="HelperCommand.SetRuntimeBundled"/> replaced, as long as Mirador's runtime is still the active one; if
    /// another program has set its own since, that stays and the record is just dropped. Any failure comes back as an
    /// error reply.
    /// </summary>
    public Task<HelperReply> RestoreRuntimeAsync() => Task.Run(() => Locked(RestoreRuntime));

    private HelperReply Handle(HelperRequest request, CallerInfo? caller)
    {
        var command = request.Command;

        // Ping and Wait touch no machine state, so they don't wait behind a request that is (perhaps for a long time)
        // changing it: Wait is polled every half-second while a session runs and must answer at once.
        if (command == HelperCommand.Ping)
        {
            return new HelperReply(true, Value: version);
        }

        if (command == HelperCommand.GetInstallFolder)
        {
            return new HelperReply(true, Value: installDir);
        }

        if (command == HelperCommand.WaitMonadoElevated)
        {
            return WaitMonadoElevated(caller);
        }

        // A download takes minutes: it doesn't hold up the other commands (the installer takes its own lock).
        if (command == HelperCommand.InstallMonado)
        {
            if (monado is null)
            {
                return new HelperReply(false, NoMiradorsMonado);
            }

            // Only a caller Windows names, and only the installed Mirador: it changes the Monado every user's elevated
            // start and runtime use, so another program of any user's can't pick (an older) one.
            if (caller is null)
            {
                return new HelperReply(false, NoCaller);
            }

            if (!InstalledMirador.IsInstalledCopy(caller.ImagePath, installDir))
            {
                return new HelperReply(false, NotInstalledMirador);
            }

            try
            {
                return monado.InstallAsync(request.Version ?? "", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                return new HelperReply(false, Shorten($"The bundled Monado couldn't be installed ({e.Message.TrimEnd('.')})."));
            }
        }

        return Locked(() => command switch
        {
            HelperCommand.SetRuntimeBundled => SetRuntimeBundled(),
            HelperCommand.StopPimax => StopPimax(),
            HelperCommand.StartPimax => StartPimax(),
            HelperCommand.GetPimaxEyeBlob => GetPimaxEyeBlob(),
            HelperCommand.RevokeElevatedMonado => RevokeElevatedMonado(caller),
            HelperCommand.StartMonadoElevated => StartMonadoElevated(request.Start, caller),
            HelperCommand.StopMonadoElevated => StopMonadoElevated(caller),
            HelperCommand.RollBackMonado =>
                caller is null ? new HelperReply(false, NoCaller)
                : !InstalledMirador.IsInstalledCopy(caller.ImagePath, installDir) ? new HelperReply(false, NotInstalledMirador)
                : monado?.RollBack() ?? new HelperReply(false, NoMiradorsMonado),
            HelperCommand.SetRuntimeApproved => SetRuntimeApproved(request.Build, caller),
            HelperCommand.ListApprovedBuilds => ListApprovedBuilds(caller),
            HelperCommand.ForgetApprovedBuild => ForgetApprovedBuild(request.Build, caller),
            _ => new HelperReply(false, UnknownCommand),
        });
    }

    /// <summary>Starts the elevated Monado for the caller (never from a path the request names; the runner does the checks).</summary>
    private HelperReply StartMonadoElevated(ElevatedStart? start, CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        if (elevated is null)
        {
            return new HelperReply(false, NoElevated);
        }

        return start is null ? new HelperReply(false, NoStart) : elevated.Start(start, caller);
    }

    /// <summary>Ends the elevated Monado this caller started.</summary>
    private HelperReply StopMonadoElevated(CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        return elevated is null ? new HelperReply(false, NoElevated) : elevated.Stop(caller);
    }

    /// <summary>Whether this caller's elevated Monado has ended, and its exit code; outside the lock, answered at once.</summary>
    private HelperReply WaitMonadoElevated(CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        return elevated is null ? new HelperReply(false, NoElevated) : elevated.Wait(caller);
    }

    /// <summary>Runs one step that changes the machine, one at a time; an exception becomes an error reply.</summary>
    private HelperReply Locked(Func<HelperReply> step)
    {
        lock (gate)
        {
            try
            {
                return step();
            }
            catch (Exception e)
            {
                return new HelperReply(false, Shorten(e.Message));
            }
        }
    }

    /// <summary>Deletes the consent of the user on the other end of the pipe, with the SteamVR files they allowed, and no one else's.</summary>
    private HelperReply RevokeElevatedMonado(CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        if (consent is null)
        {
            return new HelperReply(false, NoConsent);
        }

        // Approvals stay: they also make a build the OpenXR runtime. The elevated runner checks the consent at a Start.
        consent.Revoke(caller.Sid);
        return new HelperReply(true);
    }

    /// <summary>The caller's approved profiles, comma-separated (<see cref="HelperCommand.ListApprovedBuilds"/>).</summary>
    private HelperReply ListApprovedBuilds(CallerInfo? caller) =>
        caller is null ? new HelperReply(false, NoCaller)
        : new HelperReply(true, Value: string.Join(',', approved?.ProfilesOf(caller.Sid.Value) ?? []));

    /// <summary>Takes back the caller's approval of one profile's build (<see cref="HelperCommand.ForgetApprovedBuild"/>).</summary>
    private HelperReply ForgetApprovedBuild(string? profileId, CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        if (!ApprovedBuilds.IsProfileId(profileId))
        {
            return new HelperReply(false, NotApproved);
        }

        Forget(caller, profileId!);
        return new HelperReply(true);
    }

    private void Forget(CallerInfo caller, string profileId)
    {
        if (approved is null)
        {
            return;
        }

        // The machine's runtime never points at a copy that is going (or that stays, unrecorded, while a program holds
        // it): the bundled Monado takes over when there is one, else the runtime Mirador replaced if it is still there,
        // else none.
        var owner = caller.Sid.Value;
        if (approved.Get(owner, profileId) is { } build && registry.GetActive() is { } active
            && string.Equals(Path.GetFullPath(active), Path.GetFullPath(build.Manifest), StringComparison.OrdinalIgnoreCase))
        {
            if (monado?.CurrentManifest() is { } miradors && File.Exists(miradors))
            {
                registry.SetActive(miradors);
            }
            else
            {
                registry.SetActive(registry.GetRecorded() is { } recorded && File.Exists(recorded) ? recorded : null);
                registry.SetRecorded(null);
            }
        }

        ApprovedBuilds.Forget(owner, profileId, installDir, approved);
    }

    /// <summary>The bundled Monado's current version.</summary>
    private HelperReply SetRuntimeBundled()
    {
        if (monado?.CurrentManifest() is not { } manifest || !File.Exists(manifest))
        {
            return new HelperReply(false, BundledMissing);
        }

        var active = registry.GetActive();
        if (active is not null && !IsBundled(active))
        {
            registry.SetRecorded(active);
        }

        registry.SetActive(manifest);
        return new HelperReply(true);
    }

    /// <summary>
    /// The approved copy of a profile's build as the runtime (Mirador.md §4.11 item 51): only once every file matches its
    /// recorded hash, so the machine's runtime never points at anything but what was approved.
    /// </summary>
    private HelperReply SetRuntimeApproved(string? profileId, CallerInfo? caller)
    {
        if (caller is null)
        {
            return new HelperReply(false, NoCaller);
        }

        // The caller's own approval only (the record is per user). No consent needed: the approval itself took an
        // administrator prompt, and the runtime runs inside each OpenXR program, never as administrator by Mirador.
        if (approved is null || !ApprovedBuilds.IsProfileId(profileId) || approved.Get(caller.Sid.Value, profileId!) is not { } build)
        {
            return new HelperReply(false, NotApproved);
        }

        if (ApprovedBuilds.Verify(build, installDir) is { } problem)
        {
            return new HelperReply(false, problem);
        }

        var active = registry.GetActive();
        if (active is not null && !IsBundled(active))
        {
            registry.SetRecorded(active);
        }

        registry.SetActive(build.Manifest);
        return new HelperReply(true);
    }

    /// <summary>
    /// Puts the recorded runtime back, but only while the bundled one is still the active one: if another program has
    /// made its own runtime active since, that choice stays, and the record is just forgotten. It must never overwrite
    /// a runtime Mirador didn't set.
    /// </summary>
    private HelperReply RestoreRuntime()
    {
        var recorded = registry.GetRecorded();
        if (recorded is null)
        {
            return new HelperReply(true);
        }

        var active = registry.GetActive();
        if (active is not null && IsBundled(active))
        {
            registry.SetActive(recorded);
        }

        registry.SetRecorded(null);
        return new HelperReply(true);
    }

    /// <summary>
    /// Stops Pimax's service, then ends its processes, whether or not the service stopped. A failure of either step is
    /// in the reply (the first one if both fail) together with <c>WasRunning</c>.
    /// </summary>
    private HelperReply StopPimax()
    {
        var wasRunning = services.Exists(PimaxNames.Service) && services.IsRunning(PimaxNames.Service);
        string? error = null;
        if (wasRunning)
        {
            try
            {
                services.Stop(PimaxNames.Service, StopTimeout);
            }
            catch (Exception e)
            {
                error = Shorten(e.Message);
            }
        }

        try
        {
            processes.KillAll(PimaxNames.Processes);
        }
        catch (Exception e)
        {
            error ??= Shorten(e.Message);
        }

        return new HelperReply(error is null, error, wasRunning);
    }

    /// <summary>
    /// Starts Pimax's service unless it runs. One that is still stopping (a stop that timed out) is waited for, then
    /// started: left alone, it would finish stopping and stay stopped.
    /// </summary>
    private HelperReply StartPimax()
    {
        if (services.Exists(PimaxNames.Service))
        {
            services.Start(PimaxNames.Service, StopTimeout);
        }

        return new HelperReply(true);
    }

    /// <summary>
    /// The variant Pimax's eye runtime was started with. Any user can start a program named like it, even Pimax's own
    /// file with a made-up command line, and the variant decides what Mirador sends to the eye tracker: so only a
    /// runtime that Pimax's service (a SYSTEM process no user can pose as) started, after the service itself started,
    /// from its place in Pimax's folder counts, and runtimes that disagree count as none.
    /// </summary>
    private HelperReply GetPimaxEyeBlob()
    {
        if (PimaxFolder() is not { } folder || services.Process(PimaxNames.Service) is not { } service)
        {
            return new HelperReply(true);
        }

        var expected = Path.Combine(folder, "EyeTrackingServer", "platform_runtime", PimaxNames.EyeRuntimeProcess + ".exe");
        var variants = processes.CommandLines(PimaxNames.EyeRuntimeProcess)
            .Where(p => p.ParentId == service.Id && p.Started >= service.Started && SameFile(p.ExecutablePath, expected))
            .Select(p => PimaxEyeBlob.Parse(p.CommandLine))
            .OfType<string>()
            .Distinct()
            .ToList();
        return variants.Count == 1 ? new HelperReply(true, Value: variants[0]) : new HelperReply(true);
    }

    /// <summary>Pimax's runtime folder, with a separator at the end: the folder its service's program is in.</summary>
    private string? PimaxFolder()
    {
        var program = ServiceImage.ProgramOf(services.ImagePath(PimaxNames.Service));
        try
        {
            var folder = program is null ? null : Path.GetDirectoryName(program);
            return folder is null || !Path.IsPathFullyQualified(folder)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SameFile(string? file, string expected)
    {
        try
        {
            return file is not null && Path.IsPathFullyQualified(file)
                && string.Equals(Path.GetFullPath(file), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="manifest"/> is Mirador's: a version of the bundled Monado, or an approved copy.
    /// </summary>
    private bool IsBundled(string manifest) =>
        monado?.IsMiradors(manifest) == true
        || Path.GetFullPath(manifest).StartsWith(ApprovedBuilds.Root(installDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps a reply within one protocol line.</summary>
    private static string Shorten(string message) =>
        message.Length <= MaxErrorLength ? message : message[..(MaxErrorLength - 3)] + "...";

    /// <summary>The helper's version as numbers only, for GitHub's User-Agent.</summary>
    public static string OwnVersionNumber() => OwnVersion().Split('+')[0];

    private static string OwnVersion() =>
        typeof(HelperHandler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
