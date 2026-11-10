using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.App.ViewModels;
using Mirador.Core;
using Mirador.Core.BaseStations;
using Mirador.Core.Diagnostics;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Session;
using Mirador.Ipc;

namespace Mirador.App;

public partial class App : Application
{
    private readonly AppArgs args;
    private AppHost? host;
    private MainWindow? window;
    private TrayIcon? tray;
    private HomeViewModel? home;
    private Updates? updates;
    private DispatcherQueue? ui;
    private bool lastStartFailed;
    private bool endedBadly;
    private bool quitting;
    private bool closingForGood;
    private int stationItemsQueued;

    public App(AppArgs args)
    {
        this.args = args;
        InitializeComponent();

        // Mirador lives in the tray: only Quit ends it, never the window closing (nor a close during Quit).
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            // A crash would take Monado down with it (and leave the headset's software stopped): log and go on.
            AppLog.Write("Unhandled", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved", e.Exception);
            e.SetObserved();
        };
    }

    public static new App Current => (App)Application.Current;

    /// <summary>Everything Mirador is made of; set before the window opens.</summary>
    public AppHost Host => host ?? throw new InvalidOperationException("Mirador hasn't started yet.");

    public MainWindow MainWindow => window ?? throw new InvalidOperationException("Mirador hasn't started yet.");

    /// <summary>Home's view model, made at start so that nothing that happens before Home opens is lost.</summary>
    public HomeViewModel Home => home ?? throw new InvalidOperationException("Mirador hasn't started yet.");

    /// <summary>The check for newer releases, daily and on request.</summary>
    public Updates Updates => updates ?? throw new InvalidOperationException("Mirador hasn't started yet.");

    /// <summary>The UI thread's queue, for handing work from background threads to it.</summary>
    public DispatcherQueue Ui => ui ?? throw new InvalidOperationException("Mirador hasn't started yet.");

    /// <summary>Whether Mirador is on its way out.</summary>
    public bool Quitting => quitting;

    /// <summary>Whether the window may really close: set only once Quit has stopped everything.</summary>
    public bool ClosingForGood => closingForGood;

    /// <summary>Raised on the UI thread when the last Start failed, with its message (null once a Start worked).</summary>
    public event Action<string?>? StartFailed;

    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        ui = DispatcherQueue.GetForCurrentThread();
        var paths = AppPaths.Default;
        AppLog.Open(paths);
        AppLog.Write($"Mirador {AppInfo.Version} started from {AppContext.BaseDirectory}");
        try
        {
            host = AppHost.Create(AppContext.BaseDirectory, paths);
            Notifications.Register(() => Ui.TryEnqueue(() => MainWindow.ShowAndActivate()));
            home = new HomeViewModel(Host, Ui);
            updates = new Updates(Host);
            window = new MainWindow();
            tray = new TrayIcon(() => MainWindow.ShowAndActivate(), () => MainWindow.ToggleFromTray(), StartOrStopAsync, SwitchStationsAsync, QuitAsync);
        }
        catch (Exception ex)
        {
            AppLog.Write("Mirador couldn't start", ex);
            Program.MessageBox(ex is MiradorException ? ex.Message : $"Mirador couldn't start: {ex.Message}");
            tray?.Dispose();
            Exit();
            return;
        }

        Updates.Changed += () => Ui.TryEnqueue(() => Home.ShowUpdates(Updates.Last));
        Host.Stations.Changed += OnStationsChanged;
        UpdateStationItems();
        Host.Session.Changed += () => Ui.TryEnqueue(UpdateTray);
        Host.Session.StatusChanged += () => Ui.TryEnqueue(UpdateTray);
        Host.Session.Warning += w =>
        {
            AppLog.Write($"Warning: {w.Text}");
            Ui.TryEnqueue(() => NotifyUnlessSeen("While Monado runs", w.Text));
        };
        Host.Session.Ended += text =>
        {
            AppLog.Write($"Monado ended: {text}");
            Ui.TryEnqueue(() => OnEnded(text));
        };
        AppHost.RecordAppPath();
        _ = Task.Run(Host.Session.PlaceStandIn);

        ShowFirstWindow();

        // The pipe opens before recovery has finished, without waiting for it: a game's Start waits for recovery
        // inside the session (it never replaces session.json that still holds steps to undo), and a stand-in that
        // started Mirador gets connected at once instead of timing out while Pimax's service comes back.
        var recovery = Host.Session.RecoverAsync(CancellationToken.None);
        try
        {
            Host.StartPipe(AnswerPipeAsync);
        }
        catch (MiradorException ex)
        {
            AppLog.Write(ex.Message);
            Notifications.Show("Games can't start Monado", ex.Message);
        }

