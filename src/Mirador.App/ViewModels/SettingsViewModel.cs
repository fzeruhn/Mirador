using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.SignIn;
using Mirador.Ipc;

namespace Mirador.App.ViewModels;

/// <summary>App settings: start at sign-in, close to the tray and the update check; and About. The base stations' settings are on the Lighthouse page.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppHost host;
    private readonly SignInStartup signIn = new(new RegistryRunKeys(), Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"));
    private readonly DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();
    private bool loading;

    public SettingsViewModel(AppHost host)
    {
        this.host = host;
        App.Current.Updates.Changed += OnUpdatesChanged;
        ShowReport();
        loading = true;
        var settings = Read(host, out var problem);
        Problem = problem;
        CloseToTray = settings.CloseToTray;
        CheckForUpdates = settings.CheckForUpdates;
        StartMinimized = settings.StartMinimized;
        try
        {
            StartAtSignIn = signIn.IsOn;
        }
        catch (MiradorException e)
        {
            Problem = e.Message;
        }

        try
        {
            standardAccount = ElevatedMonado.IsStandardAccount();
            RunMonadoAsAdministrator = ElevatedMonado.IsAllowed();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Problem = $"Mirador couldn't read whether Monado may run as administrator ({e.Message.Trim().TrimEnd('.')}).";
        }

        loading = false;
    }

    private const string RunAsAdministratorText = "Realtime GPU priority for Monado. Programs running as you could use this to get administrator rights.";
    private readonly bool standardAccount;

    /// <summary>
    /// Whether this user has allowed Monado to run as administrator. Turning it on asks Windows once (UAC); turning it off
    /// asks the helper service to forget it, with no prompt.
    /// </summary>
    [ObservableProperty]
    public partial bool RunMonadoAsAdministrator { get; set; }

    /// <summary>Whether a change is being made (the UAC prompt is up, or the helper is working): the switch waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeRunMonadoAsAdministrator))]
    public partial bool ChangingRunMonadoAsAdministrator { get; set; }

    /// <summary>False for a standard account, which has no administrator token to run Monado with, and while a change runs.</summary>
    public bool CanChangeRunMonadoAsAdministrator => !standardAccount && !ChangingRunMonadoAsAdministrator;

    public string RunMonadoAsAdministratorDescription =>
        standardAccount ? $"{RunAsAdministratorText} Needs an administrator account." : RunAsAdministratorText;

    /// <summary>Whether Windows starts Mirador at sign-in (read from the registry when the page opens).</summary>
    [ObservableProperty]
    public partial bool StartAtSignIn { get; set; }

    /// <summary>Whether a start at sign-in stays in the notification area (or minimized) instead of showing the window.</summary>
    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    public string Version => $"Mirador {AppInfo.Version}";

    /// <summary>The "Updates" card's description: the last check's report.</summary>
    [ObservableProperty]
    public partial string UpdatesText { get; set; } = "Not checked yet.";

    /// <summary>Mirador's release page, for the "Updates" card when a newer Mirador is out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMiradorRelease))]
    public partial Uri? MiradorReleaseUrl { get; set; }

    public bool HasMiradorRelease => MiradorReleaseUrl is not null;

    /// <summary>Whether "Update OpenComposite" shows: a newer OpenComposite is out and Mirador can update it (Task 48).</summary>
    [ObservableProperty]
    public partial bool CanUpdateOpenComposite { get; set; }

    /// <summary>Whether a check is running: the ring shows and "Check now" waits.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial bool Checking { get; set; }

    /// <summary>The "OpenComposite" card of About: the version and commit, and where this copy came from.</summary>
    public string OpenCompositeDescription =>
        (host.OpenCompositeCopy.CopySource ?? host.OpenCompositeCopy.BundleSource) is { } source
            ? $"{source.Describe()}. {(host.OpenCompositeCopy.HoldsDownload ? "From GitHub." : "Bundled with Mirador.")}"
            : "Not downloaded yet: Home has \"Get OpenComposite\".";

    /// <summary>Whether the copy holds a download, so "Roll back" shows.</summary>
    public bool HoldsDownload => host.OpenCompositeCopy.HoldsDownload;

    /// <summary>"today at 09:10" or "yesterday at 14:02", else "on 3 Nov at 14:02".</summary>
    internal static string DescribeLastCheck(DateTime last, DateTime now)
    {
        var time = last.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        var days = (now.Date - last.Date).Days;
        return days switch
        {
            0 => $"today at {time}",
            1 => $"yesterday at {time}",
            _ => $"on {last.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)} at {time}",
        };
    }

    private void OpenCompositeChanged()
    {
        OnPropertyChanged(nameof(OpenCompositeDescription));
        OnPropertyChanged(nameof(HoldsDownload));
    }

    public void Dispose() => App.Current.Updates.Changed -= OnUpdatesChanged;

    /// <summary>
    /// Reads Mirador's settings for a page whose settings save at once. When they can't be read, gives the defaults and
    /// says why in <paramref name="problem"/>; null otherwise.
    /// </summary>
    internal static AppSettings Read(AppHost host, out string? problem)
    {
        try
        {
            problem = null;
            return host.Settings.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"Mirador couldn't read its settings ({e.Message.Trim().TrimEnd('.')}).";
            return new AppSettings();
        }
    }

    /// <summary>Applies <paramref name="change"/> to the settings as saved now and saves them: null when saved, else why not.</summary>
    internal static string? Write(AppHost host, Func<AppSettings, AppSettings> change)
    {
        try
        {
            host.Settings.Save(change(host.Settings.Load()));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Mirador couldn't save its settings ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        Checking = true;
        outcome = null;
        try
        {
            await App.Current.Updates.CheckAsync();
            Problem = App.Current.Updates.RecordProblem;
        }
        catch (Exception e)
        {
            AppLog.Write("The update check failed", e);
            UpdatesText = $"The check failed ({e.Message.Trim().TrimEnd('.')}).";
        }
        finally
        {
            Checking = false;
        }
    }

    private bool CanCheckNow() => !Checking;

    // The last Update/Use-bundled outcome. The re-check inside those queues ShowReport, which may run before or after
    // the outcome is known; ShowReport puts the outcome in front of the report, and ShowOutcome sets the same text, so
    // either order ends with the same line.
    private string? outcome;

    private async Task ShowOutcome(Task<string> operation)
    {
        outcome = null;
        outcome = await operation;
        ShowReport();
    }

    /// <summary>An update check, daily or asked for, finished on whichever thread.</summary>
    private void OnUpdatesChanged() => ui.TryEnqueue(ShowReport);

    private void ShowReport()
    {
        var updates = App.Current.Updates;
        if (updates.Last is not { } report)
        {
            // No report from this run: the daily check may have been skipped because one already ran.
            if (Read(host, out _).LastUpdateCheck is { } last)
            {
                UpdatesText = $"Last checked {DescribeLastCheck(last.LocalDateTime, DateTime.Now)}.";
            }

            return;
        }

        UpdatesText = outcome is null ? report.Describe() : $"{outcome} {report.Describe()}";
        MiradorReleaseUrl = report.NewerMirador is { } m && Uri.TryCreate(m.Url, UriKind.Absolute, out var url) ? url : null;
        CanUpdateOpenComposite = report.NewerOpenComposite is { WindowsZipUrl: not null } && updates.CanUpdateOpenComposite;
    }

    [RelayCommand]
    private async Task UpdateOpenCompositeAsync()
    {
        if (App.Current.Updates.Last?.NewerOpenComposite is not { } release)
        {
            return;
        }

        try
        {
            Problem = null;
            await ShowOutcome(App.Current.Updates.UpdateOpenCompositeAsync(release));
        }
        catch (Exception e)
        {
            AppLog.Write("Updating OpenComposite failed", e);
            Problem = $"Updating OpenComposite failed ({e.Message.Trim().TrimEnd('.')}); nothing changed.";
        }

        OpenCompositeChanged();
    }

    [RelayCommand]
    private async Task RollBackOpenCompositeAsync()
    {
        try
        {
            Problem = null;
            await ShowOutcome(App.Current.Updates.RollBackOpenCompositeAsync());
        }
        catch (Exception e)
        {
            AppLog.Write("Rolling OpenComposite back failed", e);
            Problem = $"Rolling OpenComposite back failed ({e.Message.Trim().TrimEnd('.')}); nothing changed.";
        }

        OpenCompositeChanged();
    }

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnStartAtSignInChanged(bool value)
    {
        if (loading)
        {
            return;
        }

        try
        {
            if (value)
            {
                signIn.TurnOn();
            }
            else
            {
                signIn.TurnOff();
            }

            Problem = null;
        }
        catch (MiradorException e)
        {
            Problem = e.Message;

            // The switch goes back to what Windows has.
            loading = true;
            try
            {
                StartAtSignIn = !value;
            }
            finally
            {
                loading = false;
            }
        }
    }

    partial void OnRunMonadoAsAdministratorChanged(bool value)
    {
        if (!loading)
        {
            _ = ChangeRunMonadoAsAdministratorAsync(value);
        }
    }

    private async Task ChangeRunMonadoAsAdministratorAsync(bool value)
    {
        ChangingRunMonadoAsAdministrator = true;
        try
        {
            Problem = null;
            string? problem = null;
            if (value)
            {
                var code = await ElevatedMonado.AllowAsync();
                if (code == 0)
                {
                    // The consent step recorded SteamVR's Lighthouse files as they are now.
                    host.SteamVrAllowedAgain();
                }
                else if (code != ElevatedRuntime.PromptCancelled)
                {
                    problem = "Mirador couldn't record the consent for Monado to run as administrator. The reason is in mirador.log.";
                }
            }
            else
            {
                var reply = await host.Helper.SendAsync(HelperCommand.RevokeElevatedMonado, CancellationToken.None);
                if (!reply.Ok)
                {
                    problem = $"Mirador couldn't take back the consent for Monado to run as administrator: {reply.Error}";
                }
            }

            // The switch shows what the registry says now: a cancelled prompt or a failure leaves it as it was.
            bool actual;
            try
            {
                actual = ElevatedMonado.IsAllowed();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                actual = !value;
                problem ??= $"Mirador couldn't read whether Monado may run as administrator ({e.Message.Trim().TrimEnd('.')}).";
            }

            loading = true;
            try
            {
                RunMonadoAsAdministrator = actual;
            }
            finally
            {
                loading = false;
            }

            Problem = problem;
        }
        catch (Exception e)
        {
            AppLog.Write("Changing whether Monado runs as administrator failed", e);
            Problem = $"Changing whether Monado runs as administrator failed ({e.Message.Trim().TrimEnd('.')}).";
        }
        finally
        {
            ChangingRunMonadoAsAdministrator = false;
        }
    }

    partial void OnStartMinimizedChanged(bool value) => Save(s => s with { StartMinimized = value });

    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s with { CheckForUpdates = value });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!loading)
        {
            Problem = Write(host, change);
        }
    }
}
