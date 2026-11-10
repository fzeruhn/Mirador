using System.IO.Compression;
using Microsoft.Extensions.Time.Testing;
using Mirador.Core;

namespace Mirador.Pimax.Tests;

public sealed class CalibrationStoreTests : IDisposable
{
    private const string Serial = "XR5DA-0123456789";

    private readonly TempDir dir = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)) { };
    private bool running;

    public CalibrationStoreTests() => time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose() => dir.Dispose();

    private string MonadoDir => Path.Combine(dir.Path, "monado", "dreamair");

    private string BackupsDir => Path.Combine(dir.Path, "Mirador", "calibration-backups");

    private CalibrationStore Store() => new(MonadoDir, BackupsDir, time, () => running);

    private void Calibrate(string calibration, string? correction = null, string serial = Serial)
    {
        Directory.CreateDirectory(MonadoDir);
        File.WriteAllText(Path.Combine(MonadoDir, CalibrationStore.CalibrationName(serial)), calibration);
        var correctionPath = Path.Combine(MonadoDir, CalibrationStore.CorrectionName(serial));
        if (correction is null)
        {
            File.Delete(correctionPath);
        }
        else
        {
            File.WriteAllText(correctionPath, correction);
        }
    }

    private string Current() => File.ReadAllText(Path.Combine(MonadoDir, CalibrationStore.CalibrationName(Serial)));

    private string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(dir.Path, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }

    [Fact]
    public void A_snapshot_only_when_the_pair_changed()
    {
        var store = Store();
        Calibrate("cal 1", "corr 1");

        var first = store.SnapshotIfChanged(Serial);
        time.Advance(TimeSpan.FromMinutes(1));
        var same = store.SnapshotIfChanged(Serial);
        Calibrate("cal 1"); // the correction went: that is a change too
        time.Advance(TimeSpan.FromMinutes(1));
        var changed = store.SnapshotIfChanged(Serial);

        Assert.NotNull(first);
        Assert.Null(same);
        Assert.NotNull(changed);
        Assert.Equal(Path.Combine(BackupsDir, Serial, "20261104-120000"), first.Dir);
        Assert.Equal([changed.Dir, first.Dir], store.Backups(Serial).Select(b => b.Dir));
        Assert.Equal([Serial], store.Serials());
    }

    [Fact]
    public void No_calibration_no_snapshot()
    {
        Assert.Null(Store().SnapshotIfChanged(Serial));
    }

    [Fact]
    public void Two_snapshots_in_one_second_both_stay()
    {
        var store = Store();
        Calibrate("a");
        store.SnapshotIfChanged(Serial);
        Calibrate("b");

        var second = store.SnapshotIfChanged(Serial);

        Assert.EndsWith("20261104-120000-2", second!.Dir);
        Assert.Equal(2, store.Backups(Serial).Count);
    }

    [Fact]
    public void Restore_keeps_the_replaced_pair()
    {
        var store = Store();
        Calibrate("mine", "my correction");
        var mine = store.SnapshotIfChanged(Serial)!;
        time.Advance(TimeSpan.FromMinutes(5));
        Calibrate("pimax's"); // the tracker's calibration replaced it, without a correction

        store.Restore(mine);

        Assert.Equal("mine", Current());
        Assert.Equal("my correction", File.ReadAllText(Path.Combine(MonadoDir, CalibrationStore.CorrectionName(Serial))));
        var kept = store.Backups(Serial)[0];
        Assert.Equal("pimax's", File.ReadAllText(Path.Combine(kept.Dir, CalibrationStore.CalibrationName(Serial))));
    }

    [Fact]
    public void Restore_without_a_correction_takes_the_current_one_away()
    {
        var store = Store();
        Calibrate("plain");
        var plain = store.SnapshotIfChanged(Serial)!;
        time.Advance(TimeSpan.FromMinutes(1));
        Calibrate("newer", "newer correction");

        store.Restore(plain);

        Assert.False(File.Exists(Path.Combine(MonadoDir, CalibrationStore.CorrectionName(Serial))));
    }

    [Fact]
    public void Restore_while_monado_runs_is_refused()
    {
        var store = Store();
        Calibrate("mine");
        var mine = store.SnapshotIfChanged(Serial)!;
        Calibrate("other");
        running = true;

        var e = Assert.Throws<MiradorException>(() => store.Restore(mine));

        Assert.Equal("Stop Monado first.", e.Message);
        Assert.Equal("other", Current());
    }

    [Fact]
    public void Export_and_import_round_trip()
    {
        var store = Store();
        Calibrate("mine", "my correction");
        var zip = Path.Combine(dir.Path, "export.zip");
        store.Export(Serial, zip);
        Calibrate("something else");
        time.Advance(TimeSpan.FromMinutes(1));

        var serial = store.Import(zip);

        Assert.Equal(Serial, serial);
        Assert.Equal("mine", Current());
        Assert.Equal("my correction", File.ReadAllText(Path.Combine(MonadoDir, CalibrationStore.CorrectionName(Serial))));
        Assert.Equal("something else", File.ReadAllText(Path.Combine(store.Backups(Serial)[0].Dir, CalibrationStore.CalibrationName(Serial))));
    }

    [Fact]
    public void Import_of_a_lone_calibration_works()
    {
        Assert.Equal("ABC", Store().Import(Zip(("eye_calibration_ABC.bin", "x"))));
    }

    [Theory]
    [InlineData("..\\evil.bin")]
    [InlineData("eye_calibration_..\\evil.bin")]
    [InlineData("eye_calibration_../evil.bin")]
    [InlineData("eye_calibration_.bin")]
    public void Import_refuses_names_that_arent_an_export(string name)
    {
        var e = Assert.Throws<MiradorException>(() => Store().Import(Zip((name, "x"))));

        Assert.Equal(CalibrationStore.NotAnExport, e.Message);
        Assert.False(Directory.Exists(MonadoDir));
    }

    [Fact]
    public void Import_refuses_an_extra_file()
    {
        var e = Assert.Throws<MiradorException>(() =>
            Store().Import(Zip(("eye_calibration_ABC.bin", "x"), ("notes.txt", "hello"))));

        Assert.Equal(CalibrationStore.NotAnExport, e.Message);
    }

    [Fact]
    public void Import_refuses_two_calibrations()
    {
        var e = Assert.Throws<MiradorException>(() =>
            Store().Import(Zip(("eye_calibration_ABC.bin", "x"), ("eye_calibration_DEF.bin", "y"))));

        Assert.Equal(CalibrationStore.NotAnExport, e.Message);
    }

    [Fact]
    public void A_copy_cut_short_isnt_listed()
    {
        Directory.CreateDirectory(Path.Combine(BackupsDir, Serial, "20261104-120000.partial"));
        File.WriteAllText(Path.Combine(BackupsDir, Serial, "20261104-120000.partial", CalibrationStore.CalibrationName(Serial)), "half");

        Assert.Empty(Store().Backups(Serial));
    }

    [Fact]
    public void A_tracker_with_only_copies_has_no_calibration_to_export()
    {
        var store = Store();
        Calibrate("mine");
        store.SnapshotIfChanged(Serial);
        File.Delete(Path.Combine(MonadoDir, CalibrationStore.CalibrationName(Serial)));

        Assert.Equal([Serial], store.Serials());
        Assert.False(store.HasCalibration(Serial));
    }

    [Fact]
    public void Import_refuses_mismatched_serials()
    {
        var e = Assert.Throws<MiradorException>(() =>
            Store().Import(Zip(("eye_calibration_ABC.bin", "x"), ("eye_correction_DEF.json", "{}"))));

        Assert.Equal(CalibrationStore.NotAnExport, e.Message);
    }

    [Fact]
    public void Import_refuses_a_file_that_isnt_a_zip()
    {
        var path = Path.Combine(dir.Path, "not.zip");
        File.WriteAllText(path, "hello");

        Assert.Equal(CalibrationStore.NotAnExport, Assert.Throws<MiradorException>(() => Store().Import(path)).Message);
    }

    [Fact]
    public void Import_while_monado_runs_is_refused()
    {
        running = true;

        Assert.Equal("Stop Monado first.", Assert.Throws<MiradorException>(() => Store().Import(Zip(("eye_calibration_ABC.bin", "x")))).Message);
    }

    [Fact]
    public void Export_without_a_calibration_says_so()
    {
        Assert.Throws<MiradorException>(() => Store().Export(Serial, Path.Combine(dir.Path, "x.zip")));
    }
}
