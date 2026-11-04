using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.DreamAir;

namespace Mirador.App.ViewModels;

/// <summary>A calibration copy in the list, with when it was taken.</summary>
public sealed record BackupItem(CalibrationBackup Backup)
{
    public string Text => Backup.Taken.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}

/// <summary>The Eye tracking page (Dream Air profiles only): the gaze check, and the calibrations' copies.</summary>
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

    public ObservableCollection<string> Serials { get; } = [];

    public ObservableCollection<BackupItem> Backups { get; } = [];

    [ObservableProperty]
    public partial string? SelectedSerial { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupMessage))]
    public partial string? BackupMessage { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity BackupSeverity { get; set; }

    [ObservableProperty]
    public partial bool HasSerials { get; set; }

    public bool HasBackupMessage => BackupMessage is not null;

    public void DismissResult() => Result = null;

    public void DismissBackupMessage() => BackupMessage = null;

    /// <summary>Puts a copy back (the page asked first).</summary>
    public void Restore(BackupItem item) => Do(() =>
    {
        host.Calibrations.Restore(item.Backup);
        return $"The calibration from {item.Text} is back. The one it replaced is kept in the list.";
    });

    /// <summary>Writes the selected tracker's calibration to <paramref name="zipPath"/> (the page picked it).</summary>
    public void Export(string zipPath) => Do(() =>
    {
        host.Calibrations.Export(SelectedSerial ?? throw new MiradorException("There is no calibration to export yet."), zipPath);
        return $"Saved {zipPath}.";
    });

    /// <summary>Takes in an export from <paramref name="zipPath"/> (the page picked it).</summary>
    public void Import(string zipPath) => Do(() =>
    {
        var serial = host.Calibrations.Import(zipPath);
        SelectedSerial = serial;
        return $"The calibration of eye tracker {serial} is in. The one it replaced is kept in the list.";
    });

    partial void OnSelectedSerialChanged(string? value) => LoadBackups();

    private void Do(Func<string> action)
    {
        try
        {
            BackupMessage = action();
            BackupSeverity = InfoBarSeverity.Success;
        }
        catch (MiradorException e)
        {
            BackupMessage = e.Message;
            BackupSeverity = InfoBarSeverity.Error;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            BackupMessage = $"Mirador couldn't do that ({e.Message.Trim().TrimEnd('.')}).";
            BackupSeverity = InfoBarSeverity.Error;
        }

        LoadSerials();
    }

    private void LoadSerials()
    {
        IReadOnlyList<string> serials;
        try
        {
            serials = host.Calibrations.Serials();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            serials = [];
        }

        var keep = SelectedSerial;
        Serials.Clear();
        foreach (var serial in serials)
        {
            Serials.Add(serial);
        }

        HasSerials = Serials.Count > 0;
        SelectedSerial = Serials.Contains(keep ?? "") ? keep : Serials.FirstOrDefault();
        LoadBackups();
    }

    private void LoadBackups()
    {
        Backups.Clear();
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        try
        {
            foreach (var backup in host.Calibrations.Backups(serial))
            {
                Backups.Add(new BackupItem(backup));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

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

        LoadSerials();
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
