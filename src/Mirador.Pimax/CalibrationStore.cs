using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mirador.Core;

namespace Mirador.Pimax;

/// <summary>A kept copy of one eye tracker's calibration (and its gaze correction, when there was one).</summary>
public sealed record CalibrationBackup(string Serial, DateTimeOffset Taken, string Dir);

/// <summary>
/// The eye calibrations Monado uses (Mirador.md §3, §4.5 item 17): <c>eye_calibration_&lt;serial&gt;.bin</c> and
/// <c>eye_correction_&lt;serial&gt;.json</c> in <c>%LOCALAPPDATA%\monado\dreamair</c>. Every pair that changed is kept in
/// <c>&lt;backups&gt;\&lt;serial&gt;\&lt;yyyyMMdd-HHmmss&gt;\</c>, so a calibration the tracker replaced (Pimax Play's) can
/// be put back. Restoring and importing keep the pair they replace first, and wait for Monado to stop.
/// </summary>
/// <param name="monadoDir">Where Monado keeps the files.</param>
/// <param name="backupsDir">Where Mirador keeps the copies.</param>
/// <param name="time">The clock, for the copies' names.</param>
/// <param name="monadoRuns">Whether Monado runs now (it holds the files then); null: never.</param>
public sealed partial class CalibrationStore(string monadoDir, string backupsDir, TimeProvider time, Func<bool>? monadoRuns = null)
{
    public const string NotAnExport = "This isn't a Mirador calibration export.";

    private const string StopFirst = "Stop Monado first.";

    private const string PartialSuffix = ".partial";

    /// <summary>Whether Monado has a calibration of this tracker now (a tracker may have only copies left).</summary>
    public bool HasCalibration(string serial) =>
        IsSerial(serial) && File.Exists(Path.Combine(monadoDir, CalibrationName(serial)));

