using CommunityToolkit.Mvvm.ComponentModel;
using Mirador.App.Services;
using Mirador.Core;

namespace Mirador.App.ViewModels;

/// <summary>App settings: close to the tray and the update check; and About. The base stations' settings are on the Lighthouse page.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost host;
    private readonly bool loading;

    public SettingsViewModel(AppHost host)
    {
        this.host = host;
        loading = true;
        var settings = Read(host, out var problem);
        Problem = problem;
        CloseToTray = settings.CloseToTray;
        CheckForUpdates = settings.CheckForUpdates;
        loading = false;
    }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    public string Version => $"Mirador {AppInfo.Version}";

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

    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    partial void OnCheckForUpdatesChanged(bool value) => Save(s => s with { CheckForUpdates = value });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!loading)
        {
            Problem = Write(host, change);
        }
    }
}
