using CommunityToolkit.Mvvm.ComponentModel;
using Mirador.App.Services;
using Mirador.Core;

namespace Mirador.App.ViewModels;

/// <summary>App settings: close to the tray, the update check, the base stations with Monado; and About.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly bool loading;

    public SettingsViewModel(AppHost host)
    {
        this.host = host;
        loading = true;
        var settings = Load();
        CloseToTray = settings.CloseToTray;
        CheckForUpdates = settings.CheckForUpdates;
        BaseStationsOnAtStart = settings.BaseStationsOnAtStart;
        BaseStationsOffAtStop = settings.BaseStationsOffAtStop;
        OffMeans = settings.BaseStationsOff == OffMode.Standby ? 1 : 0;
        loading = false;
    }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool BaseStationsOnAtStart { get; set; }

    [ObservableProperty]
    public partial bool BaseStationsOffAtStop { get; set; }

    /// <summary>The "Off means" drop-down: 0 sleep, 1 standby.</summary>
    [ObservableProperty]
    public partial int OffMeans { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    public string Version => $"Mirador {AppInfo.Version}";

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s with { CheckForUpdates = value });

    partial void OnBaseStationsOnAtStartChanged(bool value) => Save(s => s with { BaseStationsOnAtStart = value });

    partial void OnBaseStationsOffAtStopChanged(bool value) => Save(s => s with { BaseStationsOffAtStop = value });

    partial void OnOffMeansChanged(int value) =>
        Save(s => s with { BaseStationsOff = value == 1 ? OffMode.Standby : OffMode.Sleep });

    private AppSettings Load()
    {
        try
        {
            return host.Settings.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Problem = $"Mirador couldn't read its settings ({e.Message.Trim().TrimEnd('.')}).";
            return new AppSettings();
        }
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (loading)
        {
            return;
        }

        try
        {
            host.Settings.Save(change(host.Settings.Load()));
            Problem = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Problem = $"Mirador couldn't save its settings ({e.Message.Trim().TrimEnd('.')}).";
        }
    }
}
