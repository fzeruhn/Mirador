using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.OpenVr;
using Mirador.Core.Profiles;
using Mirador.Core.Runtime;
using Mirador.Core.Session;

namespace Mirador.App.ViewModels;

/// <summary>
/// Home: the profile, the one Start/Stop button, Monado's status lines, the active OpenXR runtime, and what needs the
/// user's attention (the last failed Start, the runtime, checks, warnings). Lives as long as Mirador, so that what
/// happens while another page is open isn't lost. Everything here runs on the UI thread.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly DispatcherQueue ui;
    private CancellationTokenSource? checking;
    private bool loading;
    private string? openCompositeProblem;

    public HomeViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        this.ui = ui;
        foreach (var notice in host.StartupNotices)
        {
            Notices.Add(new NoticeItem("startup", "Settings", notice, InfoBarSeverity.Warning, IsClosable: true));
        }

        if (host.OpenCompositeNotice is { } openComposite)
        {
            Notices.Add(new NoticeItem("opencomposite", "OpenVR runtime", openComposite, InfoBarSeverity.Warning, IsClosable: true));
        }

        // A headset plugged in or out: the checks say so at once, not at the next visit to Home.
        host.DevicesChanged += () => ui.TryEnqueue(() => _ = RunChecksAsync());

        host.Session.Changed += () => ui.TryEnqueue(OnSessionChanged);
        host.Session.StatusChanged += () => ui.TryEnqueue(RefreshStatus);
        host.Session.Warning += w => ui.TryEnqueue(() => AddWarning(w));
        host.Session.Ended += text => ui.TryEnqueue(() => OnEnded(text));
        App.Current.StartFailed += message =>
        {
            // Through null, so that the same message again opens the bar again after it was closed.
            LastFailure = null;
            LastFailure = message;
        };
        LoadProfiles();
        OnSessionChanged();
    }

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    public ObservableCollection<StatusItem> Status { get; } = [];

    /// <summary>Notices from Mirador's start, check results that need a look, and warnings while Monado runs.</summary>
    public ObservableCollection<NoticeItem> Notices { get; } = [];

    [ObservableProperty]
    public partial ProfileItem? SelectedProfile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeProfile))]
    public partial SessionState State { get; set; }

    [ObservableProperty]
    public partial string StartStopLabel { get; set; } = "Start";

    [ObservableProperty]
    public partial bool CanStartStop { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial string? LastFailure { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeNotice))]
    public partial string? RuntimeNotice { get; set; }

    /// <summary>The "OpenXR runtime" row of the runtimes card.</summary>
    [ObservableProperty]
    public partial StatusItem OpenXrRow { get; set; } = new("OpenXR runtime", "", StatusLevel.Info);

    [ObservableProperty]
    public partial bool CanSetRuntime { get; set; }

    /// <summary>Whether the "OpenVR runtime" row shows: not in a dev run without OpenComposite.</summary>
    [ObservableProperty]
    public partial bool ShowOpenVr { get; set; }

    /// <summary>The "OpenVR runtime" row of the runtimes card.</summary>
    [ObservableProperty]
    public partial StatusItem OpenVrRow { get; set; } = new("OpenVR runtime", "", StatusLevel.Info);

    [ObservableProperty]
    public partial bool CanSetOpenComposite { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeProblem))]
    public partial string? RuntimeProblem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenVrProblem))]
    public partial string? OpenVrProblem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenVrWarning))]
    public partial string? OpenVrWarning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial string? UpdateText { get; set; }

    [ObservableProperty]
    public partial Uri? UpdateUrl { get; set; }

    public bool CanChangeProfile => State == SessionState.Stopped;

    public bool HasFailure => LastFailure is not null;

    public bool HasRuntimeNotice => RuntimeNotice is not null;

    public bool HasRuntimeProblem => RuntimeProblem is not null;

    public bool HasOpenVrProblem => OpenVrProblem is not null;

    public bool HasOpenVrWarning => OpenVrWarning is not null;

    public bool HasUpdate => UpdateText is not null;

    /// <summary>Reads the profiles again (the Profiles page may have changed them) and runs the checks.</summary>
    public void Refresh()
    {
        LoadProfiles();
        RefreshRuntime();
        _ = RunChecksAsync();
    }

    /// <summary>Shows a newer release (from the update check).</summary>
    public void ShowUpdate(string tag, string url)
    {
        UpdateText = $"Mirador {tag} is out.";
        UpdateUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>Takes a notice away when its bar is closed.</summary>
    public void Dismiss(NoticeItem notice) => Notices.Remove(notice);

    // Concurrent: pressing "Starting…" must reach Stop while the Start it began still runs.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartStopAsync()
    {
        try
        {
            if (host.Session.State == SessionState.Stopped)
            {
                LastFailure = null;
                await App.Current.StartAsync(StartReason.Button);
            }
            else
            {
                await host.Session.StopAsync(CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            AppLog.Write("Start or Stop failed", e);
            LastFailure = e.Message;
        }
    }

    [RelayCommand]
    private async Task SetRuntimeAsync()
    {
        RuntimeProblem = null;
        MonadoInstall install;
        try
        {
            install = host.Session.CurrentInstall ?? host.Locator.Locate(SelectedSet().Selected.Monado);
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
            RuntimeProblem = e.Message;
            return;
        }

        // The elevated step's errors never reach the user, so the manifest is checked here first.
        if (RuntimeManifest.Validate(install.Manifest) is { } problem)
        {
            RuntimeProblem = problem;
            return;
        }

        CanSetRuntime = false;
        try
        {
            RuntimeProblem = await RuntimeSwitch.SetAsync(install, host.Helper, ElevatedRuntime.RunAsync, host.Runtime);
        }
        catch (Exception e)
        {
            AppLog.Write("Setting the OpenXR runtime failed", e);
            RuntimeProblem = $"Setting the OpenXR runtime failed ({e.Message.Trim().TrimEnd('.')}); the runtime is unchanged.";
        }
        finally
        {
            RefreshRuntime();
        }
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (loading || value is null)
        {
            return;
        }

        // A failed Start was about the profile picked before.
        LastFailure = null;
        try
        {
            var set = host.Profiles.Load();
            if (set.SelectedId != value.Id && set.Profiles.Any(p => p.Id == value.Id))
            {
                host.Profiles.Save(set.Select(value.Id));
                host.NotifyProfilesChanged();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Back to the profile a Start would use, and say why.
            ShowNotice("profiles", "Profiles", $"Mirador couldn't save the profile choice ({e.Message.Trim().TrimEnd('.')}).", InfoBarSeverity.Error);
            LoadProfiles();
        }

        RefreshRuntime();
        _ = RunChecksAsync();
    }

    private ProfileSet SelectedSet() => host.Profiles.Load();

    private void LoadProfiles()
    {
        loading = true;
        try
        {
            var set = SelectedSet();
            Profiles.Clear();
            foreach (var p in set.Profiles)
            {
                Profiles.Add(new ProfileItem(p.Id, p.Name));
            }

            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == set.SelectedId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowNotice("profiles", "Profiles", $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).", InfoBarSeverity.Error);
        }
        finally
        {
            loading = false;
        }
    }

    private void OnSessionChanged()
    {
        var previous = State;
        State = host.Session.State;
        (StartStopLabel, CanStartStop) = State switch
        {
            SessionState.Running => ("Stop", true),
            SessionState.Starting => ("Starting…", true),
            SessionState.Stopping => ("Stopping…", false),
            _ => ("Start", true),
        };
        if (State == SessionState.Starting && previous == SessionState.Stopped)
        {
            // A new session, however it was started: the last one's warnings and end are history.
            ClearWarnings();
            RemoveNotice("ended");
        }

        RefreshStatus();
        RefreshRuntime();
        if (State == SessionState.Stopped && previous != SessionState.Stopped)
        {
            // "While Monado runs" no longer holds; how it ended is the "ended" notice.
            ClearWarnings();
            _ = RunChecksAsync();
        }
    }

    private void RefreshStatus()
    {
        var lines = host.Session.Status.Select(StatusItem.From).ToList();
        if (!lines.SequenceEqual(Status))
        {
            Status.Clear();
            foreach (var line in lines)
            {
                Status.Add(line);
            }
        }
    }

    private void RefreshRuntime()
    {
        MonadoInstall? install = host.Session.CurrentInstall;
        if (install is null)
        {
            try
            {
                install = host.Locator.Locate(SelectedSet().Selected.Monado);
            }
            catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
            {
                install = null;
            }
        }

        var active = host.Runtime.Active();
        var isActive = install is not null && host.Runtime.IsActive(install.Manifest);
        OpenXrRow = new StatusItem("OpenXR runtime", RuntimeLine.Describe(active, install, isActive), isActive ? StatusLevel.Ok : StatusLevel.Info);
        CanSetRuntime = install is not null && !isActive;
        if (isActive)
        {
            // Why setting it failed no longer matters once it is set.
            RuntimeProblem = null;
        }

        RuntimeNotice = host.Session.RuntimeNotice;
        RefreshOpenVr(isActive, active);
    }

    /// <summary>
    /// The "OpenVR runtime" row (Mirador.md §4.6 item 21), read from the paths file each time, and what needs a look.
    /// </summary>
    private void RefreshOpenVr(bool openXrIsMonado, string? activeOpenXr)
    {
        ShowOpenVr = host.OpenVr.Available;
        if (!ShowOpenVr)
        {
            CanSetOpenComposite = false;
            OpenVrWarning = null;
            OpenVrProblem = null;
            return;
        }

        OpenVrView view;
        try
        {
            view = host.OpenVr.View(openXrIsMonado, activeOpenXr);
        }
        catch (Exception e)
        {
            // Never let this stop the rest of Home (the checks run after it).
            AppLog.Write("Reading where OpenVR games go failed", e);
            view = new OpenVrView("Unknown", false, null, $"Mirador couldn't read where OpenVR games go ({e.Message.Trim().TrimEnd('.')}).");
        }

        if (!view.OffersOpenComposite && view.Problem is null)
        {
            // Why setting it failed no longer matters once it is set.
            openCompositeProblem = null;
        }

        var level = view.Problem is not null ? StatusLevel.Error : view.OffersOpenComposite ? StatusLevel.Info : StatusLevel.Ok;
        OpenVrRow = new StatusItem("OpenVR runtime", view.Runtime, level);
        CanSetOpenComposite = view.OffersOpenComposite;
        OpenVrWarning = view.Warning;
        OpenVrProblem = openCompositeProblem ?? view.Problem;
    }

    /// <summary>"Set OpenComposite": OpenComposite first for OpenVR games, until the owner or SteamVR changes it.</summary>
    [RelayCommand]
    private async Task SetOpenCompositeAsync()
    {
        CanSetOpenComposite = false;
        try
        {
            openCompositeProblem = await Task.Run(host.OpenVr.UseMonado);
        }
        catch (Exception e)
        {
            AppLog.Write("Setting OpenComposite failed", e);
            openCompositeProblem = $"Setting OpenComposite failed ({e.Message.Trim().TrimEnd('.')}); nothing changed.";
        }

        if (openCompositeProblem is not null)
        {
            AppLog.Write("Setting OpenComposite: " + openCompositeProblem);
        }

        // Through null, so that the same problem again opens the bar again after it was closed.
        OpenVrProblem = null;
        RefreshRuntime();
    }

    /// <summary>Runs the selected profile's headset checks and shows those that need a look.</summary>
    private async Task RunChecksAsync()
    {
        checking?.Cancel();
        var cts = checking = new CancellationTokenSource();
        Profile profile;
        try
        {
            profile = SelectedSet().Selected;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        IReadOnlyList<CheckResult> results = [];
        if (profile.ModuleId is { } id && host.Modules.TryGetValue(id, out var module))
        {
            try
            {
                results = await module.CheckAsync(host.Context, profile, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                AppLog.Write("The checks failed", e);
            }
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        for (var i = Notices.Count - 1; i >= 0; i--)
        {
            if (Notices[i].Key.StartsWith("check:", StringComparison.Ordinal))
            {
                Notices.RemoveAt(i);
            }
        }

        foreach (var r in results.Where(r => r.Level is StatusLevel.Warning or StatusLevel.Error))
        {
            Notices.Add(new NoticeItem(
                "check:" + r.Label,
                r.Label,
                r.Fix is null ? r.Text : $"{r.Text} {r.Fix}",
                r.Level == StatusLevel.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                IsClosable: false));
        }
    }

    /// <summary>Monado ended other than by Stop: by itself (a problem), or after the last app closed (as asked).</summary>
    private void OnEnded(string text)
    {
        if (text != "Stopped.")
        {
            var problem = text.StartsWith("Monado stopped by itself", StringComparison.Ordinal);
            ShowNotice("ended", "Monado stopped", text, problem ? InfoBarSeverity.Error : InfoBarSeverity.Informational);
        }
    }

    private void ShowNotice(string key, string title, string message, InfoBarSeverity severity)
    {
        RemoveNotice(key);
        Notices.Insert(0, new NoticeItem(key, title, message, severity, IsClosable: true));
    }

    private void RemoveNotice(string key)
    {
        for (var i = Notices.Count - 1; i >= 0; i--)
        {
            if (Notices[i].Key == key)
            {
                Notices.RemoveAt(i);
            }
        }
    }

    private void AddWarning(SessionWarning w)
    {
        var key = "warning:" + w.Key;
        var existing = Notices.FirstOrDefault(n => n.Key == key);
        var item = new NoticeItem(key, "While Monado runs", w.Text, InfoBarSeverity.Warning, IsClosable: false);
        if (existing is null)
        {
            Notices.Add(item);
        }
        else
        {
            Notices[Notices.IndexOf(existing)] = item;
        }
    }

    private void ClearWarnings()
    {
        for (var i = Notices.Count - 1; i >= 0; i--)
        {
            if (Notices[i].Key.StartsWith("warning:", StringComparison.Ordinal))
            {
                Notices.RemoveAt(i);
            }
        }
    }
}
