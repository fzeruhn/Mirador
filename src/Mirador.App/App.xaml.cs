using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.App.ViewModels;
using Mirador.Core;
using Mirador.Core.Diagnostics;
using Mirador.Core.Modules;
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
    private DispatcherQueue? ui;
    private bool lastStartFailed;
    private bool quitting;

    public App(AppArgs args)
    {
        this.args = args;
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Write("Unhandled", e.Exception);
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

    /// <summary>The UI thread's queue, for handing work from background threads to it.</summary>
    public DispatcherQueue Ui => ui ?? throw new InvalidOperationException("Mirador hasn't started yet.");

    /// <summary>Whether Mirador is on its way out (closing the window then really closes it).</summary>
    public bool Quitting => quitting;

    /// <summary>Raised on the UI thread when the last Start failed, with its message (null once a Start worked).</summary>
    public event Action<string?>? StartFailed;

    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        ui = DispatcherQueue.GetForCurrentThread();
        var paths = AppPaths.Default;
        AppLog.Open(paths);
        AppLog.Write($"Mirador {typeof(App).Assembly.GetName().Version} started from {AppContext.BaseDirectory}");
        try
        {
            host = AppHost.Create(AppContext.BaseDirectory, paths);
        }
        catch (MiradorException ex)
        {
            AppLog.Write("Mirador couldn't start: " + ex.Message);
            Exit();
            return;
        }

        Notifications.Register(() => Ui.TryEnqueue(() => MainWindow.ShowAndActivate()));
        home = new HomeViewModel(Host, Ui);
        window = new MainWindow();
        tray = new TrayIcon(() => MainWindow.ShowAndActivate(), StartOrStopAsync, QuitAsync);
        Host.Session.Changed += () => Ui.TryEnqueue(UpdateTray);
        Host.Session.StatusChanged += () => Ui.TryEnqueue(UpdateTray);
        Host.Session.Warning += w => AppLog.Write($"Warning: {w.Text}");
        Host.Session.Ended += text => AppLog.Write($"Monado ended: {text}");
        AppHost.RecordAppPath();

        if (!args.Background)
        {
            MainWindow.ShowAndActivate();
        }

        // Before the pipe: a game's Start would otherwise replace session.json while it still holds steps to undo.
        await Host.Session.RecoverAsync(CancellationToken.None);
        try
        {
            Host.StartPipe(AnswerPipeAsync);
        }
        catch (MiradorException ex)
        {
            AppLog.Write(ex.Message);
            Notifications.Show("Games can't start Monado", ex.Message);
        }

        if (args.Start)
        {
            await StartAsync(StartReason.Tray);
        }

        await CheckForUpdateAsync();
    }

    /// <summary>At most once a day, when on: a newer release shows on Home.</summary>
    private async Task CheckForUpdateAsync()
    {
        AppSettings settings;
        try
        {
            settings = Host.Settings.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!settings.CheckForUpdates || (settings.LastUpdateCheck is { } last && now - last < TimeSpan.FromDays(1) && last <= now))
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var found = await UpdateCheck.CheckAsync(http, AppInfo.Version, CancellationToken.None);
        try
        {
            Host.Settings.Save(Host.Settings.Load() with { LastUpdateCheck = now });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't record the update check", ex);
        }

        if (found is { } release)
        {
            AppLog.Write($"Mirador {release.Tag} is out: {release.Url}");
            Home.ShowUpdate(release.Tag, release.Url);
        }
    }

    /// <summary>Starts Monado for the window or the tray, keeping what went wrong for Home and the tray.</summary>
    public async Task<StartOutcome> StartAsync(StartReason reason)
    {
        var outcome = await Host.Session.StartAsync(reason, CancellationToken.None);
        Ui.TryEnqueue(() => Report(outcome));
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

        if (Host.Session.State != SessionState.Stopped)
        {
            MainWindow.ShowAndActivate();
            var dialog = new ContentDialog
            {
                XamlRoot = MainWindow.Content.XamlRoot,
                Title = "Stop Monado and quit?",
                Content = "Apps using Monado will lose it.",
                PrimaryButtonText = "Stop and quit",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        quitting = true;
        AppLog.Write("Quitting");
        try
        {
            await Host.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Stopping on the way out failed", ex);
        }

        tray?.Dispose();
        Notifications.Unregister();
        AppLog.Close();
        window?.Close();
        Exit();
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

    /// <summary>Answers the stand-in (START) and a second Mirador (SHOW, START).</summary>
    private async Task<string> AnswerPipeAsync(string request, CancellationToken ct)
    {
        switch (request)
        {
            case AppPipe.Show:
                Ui.TryEnqueue(() => MainWindow.ShowAndActivate());
                return AppPipe.Ok;

            case AppPipe.Start:
                AppLog.Write("A game asked for Monado");
                var outcome = await Host.Session.StartAsync(StartReason.Game, ct);
                Ui.TryEnqueue(() =>
                {
                    Report(outcome);
                    if (!outcome.Ok && !MainWindow.IsShown)
                    {
                        Notifications.Show("Monado didn't start for a game", outcome.Message);
                    }
                });
                return outcome.Ok ? AppPipe.Ok : AppPipe.FailPrefix + outcome.Message;

            default:
                return AppPipe.FailPrefix + "Mirador doesn't know that request.";
        }
    }

    private void Report(StartOutcome outcome)
    {
        lastStartFailed = !outcome.Ok;
        if (!outcome.Ok)
        {
            AppLog.Write("Start failed: " + outcome.Message);
        }

        StartFailed?.Invoke(outcome.Ok ? null : outcome.Message);
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (tray is null || host is null || quitting)
        {
            return;
        }

        var state = Host.Session.State;
        var problem = lastStartFailed || Host.Session.Status.Any(l => l.Level == StatusLevel.Error);
        if (state == SessionState.Running)
        {
            lastStartFailed = false;
        }

        var (look, tooltip, action) = state switch
        {
            SessionState.Running => (problem ? TrayState.Problem : TrayState.Running, "Mirador: Monado is running", "Stop"),
            SessionState.Starting => (TrayState.Running, "Mirador: Monado is starting", "Stop"),
            SessionState.Stopping => (TrayState.Stopped, "Mirador: Monado is stopping", "Stop"),
            _ => (problem ? TrayState.Problem : TrayState.Stopped, problem ? "Mirador: Monado didn't start" : "Mirador: Monado is stopped", "Start"),
        };
        tray.Show(look, tooltip, action);
    }
}
