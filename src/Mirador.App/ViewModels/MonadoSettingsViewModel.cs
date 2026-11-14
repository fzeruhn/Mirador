using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core.Monado;
using Mirador.Core.Session;

namespace Mirador.App.ViewModels;

/// <summary>
/// The Monado settings every profile shares (Mirador.md §4.3 item 10). A change is saved once the slider is let go
/// (or a moment after the last change, whichever comes first), never at every step.
/// </summary>
public sealed partial class MonadoSettingsViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly DispatcherQueueTimer saveSoon;
    private bool loading;
    private bool loaded;

    public MonadoSettingsViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        saveSoon = ui.CreateTimer();
        saveSoon.Interval = TimeSpan.FromSeconds(1);
        saveSoon.IsRepeating = false;
        saveSoon.Tick += (_, _) => Save();
        Load();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LighthouseWaitText))]
    public partial double LighthouseWaitSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenderScaleText))]
    public partial double RenderScale { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompositorBudgetText))]
    public partial double CompositorBudget { get; set; }

    [ObservableProperty]
    public partial bool StopWhenLastAppCloses { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public string LighthouseWaitText => $"{LighthouseWaitSeconds:0} s";

    public string RenderScaleText => $"{RenderScale:0} %";

    public string CompositorBudgetText => $"{CompositorBudget:0} ms";

    public bool HasProblem => Problem is not null;

    /// <summary>Follows Monado's state while the page is open.</summary>
    public void Attach()
    {
        host.Session.Changed += OnSessionChanged;
        IsRunning = host.Session.State != SessionState.Stopped;
        Load();
    }

    public void Detach()
    {
        host.Session.Changed -= OnSessionChanged;
        Save();
    }

    /// <summary>Saves what the controls show now: a slider was let go, or the toggle flipped.</summary>
    public void Save()
    {
        saveSoon.Stop();
        if (loading || !loaded)
        {
            return; // what the controls show isn't the user's settings until they have been read
        }

        var settings = Current();
        try
        {
            if (settings != host.MonadoSettings.Load())
            {
                host.MonadoSettings.Save(settings);
            }

            Problem = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Problem = $"Mirador couldn't save the Monado settings ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    /// <summary>Back to Monado's and Mirador's defaults (the page asks first).</summary>
    public void ResetToDefaults()
    {
        Show(new MonadoSettings());
        loaded = true;
        Save();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        Save();
        await host.Session.StopAsync(CancellationToken.None, restart: true);
        var outcome = await App.Current.StartAsync(StartReason.Button);
        Problem = outcome.Ok ? null : $"Monado didn't start again: {outcome.Message}";
    }

    partial void OnLighthouseWaitSecondsChanged(double value) => SaveSoon();

    partial void OnRenderScaleChanged(double value) => SaveSoon();

    partial void OnCompositorBudgetChanged(double value) => SaveSoon();

    partial void OnStopWhenLastAppClosesChanged(bool value) => Save();

    private void SaveSoon()
    {
        if (!loading)
        {
            saveSoon.Stop();
            saveSoon.Start();
        }
    }

    private MonadoSettings Current() => new MonadoSettings(
        (int)Math.Round(LighthouseWaitSeconds) * 1000,
        (int)Math.Round(RenderScale),
        (int)Math.Round(CompositorBudget),
        StopWhenLastAppCloses).Clamped();

    private void Load()
    {
        try
        {
            Show(host.MonadoSettings.Load());
            loaded = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Problem = $"Mirador couldn't read the Monado settings ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    private void Show(MonadoSettings s)
    {
        loading = true;
        try
        {
            LighthouseWaitSeconds = s.LighthouseWaitMs / 1000.0;
            RenderScale = s.RenderScalePercent;
            CompositorBudget = s.CompositorMinTimeMs;
            StopWhenLastAppCloses = s.StopWhenLastAppCloses;
        }
        finally
        {
            loading = false;
        }
    }

    private void OnSessionChanged() =>
        App.Current.Ui.TryEnqueue(() => IsRunning = host.Session.State != SessionState.Stopped);
}
