using Microsoft.Win32;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Core.Session;
using Mirador.DreamAir;
using Mirador.Ipc;

namespace Mirador.App.Services;

/// <summary>
/// Everything Mirador is made of, put together once at start: the stores, the headset modules, the helper, the Monado
/// session and the app pipe. Paths into the install follow its layout (<c>{app}\monado</c>, <c>{app}\layer</c>, …).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly JobProcessLauncher launcher;
    private readonly List<string> notices = [];
    private AppPipeServer? pipe;

    private AppHost(string appDir, AppPaths paths)
    {
        AppDir = appDir;
        Paths = paths;
        Profiles = new ProfileStore(paths.ProfilesFile);
        MonadoSettings = new MonadoSettingsStore(paths.MonadoSettingsFile);
        Settings = new AppSettingsStore(paths.SettingsFile);
        Helper = new HelperClient();
        Runtime = new OpenXrRuntime(new RegistryRuntimeReader());
        Locator = new MonadoLocator(Path.Combine(appDir, "monado"));
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

        launcher = new JobProcessLauncher();
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
            UserEnvironment.Default(e => AppLog.Write("Windows didn't give the user's environment; Monado gets Mirador's own", e))));
    }

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

    /// <summary>
    /// Puts Mirador together and reads its settings once, so that a file it had to set aside, or couldn't read, is
    /// known from the start.
    /// </summary>
    /// <exception cref="MiradorException">A part Mirador can't run without couldn't be set up.</exception>
    public static AppHost Create(string appDir, AppPaths paths)
    {
        var host = new AppHost(appDir, paths);
        host.CheckStore("profiles", paths.ProfilesFile, () => host.Profiles.Load());
        host.CheckStore("Monado settings", paths.MonadoSettingsFile, () => host.MonadoSettings.Load());
        host.CheckStore("settings", paths.SettingsFile, () => host.Settings.Load());
        return host;
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

        await Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        launcher.Dispose();
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
