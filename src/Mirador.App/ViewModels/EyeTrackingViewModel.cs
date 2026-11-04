using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.DreamAir;

namespace Mirador.App.ViewModels;

/// <summary>The Eye tracking page (Dream Air profiles only): the gaze check.</summary>
public sealed partial class EyeTrackingViewModel : ObservableObject
{
    private readonly AppHost host;

    public EyeTrackingViewModel(AppHost host)
    {
        this.host = host;
        Refresh();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCheckCommand))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial string? Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity ResultSeverity { get; set; }

    [ObservableProperty]
    public partial string LastCheck { get; set; } = "";

    public bool HasResult => Result is not null;

    public void DismissResult() => Result = null;

    /// <summary>Reads what Mirador remembers (the last check) again.</summary>
    public void Refresh()
    {
        DreamAirState state;
        try
        {
            state = host.DreamAirState.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            state = new DreamAirState();
        }

        LastCheck = state.LastCheck is { } last
            ? $"Last check {last.When.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}: {CalibrationCheck.Describe(last.MedianDeg, last.WorstDotDeg, last.Dots).Text}"
            : "Not checked yet.";
    }

    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task RunCheckAsync()
    {
        IsChecking = true;
        Result = null;
        try
        {
            var progress = new Progress<string>(text => Progress = text);
            var result = await host.CalibrationCheck.RunAsync(host.Session, host.GazeWorldExe, host.Paths, progress, CancellationToken.None);
            var (text, good) = CalibrationCheck.Describe(result.MedianDeg, result.WorstDotDeg, result.Dots);
            Result = text;
            ResultSeverity = good ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        }
        catch (MiradorException e)
        {
            Result = e.Message;
            ResultSeverity = InfoBarSeverity.Error;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("The gaze check failed", e);
            Result = $"The gaze check failed ({e.Message.Trim().TrimEnd('.')}).";
            ResultSeverity = InfoBarSeverity.Error;
        }
        finally
        {
            IsChecking = false;
            Progress = null;
            Refresh();
        }
    }

    private bool CanRunCheck() => !IsChecking;
}
