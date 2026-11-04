using Mirador.Core;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;

namespace Mirador.DreamAir;

/// <summary>
/// What the Dream Air needs on this PC, checked before each Start and for the first-run checks and Home: an NVIDIA
/// GPU, Pimax Play's eye tracking runtime and eye geometry, SteamVR's Lighthouse driver and the headset itself. Each
/// run looks again, so a moved or missing file after an update is found (Mirador.md §4.4 item 12).
/// </summary>
/// <param name="reg">The registry, to find Pimax Play.</param>
/// <param name="usb">The USB devices, to find the headset's parts.</param>
/// <param name="fileExists">Whether a file is there.</param>
/// <param name="nvidiaWindowsVersion">The NVIDIA driver's Windows version, or null without an NVIDIA GPU (<see cref="NvidiaDriver.CurrentWindowsVersion"/>).</param>
/// <param name="steamVrDir">SteamVR's folder, or null when it isn't installed (<see cref="FindSteamVr"/>).</param>
public sealed class DreamAirChecks(
    IRegistryReader reg, IUsbDevices usb, Func<string, bool> fileExists, Func<string?> nvidiaWindowsVersion, Func<string?> steamVrDir)
{
    private const string InstallPimax = "Install Pimax Play: Mirador uses its eye tracking runtime.";
    private const string RepairPimax = "Repair or update Pimax Play.";

    /// <summary>The headset's parts, in the order they are named when missing.</summary>
    private static readonly (UsbId Id, string Name)[] Parts =
    [
        (DreamAirIds.Board, "headset board"),
        (DreamAirIds.EyeTracker, "eye tracker"),
        (DreamAirIds.Receiver, "Lighthouse receiver"),
    ];

    /// <summary>
    /// Runs every check, in this order: NVIDIA GPU, Pimax Play, Eye geometry, SteamVR, Headset. A missing headset is a
    /// warning; everything else that fails is an error, which stops a Start. Never throws for a check that fails. Blocks
    /// while Windows lists the graphics cards and USB devices (up to 5 s each), so call it off the UI thread.
    /// </summary>
    /// <param name="blobVariant">The eye geometry variant to look for, one of <see cref="EyeBlobVariants.All"/>; any other is an error.</param>
    public IReadOnlyList<CheckResult> Run(string blobVariant)
    {
        var pimax = PimaxInstall.Locate(reg);
        return [Nvidia(), PimaxPlay(pimax), EyeGeometry(pimax, blobVariant), SteamVrCheck(), Headset()];
    }

    /// <summary>
    /// SteamVR's folder on this PC, found as Monado finds it (<see cref="SteamVr.FindInstall"/>), or null. Never
    /// throws: Steam's <c>libraryfolders.vdf</c> reads as missing while Steam holds it open.
    /// </summary>
    public static string? FindSteamVr() =>
        SteamVr.FindInstall(
            SteamVr.SteamPathFromRegistry(), Environment.GetEnvironmentVariable("STEAMVR_PATH"), ReadTextOrNull, Directory.Exists);

    /// <summary>
    /// The file's text; null when it is missing or can't be read. A file another program holds open (Steam may be
    /// writing it) is tried three times, 100 ms apart, so that a Start isn't refused for a moment's write.
    /// </summary>
    internal static string? ReadTextOrNull(string path) => ReadTextOrNull(path, Thread.Sleep);

    /// <summary>For tests: <paramref name="sleep"/> stands in for the wait between tries.</summary>
    internal static string? ReadTextOrNull(string path, Action<TimeSpan> sleep)
    {
        const int Tries = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException) when (attempt < Tries)
            {
                sleep(TimeSpan.FromMilliseconds(100));
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private CheckResult Nvidia()
    {
        const string Label = "NVIDIA GPU";
        try
        {
            return nvidiaWindowsVersion() is { } windows
                ? new(Label, StatusLevel.Ok, $"Driver {NvidiaDriver.FromWindowsVersion(windows) ?? windows}")
                : new(Label, StatusLevel.Error, "No NVIDIA GPU found.", "Mirador's Dream Air support needs a GeForce.");
        }
        catch (MiradorException e)
        {
            return new(Label, StatusLevel.Error, e.Message);
        }
    }

    private CheckResult PimaxPlay(PimaxInstall? pimax)
    {
        const string Label = "Pimax Play";
        if (pimax is null)
        {
            return new(Label, StatusLevel.Error, "Pimax Play isn't installed.", InstallPimax);
        }

        return fileExists(pimax.TobiiRuntimeExe)
            ? new(Label, StatusLevel.Ok, pimax.Version ?? "Installed")
            : new(Label, StatusLevel.Error, "Pimax Play's eye tracking runtime is missing.", RepairPimax);
    }

    private CheckResult EyeGeometry(PimaxInstall? pimax, string variant)
    {
        const string Label = "Eye geometry";
        if (!EyeBlobVariants.All.Contains(variant))
        {
            // dreamair.json edited by hand, or written by a newer Mirador.
            return new(Label, StatusLevel.Error, $"Mirador doesn't know the eye geometry variant {variant}.", "Pick your headset's variant in Eye tracking.");
        }

        var file = EyeBlobVariants.FileName(variant);
        if (pimax is null)
        {
            return new(Label, StatusLevel.Error, $"{file} comes with Pimax Play, which isn't installed.", InstallPimax);
        }

        return fileExists(pimax.BlobPath(variant))
            ? new(Label, StatusLevel.Ok, $"config {variant}")
            : new(Label, StatusLevel.Error, $"{file} is missing from Pimax Play.", RepairPimax);
    }

    private CheckResult SteamVrCheck()
    {
        const string Label = "SteamVR";
        if (steamVrDir() is not { } dir)
        {
            return new(
                Label, StatusLevel.Error, "SteamVR isn't installed.",
                "Install SteamVR from Steam. It doesn't need to run: Mirador uses its Lighthouse driver.");
        }

        return fileExists(SteamVr.DriverLighthouse(dir))
            ? new(Label, StatusLevel.Ok, "Lighthouse driver found")
            : new(
                Label, StatusLevel.Error, "SteamVR's Lighthouse driver is missing.",
                "Repair SteamVR: in Steam, open its Properties, then Installed Files, and verify its files.");
    }

    private CheckResult Headset()
    {
        const string Label = "Headset";
        IReadOnlySet<UsbId> present;
        try
        {
            present = usb.Present();
        }
        catch (MiradorException e)
        {
            return new(Label, StatusLevel.Warning, e.Message);
        }

        var missing = Parts.Where(part => !present.Contains(part.Id)).Select(part => part.Name).ToList();
        return missing.Count == 0
            ? new(Label, StatusLevel.Ok, "Connected")
            : new(Label, StatusLevel.Warning, $"Not connected: {string.Join(", ", missing)}.", "Plug the headset in and switch it on.");
    }
}
