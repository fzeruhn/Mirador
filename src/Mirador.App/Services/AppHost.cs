using System.Security.Principal;
using Microsoft.Win32;
using Mirador.Core;
using Mirador.Core.BaseStations;
using Mirador.Core.Build;
using Mirador.Core.Elevation;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.OpenVr;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Core.Session;
using Mirador.DreamAir;
using Mirador.Ipc;

namespace Mirador.App.Services;

/// <summary>
/// Everything Mirador is made of, put together once at start: the stores, the headset modules, the helper, the base
/// stations, the Monado session and the app pipe. Paths into the install follow its layout (<c>{app}\monado</c>,
/// <c>{app}\layer</c>, …).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    /// <summary>How long quitting waits for the base stations' off.</summary>
    private static readonly TimeSpan StationsLimit = TimeSpan.FromSeconds(15);

    private readonly JobProcessLauncher launcher;
    private readonly List<string> notices = [];
    private readonly WinRtStationRadio radio;
    private readonly StationStore stationStore;
    private AppPipeServer? pipe;

    // Set when the helper refused the last elevated Start because SteamVR's unsigned Lighthouse DLLs aren't the copies
    // the user allowed (that Start ran as the user); cleared by an elevated Start and by allowing again.
    private volatile bool steamVrChanged;

    // The folder the running helper starts Monado as administrator for (its answer to GetInstallFolder); null until it
    // answered. Asked at start and again at each elevated Start; Home only reads it.
    private volatile string? helperInstallDir;

    private AppHost(string appDir, AppPaths paths)
    {
        AppDir = appDir;
        Paths = paths;
        Profiles = new ProfileStore(paths.ProfilesFile);
        MonadoSettings = new MonadoSettingsStore(paths.MonadoSettingsFile);
        Settings = new AppSettingsStore(paths.SettingsFile);
        Helper = new HelperClient();
        Runtime = new OpenXrRuntime(new RegistryRuntimeReader());
        Locator = new MonadoLocator(Path.Combine(appDir, "monado"), paths.BuildsDir);
        Builds = new Builds(new MonadoBuilder(
            new CommandRunner(), Locator, BuildTools.For(appDir, paths),
            new BuildChecks(new CommandRunner(), Environment.SystemDirectory, TimeProvider.System), TimeProvider.System));
        Usb = new WmiUsbDevices();
        Processes = new SystemProcessList();
        Context = new ModuleContext(paths, Helper, Processes, Usb, TimeProvider.System);
        DreamAirState = new DreamAirStateStore(paths.DreamAirFile);
        Calibrations = new CalibrationStore(
            CalibrationStore.DefaultMonadoDir, paths.BackupsDir, TimeProvider.System, () => Session!.State != SessionState.Stopped);

        var dreamAir = new DreamAirModule(new DreamAirDeps(
            new WindowsRegistryReader(),
            ConsoleProcess.Start,
            new GeometryUploader(ConsoleProcess.Start, Usb, Processes, paths, TimeProvider.System),
            DreamAirState,
            Path.Combine(appDir, "layer"),
            NvidiaDriver.CurrentWindowsVersion,
            DreamAirChecks.FindSteamVr,
            state => BlobChoice.Resolve(state, null).Variant,
            Calibrations));
        Modules = new Dictionary<string, IHeadsetModule> { [dreamAir.Id] = dreamAir };
        CalibrationCheck = new CalibrationCheck(ConsoleProcess.Start, DreamAirState, TimeProvider.System);
        OpenCompositeCopy = new OpenCompositeCopy(Path.Combine(appDir, "opencomposite"), paths.OpenCompositeDir);
        OpenVr = new OpenVrGames(new OpenVrPaths(OpenVrPaths.DefaultFile, paths.OpenCompositeDir), OpenCompositeCopy);

        radio = new WinRtStationRadio(text => AppLog.Write(text));
        stationStore = new StationStore(paths.BaseStationsFile);
        Stations = new StationManager(radio, stationStore, StationSettings, TimeProvider.System, text => AppLog.Write(text));

        launcher = new JobProcessLauncher();
        ElevatedConsent = new ElevatedConsent(new RegistryConsentValues());
        var elevatedLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mirador", "monado", CurrentSid() + ".log");
        Session = new SessionController(new SessionDeps(
            Profiles,
            MonadoSettings,
            Modules,
            Context,
            Locator,
            launcher,
            new MonadoPipe(),
            Runtime,
            StandInExe,
            UserEnvironment.Default(e => AppLog.Write("Windows didn't give the user's environment; Monado gets Mirador's own", e)),
            Stations,
            ElevatedConsented: ElevatedMonadoAllowed,
            ElevatedLauncher: start => new HelperProcessLauncher(Helper, start, elevatedLogPath, launcher)
            {
                SteamVrChecked = OnSteamVrChecked,
            },
            ElevatedInstalledCopy: () => IsInstalledCopy(askHelper: true)));
    }

    /// <summary>Home's line when an elevated Start ran as the user because SteamVR changed (Mirador.md §4.10 item 46).</summary>
    public const string SteamVrChangedReason =
        "Runs as you: SteamVR's Lighthouse driver changed since you allowed Monado as administrator.";

    /// <summary>The folder Mirador.exe is in.</summary>
    public string AppDir { get; }

    public AppPaths Paths { get; }

    public ProfileStore Profiles { get; }

    public MonadoSettingsStore MonadoSettings { get; }

    /// <summary>Mirador's own settings: close to the tray, the update check.</summary>
    public AppSettingsStore Settings { get; }

    public IHelperClient Helper { get; }

    public OpenXrRuntime Runtime { get; }

    public MonadoLocator Locator { get; }

    /// <summary>The branch builds (Mirador_Plan.md Task 61).</summary>
    public Builds Builds { get; }

    public IUsbDevices Usb { get; }

    public IProcessList Processes { get; }

    public ModuleContext Context { get; }

    public DreamAirStateStore DreamAirState { get; }

    /// <summary>The eye calibrations Monado uses, and Mirador's copies of them.</summary>
    public CalibrationStore Calibrations { get; }

    /// <summary>The Dream Air's gaze check ("Check my calibration").</summary>
    public CalibrationCheck CalibrationCheck { get; }

    /// <summary><c>gaze_world</c>, which shows the check's dots (<c>{app}\tools\gaze_world.exe</c>).</summary>
    public string GazeWorldExe => Path.Combine(AppDir, "tools", "gaze_world.exe");

    /// <summary>Raised on the UI thread after the profiles or the selected one changed (Home, the Profiles page).</summary>
    public event Action? ProfilesChanged;

    /// <summary>Raised on the UI thread a second after devices were plugged in or out.</summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// Says that devices were plugged in or out; call on the UI thread. With no Bluetooth found so far, Mirador looks
    /// for it again: an adapter may just have been plugged in or its driver installed.
    /// </summary>
    public void NotifyDevicesChanged()
    {
        DevicesChanged?.Invoke();
        if (radio.State == BluetoothState.Missing)
        {
            _ = Task.Run(StartRadioAsync);
        }
    }

    /// <summary>Says that the profiles changed, and puts the stand-in below the selected build; call on the UI thread.</summary>
    public void NotifyProfilesChanged()
    {
        ProfilesChanged?.Invoke();
        _ = Task.Run(Session.PlaceStandIn);
    }

    /// <summary>Whether the selected profile is a Dream Air's; false when the profiles can't be read.</summary>
    public bool SelectedIsDreamAir()
    {
        try
        {
            return Profiles.Load().Selected.ModuleId == DreamAirModule.ModuleId;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyDictionary<string, IHeadsetModule> Modules { get; }

    public SessionController Session { get; }

    /// <summary>Which users allowed Monado to run as administrator (Mirador.md §4.10 item 44).</summary>
    public ElevatedConsent ElevatedConsent { get; }

    /// <summary>Whether the signed-in user allowed Monado to run as administrator; false when the registry can't be read.</summary>
    public bool ElevatedMonadoAllowed()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User is { } sid && ElevatedConsent.IsAllowed(sid);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the last elevated Start ran as the user because SteamVR's Lighthouse driver changed since the user allowed
    /// Monado as administrator; Home then offers "Allow again".
    /// </summary>
    public bool SteamVrChangedSinceAllowed => steamVrChanged;

    /// <summary>
    /// Says that the consent step just recorded the user's consent again, with SteamVR's files as they are now: the next
    /// Start runs as administrator, and Home's line goes.
    /// </summary>
    public void SteamVrAllowedAgain() => steamVrChanged = false;

    /// <summary>
    /// Whether this Mirador is the installed one, the only caller the helper starts Monado as administrator for: its
    /// path against the running helper's install folder (<see cref="HelperCommand.GetInstallFolder"/>), through the
    /// helper's own test (<see cref="InstalledMirador.IsInstalledCopy"/>). Until the helper has answered (it isn't
    /// running, or is older), the folder <c>HKLM\SOFTWARE\Mirador</c> <c>AppPath</c> names stands in. Never throws.
    /// </summary>
    /// <param name="askHelper">Ask the helper now (blocks for the pipe; not on the UI thread); false: the last answer.</param>
    public bool IsInstalledCopy(bool askHelper)
    {
        if (askHelper)
        {
            helperInstallDir = AskHelperInstallDir();
        }

        return InstalledMirador.IsInstalledCopy(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"),
            helperInstallDir ?? RegisteredInstallDir());
    }

    private string? AskHelperInstallDir()
    {
        try
        {
            var reply = Helper.SendAsync(HelperCommand.GetInstallFolder, CancellationToken.None).GetAwaiter().GetResult();
            return reply.Ok && !string.IsNullOrWhiteSpace(reply.Value) ? reply.Value : null;
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't ask the helper where it is installed", e);
            return null;
        }
    }

    /// <summary>The folder of <c>HKLM\SOFTWARE\Mirador</c> <c>AppPath</c> (the installer writes it), or null.</summary>
    private static string? RegisteredInstallDir()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SOFTWARE\Mirador");
            return key?.GetValue("AppPath") is string path && path.Length > 0 ? Path.GetDirectoryName(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private void OnSteamVrChecked(bool changed, string? reason)
    {
        steamVrChanged = changed;
        if (changed)
        {
            // The helper names the files; the user sees only Home's line, so they are kept here.
            AppLog.Write($"Monado runs as you this time: {reason ?? "SteamVR's Lighthouse driver changed since you allowed Monado as administrator."}");
        }
    }

    /// <summary>
    /// Home's Monado card line while consent is on and the selected profile still runs as the user: because this copy
    /// of Mirador isn't the installed one, the profile uses a build folder (or a module folder such as the Dream Air
    /// layer) or sets extra environment variables (<see cref="ElevatedChoice.RunsAsYouReason"/>, the session's own
    /// test), or because SteamVR's Lighthouse driver changed since the user allowed it (<see cref="SteamVrChangedReason"/>).
    /// Null when the profile would run as administrator, or consent is off (Mirador.md §4.10 items 45 and 46).
    /// </summary>
    public string? RunsAsYouReason()
    {
        if (!ElevatedMonadoAllowed())
        {
            return null;
        }

        try
        {
            var profile = Profiles.Load().Selected;
            var module = profile.ModuleId is { } id ? Modules.GetValueOrDefault(id) : null;
            return ElevatedChoice.RunsAsYouReason(profile, module, IsInstalledCopy(askHelper: false))
                ?? (steamVrChanged ? SteamVrChangedReason : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CurrentSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "unknown";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "unknown";
        }
    }

    /// <summary>The SteamVR base stations: the Lighthouse page's list, Home's On and Off, and the session's own.</summary>
    public StationManager Stations { get; }

    /// <summary>Mirador's own stand-in, copied into Monado build folders (<c>{app}\standin\monado-service.exe</c>).</summary>
    public string StandInExe => Path.Combine(AppDir, "standin", "monado-service.exe");

    /// <summary>Whether closing the window keeps Mirador in the tray (the default, and when settings.json can't be read).</summary>
    public bool CloseToTray()
    {
        try
        {
            return Settings.Load().CloseToTray;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>What went wrong while Mirador started that the user should read on Home: settings reset, files it couldn't read.</summary>
    public IReadOnlyList<string> StartupNotices => notices;

    /// <summary>Home's "OpenVR runtime" row and its "Set OpenComposite" (Mirador.md §4.6).</summary>
    public OpenVrGames OpenVr { get; }

    /// <summary>Mirador's OpenComposite: the bundle and the per-user copy.</summary>
    public OpenCompositeCopy OpenCompositeCopy { get; }

    /// <summary>Why OpenVR games couldn't be put on Monado, or the copy brought up to date, at launch; null when all is well.</summary>
    public string? OpenCompositeNotice { get; private set; }

    /// <summary>
    /// Puts Mirador together and reads its settings once, so that a file it had to set aside, or couldn't read, is
    /// known from the start.
    /// </summary>
    /// <exception cref="MiradorException">A part Mirador can't run without couldn't be set up.</exception>
    public static AppHost Create(string appDir, AppPaths paths)
    {
        var host = new AppHost(appDir, paths);
        host.CheckStore("profiles", paths.ProfilesFile, () => host.Profiles.Load());
        host.MigrateProfiles();
        host.CheckStore("Monado settings", paths.MonadoSettingsFile, () => host.MonadoSettings.Load());
        host.CheckStore("settings", paths.SettingsFile, () => host.Settings.Load());

        // Before anything reads the stations: the manager reads its list at first use, and a file it set aside then
        // would go unnoticed here.
        host.CheckStore("base stations", paths.BaseStationsFile, () => host.stationStore.Load());
        _ = Task.Run(host.StartStationsAsync);
        _ = Task.Run(() => host.IsInstalledCopy(askHelper: true));
        host.OpenCompositeNotice = host.OpenVr.UpdateAtLaunch();
        return host;
    }

    /// <summary>
    /// An older install's profiles on the bundled Monado move to Mirador's Monado once the bundle is gone
    /// (Mirador_Plan.md Task 69); Home says so.
    /// </summary>
    private void MigrateProfiles()
    {
        try
        {
            var bundleGone = !File.Exists(Path.Combine(AppDir, "monado", "runtime", "openxr_monado.json"));
            var (set, changed) = Core.Migration.ProfileMigration.Migrate(Profiles.Load(), bundleGone);
            if (changed)
            {
                Profiles.Save(set);
                notices.Add("Mirador no longer comes with Monado: your profiles now use Mirador's Monado, upstream's nightly build, which Mirador downloads for them.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't move the profiles to Mirador's Monado", e);
        }
    }

    /// <summary>
    /// When a profile runs Mirador's Monado and none is installed: the helper installs the newest nightly (Mirador.md
    /// §4.11 item 55: the first run, or an install moved from the bundled Monado). What happened, for Home; null when
    /// nothing was needed.
    /// </summary>
    public async Task<(string Message, bool Ok)?> InstallMiradorsMonadoIfNeededAsync()
    {
        if (!Profiles.Load().Profiles.Any(p => p.Monado.Kind == SourceKind.Mirador)
            || Ipc.MiradorMonado.Read(Locator.MiradorRoot).Current is not null)
        {
            return null;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var newest = (await Ipc.MonadoReleaseList.FetchAsync(http, AppInfo.Version, CancellationToken.None))?.FirstOrDefault();
        if (newest is null)
        {
            return ("Mirador's Monado couldn't be downloaded: GitHub didn't answer, or no build is published yet. Try again on the Profiles page.", false);
        }

        var reply = await Helper.SendAsync(new Ipc.HelperRequest(Ipc.HelperCommand.InstallMonado, Version: newest.Version), CancellationToken.None);
        NotifyProfilesChanged();
        return reply.Ok
            ? ($"Mirador's Monado {newest.Version} is installed.", true)
            : ($"Mirador's Monado couldn't be installed: {reply.Error}", false);
    }

    /// <summary>Records <c>HKCU\Software\Mirador</c> <c>AppPath</c>, where the stand-in finds Mirador to start it.</summary>
    public static void RecordAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Mirador");
            key.SetValue("AppPath", Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"), RegistryValueKind.String);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppLog.Write("Couldn't record where Mirador is for games", e);
        }
    }

    /// <summary>Starts answering the stand-in and a second Mirador on the app pipe.</summary>
    /// <exception cref="MiradorException">Another program holds the pipe's name.</exception>
    public void StartPipe(Func<string, int?, CancellationToken, Task<string>> handle)
    {
        var server = new AppPipeServer(AppPipe.CurrentName(), handle);
        server.Failed += e => AppLog.Write("The app pipe stopped listening", e);
        server.HandlerFailed += e => AppLog.Write("Answering the app pipe failed", e);
        try
        {
            server.Start();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new MiradorException(
                $"Another program holds the name Mirador listens on for games ({AppPipe.CurrentName()}), so games can't start Monado through Mirador. Close other copies of Mirador, or restart Windows.");
        }

        pipe = server;
    }

    public async ValueTask DisposeAsync()
    {
        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        // Mirador's own off goes now, beside Monado's stop (a headset's end can take long). The session's end has its off
        // waiting its few seconds when Mirador's isn't wanted: the flush sends it at once.
        Stations.QuittingNow();
        await Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await Stations.FlushAsync(StationsLimit).ConfigureAwait(false);
        launcher.Dispose();
    }

    /// <summary>
    /// At Mirador's start, whatever started it: looks at the PC's Bluetooth, then scans for the base stations and, with
    /// "Power on when Mirador starts", switches the selected ones on (Mirador.md §4.7). The window doesn't wait for it.
    /// </summary>
    private async Task StartStationsAsync()
    {
        await StartRadioAsync().ConfigureAwait(false);
        try
        {
            await Stations.StartupAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Write("Base stations: Mirador's start with the base stations failed", e);
        }
    }

    /// <summary>
    /// Looks at the PC's Bluetooth (at start, and when devices change while it is missing); the window doesn't wait for
    /// it, and Bluetooth stays "Ready" until the first look. Never throws.
    /// </summary>
    private async Task StartRadioAsync()
    {
        try
        {
            await radio.StartAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Write("Base stations: Mirador couldn't look at the PC's Bluetooth", e);
        }
    }

    /// <summary>The settings for the base stations; never throws (the defaults when settings.json can't be read).</summary>
    private AppSettings StationSettings()
    {
        try
        {
            return Settings.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Base stations: Mirador couldn't read its settings ({e.Message.Trim().TrimEnd('.')}); it uses the defaults");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Reads a store once: a file it set aside as <c>.bad</c> just now means the settings were reset; one it can't read
    /// is named. Either way the user hears of it on Home.
    /// </summary>
    private void CheckStore(string what, string path, Action load)
    {
        var bad = path + ".bad";
        var before = Stamp(bad);
        try
        {
            load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notices.Add($"Mirador couldn't read its {what} ({path}): {e.Message.Trim().TrimEnd('.')}. It uses the defaults until it can.");
            return;
        }

        if (Stamp(bad) is { } after && after != before)
        {
            notices.Add($"Mirador's {what} file couldn't be read, so they were reset. The old file is {bad}.");
        }
    }

    private static (long Length, DateTime Written, DateTime Created)? Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) : null;
    }
}
