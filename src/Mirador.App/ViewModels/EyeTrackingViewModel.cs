using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Pimax;

namespace Mirador.App.ViewModels;

/// <summary>An eye geometry variant in the list; the tested one says so.</summary>
public sealed record VariantItem(string Id)
{
    public override string ToString() => Id == EyeBlobVariants.Tested ? $"{Id} (tested)" : Id;
}

/// <summary>A calibration copy in the list, with when it was taken.</summary>
public sealed record BackupItem(CalibrationBackup Backup)
{
    public string Text => Backup.Taken.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}

/// <summary>The Eye tracking page (Dream Air profiles only): the eye geometry variant, and the calibrations' copies.</summary>
public sealed partial class EyeTrackingViewModel : ObservableObject
{
    private readonly AppHost host;

    public EyeTrackingViewModel(AppHost host)
    {
        this.host = host;
        Refresh();
    }

    /// <summary>The variant card's description: where the variant came from, and why it matters.</summary>
    public string VariantDescription =>
        $"{VariantSource.TrimEnd('.')}. The eye tracker's geometry differs between variants; Mirador uploads the one for yours at every Start.";

    public IReadOnlyList<VariantItem> Variants { get; } = [.. EyeBlobVariants.All.Select(v => new VariantItem(v))];

    [ObservableProperty]
    public partial VariantItem? SelectedVariant { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VariantDescription))]
    public partial string VariantSource { get; set; } = "";

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

    public void DismissBackupMessage() => BackupMessage = null;

    [ObservableProperty]
    public partial bool CanExport { get; set; }

    /// <summary>Follows the session while the page is shown: a session that ended may have left a new copy.</summary>
    public void Attach() => host.Session.Changed += OnSessionChanged;

    public void Detach() => host.Session.Changed -= OnSessionChanged;

    /// <summary>
    /// Puts a copy back (the page asked first). Through the session, like Import: a Start waits until the files are
    /// written, so Monado never reads half a pair.
    /// </summary>
    public Task RestoreAsync(BackupItem item) => DoWhileStoppedAsync(() =>
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
    public Task ImportAsync(string zipPath) => DoWhileStoppedAsync(() =>
    {
        var serial = host.Calibrations.Import(zipPath);
        App.Current.Ui.TryEnqueue(() => SelectedSerial = serial);
        return $"The calibration of eye tracker {serial} is in. The one it replaced is kept in the list.";
    });

    private async Task DoWhileStoppedAsync(Func<string> action)
    {
        try
        {
            BackupMessage = await host.Session.RunWhileStoppedAsync(
                PimaxModule.ModuleId, (_, _) => Task.Run(action), CancellationToken.None);
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

    private void OnSessionChanged() => App.Current.Ui.TryEnqueue(() =>
    {
        if (host.Session.State == Core.Session.SessionState.Stopped)
        {
            LoadSerials();
        }
    });

    partial void OnSelectedSerialChanged(string? value)
    {
        CanExport = value is not null && host.Calibrations.HasCalibration(value);
        LoadBackups();
    }

    /// <summary>The owner picked a variant: it is theirs from now on, until Pimax's software says otherwise.</summary>
    partial void OnSelectedVariantChanged(VariantItem? value)
    {
        if (loadingVariant || value is null)
        {
            return;
        }

        try
        {
            host.DreamAirState.Update(s => BlobChoice.Chosen(s, value.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            BackupMessage = $"Mirador couldn't save the variant ({e.Message.Trim().TrimEnd('.')}).";
            BackupSeverity = InfoBarSeverity.Error;
        }

        ShowVariant(host.DreamAirState.Load());
    }

    private bool loadingVariant;

    private void ShowVariant(DreamAirState state)
    {
        var decision = BlobChoice.Resolve(state, null);
        loadingVariant = true;
        try
        {
            // Nothing picked while unknown: the owner can then pick the tested one too.
            SelectedVariant = decision.NeedsOwner ? null : Variants.FirstOrDefault(v => v.Id == decision.Variant);
        }
        finally
        {
            loadingVariant = false;
        }

        VariantSource = decision.NeedsOwner
            ? "Not known yet: pick yours, or start Pimax Play once and Mirador reads it."
            : state.BlobSource == BlobChoice.FromPimax ? "Read from Pimax Play" : "Chosen by you";
    }

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

    /// <summary>Reads what Mirador remembers (the variant, the calibrations) again.</summary>
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
        ShowVariant(state);
    }
}
