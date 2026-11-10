using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Build;

namespace Mirador.App.ViewModels;

/// <summary>One row of the "Check build tools" page.</summary>
public sealed record BuildToolPartItem(string Name, string Detail, string State, string Glyph);

/// <summary>
/// The "Check build tools" page (Mirador.md §4.11 item 49): each part found, missing or fixable, and one "Install
/// what's missing" button.
/// </summary>
public sealed partial class BuildToolsViewModel(AppHost host, Microsoft.UI.Dispatching.DispatcherQueue ui) : ObservableObject
{
    private static BuildToolsViewModel? shared;

    private BuildToolsReport? report;

    /// <summary>
    /// The one view model of the page, kept while Mirador runs: an install goes on after Back, and the page shows it,
    /// and how it ended, when it is opened again.
    /// </summary>
    public static BuildToolsViewModel Shared => shared ??= new BuildToolsViewModel(App.Current.Host, App.Current.Ui);

    /// <summary>Whether the page is shown: an install that ends while it isn't says so in a notification.</summary>
    public bool IsShown { get; set; }

    public ObservableCollection<BuildToolPartItem> Parts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool HasMissing { get; set; }

    /// <summary>What the last check or install came to.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity SummarySeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial bool HasSummary { get; set; }

    /// <summary>The lines of the last install.</summary>
    [ObservableProperty]
    public partial string Log { get; set; } = "";

    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task CheckAsync()
    {
        IsBusy = true;
        try
        {
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (report is null)
        {
            return;
        }

        if (host.Builds.AnyRunning)
        {
            Show(InfoBarSeverity.Warning, "A Monado build is running: install the build tools when it is done.");
            return;
        }

        IsBusy = true;
        Log = "";
        HasSummary = false;
        try
        {
            var failures = await Task.Run(() => host.BuildToolsSetup.InstallMissingAsync(report, Add, CancellationToken.None));
            await RefreshAsync();
            if (failures.Count > 0)
            {
                Show(InfoBarSeverity.Error, string.Join(' ', failures));
            }

            if (!IsShown)
            {
                Notifications.Show(
                    failures.Count == 0 ? "The build tools are installed" : "Some build tools weren't installed",
                    failures.Count == 0 ? "Mirador can build Monado branches now." : failures[0]);
            }
        }
        catch (MiradorException e)
        {
            Show(InfoBarSeverity.Error, e.Message);
            if (!IsShown)
            {
                Notifications.Show("The build tools weren't installed", e.Message);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall() => !IsBusy && HasMissing;

    private async Task RefreshAsync()
    {
        try
        {
            report = await Task.Run(() => host.BuildToolsSetup.CheckAsync(CancellationToken.None));
        }
        catch (MiradorException e)
        {
            Show(InfoBarSeverity.Error, e.Message);
            return;
        }

        Parts.Clear();
        foreach (var part in report.Parts)
        {
            Parts.Add(new BuildToolPartItem(part.Name, part.Detail, part.State switch
            {
                ToolState.Found => "Found",
                ToolState.Fixable => "Can be fixed",
                _ => "Missing",
            }, part.State switch
            {
                ToolState.Found => "",
                ToolState.Fixable => "",
                _ => "",
            }));
        }

        HasMissing = !report.AllFound;
        if (report.AllFound)
        {
            Show(InfoBarSeverity.Success, "Everything a branch build needs is here.");
        }
        else if (!HasSummary || SummarySeverity != InfoBarSeverity.Error)
        {
            Show(InfoBarSeverity.Informational, "\"Install what's missing\" gets the rest: Mirador's tools from GitHub, Visual Studio's parts through its installer.");
        }
    }

    private void Show(InfoBarSeverity severity, string text)
    {
        SummarySeverity = severity;
        Summary = text;
        HasSummary = true;
    }

    private void Add(string line)
    {
        AppLog.Write("Build tools: " + line);
        ui.TryEnqueue(() => Log = Log.Length == 0 ? line : Log + Environment.NewLine + line);
    }
}
