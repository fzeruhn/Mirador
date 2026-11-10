using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;

namespace Mirador.App.ViewModels;

/// <summary>
/// The profile's "Verify build for Windows" card (Mirador.md §4.11 item 49): the last result for the profile's build
/// as it is now (kept by its commit), Verify and Cancel. Its elevation result shows by "Run as administrator" too.
/// </summary>
public sealed partial class VerifyViewModel(AppHost host, Microsoft.UI.Dispatching.DispatcherQueue ui) : ObservableObject
{
    private Profile? profile;
    private MonadoInstall? install;
    private string? key;
    private CancellationTokenSource? running;

    /// <summary>A verify run ended (Run as administrator's note may change).</summary>
    public event Action? Changed;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    public partial bool MonadoRuns { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    public partial bool Unsaved { get; set; }

    /// <summary>The last result in one line, or that there is none for this build.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>Each step of the last result, or the running one's lines.</summary>
    [ObservableProperty]
    public partial string Details { get; set; } = "";

    /// <summary>What the last result says of the elevation fix, for Run as administrator; null when it didn't check.</summary>
    public string? ElevationNote { get; private set; }

    public void Attach() => host.Builds.Changed += OnBuildChanged;

    public void Detach() => host.Builds.Changed -= OnBuildChanged;

    // A new build is a new key: the card shows that build's result, or none.
    private void OnBuildChanged(string profileId)
    {
        if (profileId == profile?.Id && !IsBusy)
        {
            ui.TryEnqueue(() => Show(profile));
        }
    }

    /// <summary>Shows the saved <paramref name="saved"/> profile (null: none).</summary>
    public void Show(Profile? saved)
    {
        profile = saved;
        install = null;
        key = null;
        if (saved is not null)
        {
            try
            {
                install = host.Locator.Locate(saved);
                key = VerifyBuild.KeyFor(install);
            }
            catch (MiradorException)
            {
                // No Monado yet: the card says so.
            }
        }

        IsVisible = saved is not null;
        Refresh();
        VerifyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        if (profile is null)
        {
            return;
        }

        if (host.Builds.AnyRunning)
        {
            Status = "A Monado build is running: verify when it is done.";
            return;
        }

        // The build as it is now (it may have been rebuilt since the card was shown).
        Show(profile);
        if (install is null || key is null)
        {
            return;
        }

        var (target, targetKey, targetProfile) = (install, key, profile);
        IsBusy = true;
        host.Verifying = true;
        Details = "";
        Status = "Verifying (about a minute)…";
        running = new CancellationTokenSource();
        try
        {
            host.Session.PlaceStandIn(targetProfile);
            var result = await Task.Run(() => host.Session.RunWhileStoppedAsync(
                "",
                (_, ct) => host.Verify.RunAsync(target, targetKey, new VerifyServices(host, targetProfile, Add), Add, ct),
                running.Token));
            result.Save(host.VerifyResultFile(targetProfile.Id));
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled: Monado was stopped again.";
            return;
        }
        catch (MiradorException e)
        {
            Status = e.Message;
            return;
        }
        catch (Exception e)
        {
            AppLog.Write("Verify failed", e);
            Status = $"Verify stopped: {e.Message} Mirador's log has the details.";
            return;
        }
        finally
        {
            running.Dispose();
            running = null;
            host.Verifying = false;
            IsBusy = false;
        }

        Refresh();
        Changed?.Invoke();
    }

    private bool CanVerify() => install is not null && !IsBusy && !MonadoRuns && !Unsaved;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => running?.Cancel();

    private void Refresh()
    {
        ElevationNote = null;
        if (profile is null)
        {
            return;
        }

        if (install is null)
        {
            Status = "This profile has no Monado yet.";
            Details = "";
            return;
        }

        var result = VerifyResult.Load(host.VerifyResultFile(profile.Id));
        if (result is null || result.Key != key)
        {
            Status = "Not verified yet for this build. Verify opens sessions with Monado headless (no headset needed), a game's start through the stand-in, and Monado as administrator.";
            Details = "";
            return;
        }

        Status = $"{result.Summary()} Verified {result.At.ToLocalTime():g}.";
        Details = string.Join(Environment.NewLine, result.Steps.Select(s => $"{s.Outcome switch
        {
            VerifyOutcome.Passed => "Passed",
            VerifyOutcome.Failed => "Failed",
            _ => "Skipped",
        }}: {s.Name}. {s.Detail}"));
        ElevationNote = result.HasElevationFix switch
        {
            true => "Verify: games running as you connect to this build running as administrator (it has the elevation fix).",
            false => "Verify: games running as you couldn't connect to this build running as administrator (no elevation fix?).",
            null => null,
        };
    }

    private void Add(string line)
    {
        AppLog.Write("Verify: " + line);
        ui.TryEnqueue(() => Details = Details.Length == 0 ? line : Details + Environment.NewLine + line);
    }
}
