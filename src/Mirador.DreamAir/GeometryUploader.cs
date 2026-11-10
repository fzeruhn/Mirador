using System.Text.RegularExpressions;
using Mirador.Core;
using Mirador.Core.DirectMode;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.DreamAir;

/// <summary>How the eye geometry upload went.</summary>
/// <param name="Ok">Whether the tracker took the geometry.</param>
/// <param name="TrackerSerial">The eye tracker's serial, when the runtime said it; also on a failed upload.</param>
/// <param name="Message">For the Eye tracker status line: "Geometry uploaded", or what went wrong in plain words.</param>
/// <param name="LogPath">The runtime's output, written at every upload that ran.</param>
public sealed record GeometryResult(bool Ok, string? TrackerSerial, string Message, string LogPath);

/// <summary>
/// Uploads the eye geometry to the Dream Air's eye tracker, which forgets it at power-off; without it gaze is about
/// 10° off (Mirador.md §4.5 item 14). It runs the owner's own copy of Tobii's runtime from Pimax Play, unmodified, with
/// the variant's geometry file: the runtime opens the tracker, passes its licence check, sends the geometry and stops
/// at ENTER. A port of <c>DreamAirWin\tools\upload_geometry.ps1</c>.
/// </summary>
public sealed partial class GeometryUploader(
    Func<ProcessSpec, IConsoleProcess> start, IUsbDevices usb, IProcessList processes, AppPaths paths, TimeProvider time)
{
    public const string Uploaded = "Geometry uploaded";

    internal const string NoTracker = "The eye tracker isn't on USB. Is the headset connected and switched on?";

    private const string NotUploaded = "The eye geometry wasn't uploaded, so gaze will be about 10° off";

    /// <summary>How long the runtime gets to reach its licence check, which comes after it opened the tracker.</summary>
    internal static readonly TimeSpan LicenceWait = TimeSpan.FromSeconds(30);

    /// <summary>How long it then gets to send the geometry, before ENTER stops it.</summary>
    internal static readonly TimeSpan SendWait = TimeSpan.FromSeconds(2);

    /// <summary>How long it gets to end after ENTER before it is killed.</summary>
    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    /// <summary>How long a killed runtime gets to be gone, so that it lets go of the tracker and its copied files.</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pimax's programs that hold the eye tracker; the runtime can't open it while one runs. A <c>monado-service</c>
    /// isn't among them: the session refuses a real Monado before a Start gets here, and Mirador's own stand-in, which
    /// has that name too, runs while a game's Start uploads.
    /// </summary>
    private static readonly string[] Holders = ["pi_server", "vrss_gaze_provider", PimaxNames.EyeRuntimeProcess];

    /// <summary>
    /// Uploads the geometry of <paramref name="variant"/> from <paramref name="pimax"/>. Pimax's software and Monado must
    /// not run. Pimax Play's folder is only read: the runtime and the geometry file are copied to
    /// <c>&lt;GeometryDir&gt;\runtime\</c> and run there.
    /// </summary>
    /// <returns>Never a thrown failure: a failed upload is a result, since Monado can start without the geometry.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the runtime was ended first.</exception>
    public async Task<GeometryResult> UploadAsync(PimaxInstall pimax, string variant, CancellationToken ct)
    {
        var logPath = Path.Combine(paths.GeometryDir, "runtime.log");
        if (!EyeBlobVariants.All.Contains(variant))
        {
            return new GeometryResult(false, null, $"{NotUploaded}: Mirador doesn't know the eye geometry variant {variant}.", logPath);
        }

        try
        {
            if (!usb.Present().Contains(DreamAirIds.EyeTracker))
            {
                return new GeometryResult(false, null, NoTracker, logPath);
            }
        }
        catch (MiradorException e)
        {
            return new GeometryResult(false, null, $"{NotUploaded}: {e.Message}", logPath);
        }

        if (processes.Snapshot().FirstOrDefault(p => Holders.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) is { } holder)
        {
            return new GeometryResult(
                false, null,
                $"{NotUploaded}: {holder.Name} is running and holds the eye tracker.",
                logPath);
        }

        var lines = new List<string>();
        var notes = new List<string>();
        try
        {
            await RunAsync(pimax, variant, lines, notes, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
            notes.Add(e.Message);
        }
        finally
        {
            WriteLog(logPath, lines, notes);
        }

        List<string> output;
        lock (lines)
        {
            output = [.. lines];
        }

        var (connected, created, serial) = Evaluate(output);
        return connected && created
            ? new GeometryResult(true, serial, Uploaded, logPath)
            : new GeometryResult(false, serial, $"{NotUploaded}. Details: {logPath}", logPath);
    }

    /// <summary>
    /// Reads the runtime's output: whether it opened the tracker (<c>Opened device '…'</c>, which names its serial) and
    /// whether it created its session with the tracker (<c>tobii_xr_create successful</c>), after which the geometry
    /// is on it.
    /// </summary>
    public static (bool Connected, bool Created, string? Serial) Evaluate(IReadOnlyList<string> lines)
    {
        string? serial = null;
        var created = false;
        foreach (var line in lines)
        {
            if (serial is null && OpenedDevice().Match(line) is { Success: true } match)
            {
                serial = match.Groups[1].Value;
            }

            created |= line.Contains("tobii_xr_create successful", StringComparison.OrdinalIgnoreCase);
        }

        return (serial is not null, created, serial);
    }

    private async Task RunAsync(PimaxInstall pimax, string variant, List<string> lines, List<string> notes, CancellationToken ct)
    {
        var runtimeDir = Path.Combine(paths.GeometryDir, "runtime");
        var outputDir = Path.Combine(paths.GeometryDir, "log");
        Directory.CreateDirectory(runtimeDir);
        Directory.CreateDirectory(outputDir);

        var blob = EyeBlobVariants.FileName(variant);
        var exe = Path.Combine(runtimeDir, PimaxNames.EyeRuntimeProcess + ".exe");
        File.Copy(pimax.TobiiRuntimeExe, exe, overwrite: true);
        File.Copy(pimax.BlobPath(variant), Path.Combine(runtimeDir, blob), overwrite: true);

        // The output folder relative to the runtime's own: a user name outside the ANSI code page can't break it.
        var outputArg = Path.Combine("..", "log");
        using var process = start(new ProcessSpec(exe, $"-x {blob} \"{outputArg}\"", runtimeDir, ConsoleProcess.CurrentEnvironment()));
        var licence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Line += line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }

            if (line.Contains("validating licenses", StringComparison.OrdinalIgnoreCase))
            {
                licence.TrySetResult();
            }
        };

        try
        {
            // Its stdout holds back the line saying the session was created until it ends, so wait for the licence
            // check instead, then give it a moment to send the geometry.
            if (await WaitAsync(licence.Task, process.Exited, LicenceWait, ct).ConfigureAwait(false))
            {
                await WaitAsync(process.Exited, null, SendWait, ct).ConfigureAwait(false);
            }
            else if (!process.Exited.IsCompleted)
            {
                notes.Add($"No licence check within {LicenceWait.TotalSeconds:0} s.");
            }

            process.WriteLine("");
            process.CloseInput();
            if (!await WaitAsync(process.Exited, null, ExitWait, ct).ConfigureAwait(false))
            {
                notes.Add($"Didn't stop within {ExitWait.TotalSeconds:0} s of ENTER; ended it.");
                await KillAsync(process).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Ends the runtime and waits a moment for it to be gone, so that Monado finds the tracker free.</summary>
    private async Task KillAsync(IConsoleProcess process)
    {
        process.Kill();
        using var stop = new CancellationTokenSource();
        await Task.WhenAny(process.Exited, Task.Delay(KillWait, time, stop.Token)).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="task"/> completed within <paramref name="limit"/>; <paramref name="orElse"/> ending first counts as no.</summary>
    private async Task<bool> WaitAsync(Task task, Task? orElse, TimeSpan limit, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(limit, time, stop.Token);
        var first = await (orElse is null ? Task.WhenAny(task, delay) : Task.WhenAny(task, orElse, delay)).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return first == task;
    }

    private static void WriteLog(string logPath, List<string> lines, List<string> notes)
    {
        try
        {
            List<string> all;
            lock (lines)
            {
                all = [.. lines, .. notes.Select(note => "Mirador: " + note)];
            }

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, all);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is for details only; the result says what happened.
        }
    }

    [GeneratedRegex(@"Opened device '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex OpenedDevice();
}