        await recovery;
        if (args.Start)
        {
            await StartAsync(StartReason.Tray);
        }

        await CheckForUpdateAsync();
    }

    /// <summary>
    /// The window at launch: none for <c>--background</c>; for <c>--sign-in</c> with "Start minimized" none when Mirador
    /// closes to the tray (the tray icon is there), else minimized on the taskbar; otherwise shown.
    /// </summary>
    private void ShowFirstWindow()
    {
        if (args.Background)
        {
            return;
        }

        if (args.SignIn)
        {
            var settings = SettingsViewModel.Read(Host, out _);
            if (settings.StartMinimized)
            {
                if (!settings.CloseToTray)
                {
                    MainWindow.ShowMinimized();
                }

                return;
            }
        }

        MainWindow.ShowAndActivate();
    }

    /// <summary>At most once a day, when on: a newer release of Mirador or of OpenComposite shows on Home.</summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            // On the first run, only once the user said where Monado comes from, and only for "Bundled Monado".
            if (await Host.FirstRunAnswered && await Host.InstallMiradorsMonadoIfNeededAsync() is { } installed)
            {
                Ui.TryEnqueue(() => Home.ShowMonadoNotice(installed.Message, installed.Ok));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Installing the bundled Monado at launch failed", ex);
        }

        try
        {
            await Updates.CheckDailyAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("The update check failed", ex);
        }
    }

    /// <summary>Starts Monado for the window or the tray, keeping what went wrong for Home and the tray.</summary>
    public async Task<StartOutcome> StartAsync(StartReason reason)
    {
        var outcome = await Host.Session.StartAsync(reason, CancellationToken.None);
        Ui.TryEnqueue(() => Report(outcome, reason));
        return outcome;
    }

    /// <summary>
    /// Quits Mirador. While Monado runs it asks first ("Stop Monado and quit?"), then stops it, which puts the
    /// headset's software back.
    /// </summary>
    public async Task QuitAsync()
    {
        if (quitting)
        {
            return;
        }

        if (Host.Session.State != SessionState.Stopped && !await AskToStopAsync())
        {
            return;
        }

        quitting = true;
        AppLog.Write("Quitting");
        try
        {
            // A display check (or recovery) under way has Pimax's software stopped: it puts it back first.
            await Host.Session.WhenSettledAsync();
            await Host.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Stopping on the way out failed", ex);
        }

        tray?.Dispose();
        Notifications.Unregister();
        AppLog.Close();
        closingForGood = true;
        window?.Close();
        Exit();
    }

    /// <summary>"Stop Monado and quit?" in the window (shown first, and loaded, which a window started hidden isn't yet).</summary>
    private async Task<bool> AskToStopAsync()
    {
        MainWindow.ShowAndActivate();
        try
        {
            var root = await MainWindow.LoadedRootAsync();
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = "Stop Monado and quit?",
                Content = "Apps using Monado will lose it.",
                PrimaryButtonText = "Stop and quit",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // Most likely another dialog is open: the user is busy in the window, so Mirador stays.
            AppLog.Write("Couldn't ask whether to stop Monado and quit", ex);
            return false;
        }
    }

    private async Task StartOrStopAsync()
    {
        if (Host.Session.State == SessionState.Stopped)
        {
            await StartAsync(StartReason.Tray);
        }
        else
        {
            await Host.Session.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Answers the stand-in (START) and a second Mirador (SHOW, START); <paramref name="caller"/> is the asking process.</summary>
    private async Task<string> AnswerPipeAsync(string request, int? caller, CancellationToken ct)
    {
        switch (request)
        {
            case AppPipe.Show:
                Ui.TryEnqueue(() => MainWindow.ShowAndActivate());
                return AppPipe.Ok;

            case AppPipe.Start:
                // A stand-in sits beside the Monado runtime the game loaded: only that Monado may start for it.
                var standIn = StandIn.ProgramOf(caller, Host.Processes.ImagePathOf);
                if (await Host.TryAnswerStandInAsync(standIn, ct) is { } verifying)
                {
                    AppLog.Write($"Verify's game asked for Monado ({standIn})");
                    return verifying;
                }

                AppLog.Write(standIn is null ? "A game asked for Monado" : $"A game asked for Monado ({standIn})");
                var wasStopped = Host.Session.State == SessionState.Stopped;
                var outcome = await Host.Session.StartAsync(StartReason.Game, standIn, ct);
                Ui.TryEnqueue(() =>
                {
                    Report(outcome, StartReason.Game);
                    if (outcome.Ok && wasStopped)
                    {
                        NotifyUnlessSeen("A game started Monado", "Monado is running.");
                    }
                });
                return outcome.Ok ? AppPipe.Ok : AppPipe.FailPrefix + outcome.Message;

            default:
                return AppPipe.FailPrefix + "Mirador doesn't know that request.";
        }
    }

    private void Report(StartOutcome outcome, StartReason reason)
    {
        lastStartFailed = !outcome.Ok;
        if (outcome.Ok)
        {
            endedBadly = false;
        }
        else
        {
            AppLog.Write("Start failed: " + outcome.Message);
            NotifyUnlessSeen(reason == StartReason.Game ? "Monado didn't start for a game" : "Monado didn't start", outcome.Message);
        }

        StartFailed?.Invoke(outcome.Ok ? null : outcome.Message);
        UpdateTray();
    }

    /// <summary>Monado ended: by itself is a problem (the tray says so, and a toast when the window isn't in front).</summary>
    private void OnEnded(string text)
    {
        if (text.StartsWith("Monado stopped by itself", StringComparison.Ordinal))
        {
            endedBadly = true;
            NotifyUnlessSeen("Monado stopped", text);
        }

        UpdateTray();
    }

    /// <summary>A toast, unless Mirador's window is in front, where the user sees it anyway.</summary>
    private void NotifyUnlessSeen(string title, string text)
    {
        if (window is null || !window.IsInForeground)
        {
            Notifications.Show(title, text);
        }
    }

    /// <summary>
    /// The tray's two base-station items: On, or Off (what <see cref="StationManager.OffCommand"/> says) for any other
    /// command. Only the selected stations are switched. A problem in the summary afterwards reaches the user in a
    /// notification when the window is hidden (in the window, Home shows it).
    /// </summary>
    private async Task SwitchStationsAsync(StationCommand command)
    {
        try
        {
            await Host.Stations.SwitchSelectedAsync(command == StationCommand.On ? command : Host.Stations.OffCommand(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Write("Switching the base stations from the tray failed", ex);
            Ui.TryEnqueue(() => NotifyBaseStations(ex.Message));
            return;
        }

        Ui.TryEnqueue(() =>
        {
            var summary = Host.Stations.Summary;
            if (summary.Level is StatusLevel.Warning or StatusLevel.Error)
            {
                NotifyBaseStations(summary.Text);
            }
        });
    }

    private void NotifyBaseStations(string text)
    {
        if (window is null || !window.IsShown)
        {
            Notifications.Show("Base stations", text);
        }
    }

    /// <summary>On whichever thread the manager is on: updates the tray's station items on the UI thread, once for a burst.</summary>
    private void OnStationsChanged()
    {
        if (Interlocked.Exchange(ref stationItemsQueued, 1) == 0 && !Ui.TryEnqueue(() =>
            {
                Volatile.Write(ref stationItemsQueued, 0);
                UpdateStationItems();
            }))
        {
            Volatile.Write(ref stationItemsQueued, 0);
        }
    }

    /// <summary>The station items are there while Bluetooth is ready and a station is selected.</summary>
    private void UpdateStationItems()
    {
        if (tray is null || host is null || quitting)
        {
            return;
        }

        try
        {
            tray.ShowStations(Host.Stations.Bluetooth == BluetoothState.Ready && Host.Stations.Stations.Any(s => s.Station.Selected));
        }
        catch (Exception ex)
        {
            AppLog.Write("Reading the base stations for the tray failed", ex);
        }
    }

    private void UpdateTray()
    {
        if (tray is null || host is null || quitting)
        {
            return;
        }

        var state = Host.Session.State;
        var problem = lastStartFailed || endedBadly || Host.Session.Status.Any(l => l.Level == StatusLevel.Error);
        if (state == SessionState.Running)
        {
            lastStartFailed = false;
        }

        var (look, tooltip, action) = state switch
        {
            SessionState.Running => problem
                ? (TrayState.RunningProblem, "Mirador: Monado is running with a problem", "Stop")
                : (TrayState.Running, "Mirador: Monado is running", "Stop"),
            SessionState.Starting => (TrayState.Running, "Mirador: Monado is starting", "Stop"),
            SessionState.Stopping => (TrayState.Stopped, "Mirador: Monado is stopping", "Stop"),
            _ => (problem ? TrayState.Problem : TrayState.Stopped, problem ? (endedBadly ? "Mirador: Monado stopped by itself" : "Mirador: Monado didn't start") : "Mirador: Monado is stopped", "Start"),
        };
        tray.Show(look, tooltip, action);
    }
}