    /// <summary>The default: <c>%LOCALAPPDATA%\monado\dreamair</c>.</summary>
    public static string DefaultMonadoDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "monado", "dreamair");

    public static string CalibrationName(string serial) => $"eye_calibration_{serial}.bin";

    public static string CorrectionName(string serial) => $"eye_correction_{serial}.json";

    /// <summary>The trackers there is a calibration of, or a copy of one, sorted.</summary>
    public IReadOnlyList<string> Serials()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(monadoDir))
        {
            foreach (var file in Directory.EnumerateFiles(monadoDir, "eye_calibration_*.bin"))
            {
                if (CalibrationFile().Match(Path.GetFileName(file)) is { Success: true } m && IsSerial(m.Groups[1].Value))
                {
                    found.Add(m.Groups[1].Value);
                }
            }
        }

        if (Directory.Exists(backupsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(backupsDir))
            {
                var serial = Path.GetFileName(dir);
                if (IsSerial(serial) && Backups(serial).Count > 0)
                {
                    found.Add(serial);
                }
            }
        }

        return [.. found];
    }

    /// <summary>Keeps a copy of the tracker's pair when it differs from the newest copy; null when it didn't (or there is none).</summary>
    public CalibrationBackup? SnapshotIfChanged(string serial)
    {
        CheckSerial(serial);
        var calibration = Path.Combine(monadoDir, CalibrationName(serial));
        if (!File.Exists(calibration))
        {
            return null;
        }

        var hash = Hash(monadoDir, serial);
        if (Backups(serial).FirstOrDefault() is { } newest && Hash(newest.Dir, serial) == hash)
        {
            return null;
        }

        var now = time.GetLocalNow();
        var name = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(backupsDir, serial, name);
        for (var n = 2; Directory.Exists(dir); n++)
        {
            dir = Path.Combine(backupsDir, serial, $"{name}-{n}");
        }

        // Made aside and moved into place whole: a copy cut short never shows in the list as one without its correction.
        var partial = dir + PartialSuffix;
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        Directory.CreateDirectory(partial);
        File.Copy(calibration, Path.Combine(partial, CalibrationName(serial)));
        var correction = Path.Combine(monadoDir, CorrectionName(serial));
        if (File.Exists(correction))
        {
            File.Copy(correction, Path.Combine(partial, CorrectionName(serial)));
        }

        Directory.Move(partial, dir);
        return new CalibrationBackup(serial, now, dir);
    }

    /// <summary>The tracker's copies, newest first.</summary>
    public IReadOnlyList<CalibrationBackup> Backups(string serial)
    {
        CheckSerial(serial);
        var root = Path.Combine(backupsDir, serial);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var found = new List<CalibrationBackup>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.Length >= 15 && !name.EndsWith(PartialSuffix, StringComparison.Ordinal)
                && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var taken)
                && File.Exists(Path.Combine(dir, CalibrationName(serial))))
            {
                found.Add(new CalibrationBackup(serial, new DateTimeOffset(taken), dir));
            }
        }

        return [.. found.OrderByDescending(b => b.Taken).ThenByDescending(b => b.Dir, StringComparer.Ordinal)];
    }

    /// <summary>Puts a copy back, keeping the pair it replaces first.</summary>
    /// <exception cref="MiradorException">Monado runs, or the copy is gone.</exception>
    public void Restore(CalibrationBackup backup)
    {
        CheckSerial(backup.Serial);
        RefuseWhileRunning();
        var calibration = Path.Combine(backup.Dir, CalibrationName(backup.Serial));
        if (!File.Exists(calibration))
        {
            throw new MiradorException($"That copy isn't there any more ({backup.Dir}).");
        }

        SnapshotIfChanged(backup.Serial);
        Put(backup.Serial, File.ReadAllBytes(calibration), ReadIfThere(Path.Combine(backup.Dir, CorrectionName(backup.Serial))));
    }

    /// <summary>Writes the tracker's pair into a zip, to keep or move to another PC.</summary>
    /// <exception cref="MiradorException">There is no calibration of that tracker.</exception>
    public void Export(string serial, string zipPath)
    {
        CheckSerial(serial);
        var calibration = Path.Combine(monadoDir, CalibrationName(serial));
        if (!File.Exists(calibration))
        {
            throw new MiradorException($"There is no calibration of eye tracker {serial} to export.");
        }

        var temp = zipPath + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(calibration, CalibrationName(serial));
                var correction = Path.Combine(monadoDir, CorrectionName(serial));
                if (File.Exists(correction))
                {
                    archive.CreateEntryFromFile(correction, CorrectionName(serial));
                }
            }

            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// Takes in an export: a zip of exactly <c>eye_calibration_&lt;serial&gt;.bin</c> and, optionally,
    /// <c>eye_correction_&lt;serial&gt;.json</c> of the same serial. The pair it replaces is kept first.
    /// </summary>
    /// <returns>The serial.</returns>
    /// <exception cref="MiradorException">Monado runs, or the zip isn't an export (<see cref="NotAnExport"/>).</exception>
    public string Import(string zipPath)
    {
        RefuseWhileRunning();
        byte[] calibration;
        byte[]? correction = null;
        string serial;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries.ToList();
            var calibrations = entries.Where(e => CalibrationFile().IsMatch(e.FullName)).ToList();
            if (entries.Count is not (1 or 2) || calibrations.Count != 1)
            {
                throw new MiradorException(NotAnExport);
            }

            var calibrationEntry = calibrations[0];

            serial = CalibrationFile().Match(calibrationEntry.FullName).Groups[1].Value;
            if (!IsSerial(serial) || entries.Any(e => e != calibrationEntry && e.FullName != CorrectionName(serial)))
            {
                throw new MiradorException(NotAnExport);
            }

            calibration = Read(calibrationEntry);
            if (entries.FirstOrDefault(e => e != calibrationEntry) is { } correctionEntry)
            {
                correction = Read(correctionEntry);
            }
        }
        catch (InvalidDataException)
        {
            throw new MiradorException(NotAnExport);
        }

        SnapshotIfChanged(serial);
        Put(serial, calibration, correction);
        return serial;
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        // A calibration is tens of kilobytes; anything far larger isn't one.
        const long Limit = 16 * 1024 * 1024;
        if (entry.Length > Limit)
        {
            throw new MiradorException(NotAnExport);
        }

        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[]? ReadIfThere(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>A name made only of letters, digits, <c>_ . -</c>, not starting with a dot: it becomes part of paths.</summary>
    private static bool IsSerial(string serial) => SerialPattern().IsMatch(serial) && !serial.Contains("..", StringComparison.Ordinal);

    private static void CheckSerial(string serial)
    {
        if (!IsSerial(serial))
        {
            throw new ArgumentException($"Not an eye tracker serial: {serial}", nameof(serial));
        }
    }

    /// <summary>SHA-256 of the pair in <paramref name="dir"/> (a missing correction counts as such).</summary>
    private static string Hash(string dir, string serial)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(File.ReadAllBytes(Path.Combine(dir, CalibrationName(serial))));
        var correction = Path.Combine(dir, CorrectionName(serial));
        sha.AppendData(File.Exists(correction) ? [1, .. File.ReadAllBytes(correction)] : [0]);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private void RefuseWhileRunning()
    {
        if (monadoRuns?.Invoke() == true)
        {
            throw new MiradorException(StopFirst);
        }
    }

    /// <summary>Writes the pair for Monado; without a correction, a correction left from before goes, so the pair matches.</summary>
    private void Put(string serial, byte[] calibration, byte[]? correction)
    {
        Directory.CreateDirectory(monadoDir);
        WriteWhole(Path.Combine(monadoDir, CalibrationName(serial)), calibration);
        var correctionPath = Path.Combine(monadoDir, CorrectionName(serial));
        if (correction is null)
        {
            File.Delete(correctionPath);
        }
        else
        {
            WriteWhole(correctionPath, correction);
        }
    }

    private static void WriteWhole(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"^eye_calibration_(.+)\.bin$", RegexOptions.CultureInvariant)]
    private static partial Regex CalibrationFile();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();
}
