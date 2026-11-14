using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Core.Build;
using Mirador.Core.Profiles;

namespace Mirador.App.ViewModels;

/// <summary>
/// The Profiles page's build card for a branch profile (Mirador.md §4.11 item 49): what was built last (the commit,
/// the compiler, the checks), "Build" or "Pull and rebuild", Cancel, and the build's log as it runs.
/// </summary>
public sealed partial class BuildViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue ui;
    private Profile? profile;

    public BuildViewModel(AppHost host, Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        this.host = host;
        this.ui = ui;
    }

    /// <summary>Whether the card shows: the saved profile builds a branch.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(CancelCommand))]
    public partial bool IsBuilding { get; set; }

    /// <summary>The last build: its commit and checks, or that there is none yet.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string BuildLabel { get; set; } = "Build";

    /// <summary>The running or last build's lines.</summary>
    [ObservableProperty]
    public partial string Log { get; set; } = "";

    /// <summary>Whether the profile has unsaved changes: it is built as saved, so Build waits for Save.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial bool Unsaved { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    public partial bool MonadoRuns { get; set; }

    public void Attach() => host.Builds.Changed += OnChanged;

    public void Detach() => host.Builds.Changed -= OnChanged;

    /// <summary>Shows the saved <paramref name="saved"/> profile (null: none).</summary>
    public void Show(Profile? saved)
    {
        profile = saved?.Monado.Kind == SourceKind.Branch ? saved : null;
        IsVisible = profile is not null;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private void Build()
    {
        if (host.Verifying)
        {
            Status = "Verify build for Windows is running: build when it is done.";
            return;
        }

        if (host.BuildToolsSetup.IsInstalling)
        {
            Status = "The build tools are being installed: build when that is done.";
            return;
        }

        if (profile is not null)
        {
            host.Builds.Start(profile);
        }
    }

    private bool CanBuild() => profile is not null && !IsBuilding && !Unsaved && !MonadoRuns;

    [RelayCommand(CanExecute = nameof(IsBuilding))]
    private void Cancel()
    {
        if (profile is not null)
        {
            host.Builds.Cancel(profile.Id);
        }
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (profile is not null && host.Builds.LastLog(profile.Id) is { } log)
        {
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        }
    }

    private void OnChanged(string profileId)
    {
        if (profileId == profile?.Id)
        {
            ui.TryEnqueue(Refresh);
        }
    }

    private void Refresh()
    {
        if (profile is null)
        {
            return;
        }

        var run = host.Builds.RunOf(profile.Id);
        IsBuilding = run is { Outcome: null, Cancelled: false };
        Log = run is null ? "" : string.Join(Environment.NewLine, run.Lines);
        var installDir = host.Locator.BranchInstallDir(profile.Id);
        var info = BuildInfo.Load(installDir);
        BuildLabel = info is null ? "Build" : "Pull and rebuild";
        if (IsBuilding)
        {
            Status = "Building. This takes longer the first time.";
            return;
        }

        var built = info is null
            ? "Not built yet."
            : $"Built {info.ShortCommit} on {info.BuiltAt} with {info.Compiler}. {BuildCheckResult.Load(installDir)?.Summary()}";
        Status = run switch
        {
            { Cancelled: true } => $"Cancelled. {built}",
            { Outcome: { Ok: false } failed } => $"{failed.Message} {built}",
            _ => built,
        };
    }
}
