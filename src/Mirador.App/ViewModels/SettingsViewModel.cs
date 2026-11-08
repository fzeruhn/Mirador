using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.SignIn;

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

        loading = false;
    }

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

    /// <summary>The "OpenComposite" card of About: <c>OpenComposite 2026.11.06 (7534249)</c>.</summary>
    public string OpenCompositeHeader =>
        host.OpenCompositeCopy.BundleSource is { } source ? $"OpenComposite {source.Describe()}" : "OpenComposite (not in this build)";

    /// <summary>Where this OpenComposite came from; "Updated from GitHub." is Task 48's.</summary>
    public string OpenCompositeDescription => "Bundled with Mirador.";

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

    /// <summary>An update check, daily or asked for, finished on whichever thread.</summary>
    private void OnUpdatesChanged() => ui.TryEnqueue(ShowReport);

    private void ShowReport()
    {
        var updates = App.Current.Updates;
        if (updates.Last is not { } report)
        {
            return;
        }

        UpdatesText = report.Describe();
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
            await App.Current.Updates.UpdateOpenCompositeAsync(release);
        }
        catch (Exception e)
        {
            AppLog.Write("Updating OpenComposite failed", e);
            Problem = $"Updating OpenComposite failed ({e.Message.Trim().TrimEnd('.')}); nothing changed.";
        }
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
