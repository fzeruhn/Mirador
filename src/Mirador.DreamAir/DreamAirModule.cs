using System.Text;
using Mirador.Core;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.DreamAir;

/// <summary>What the Dream Air module needs from the rest of Mirador.</summary>
/// <param name="Registry">The registry, to find Pimax Play.</param>
/// <param name="StartConsole">Starts the display check (<see cref="ConsoleProcess.Start"/>).</param>
/// <param name="Geometry">Uploads the eye geometry.</param>
/// <param name="State">What Mirador remembers about the Dream Air.</param>
/// <param name="BundledLayerDir">The layer that comes with Mirador (<c>{app}\layer</c>).</param>
/// <param name="NvidiaWindowsVersion">The NVIDIA driver's Windows version (<see cref="NvidiaDriver.CurrentWindowsVersion"/>).</param>
/// <param name="SteamVrDir">SteamVR's folder (<see cref="DreamAirChecks.FindSteamVr"/>).</param>
/// <param name="BlobVariant">The eye geometry variant to use for a state; <see cref="EyeBlobVariants.Tested"/> until Mirador can tell.</param>
/// <param name="Calibration">The eye calibrations' copies, taken before each Start and after each session; null: none.</param>
public sealed record DreamAirDeps(
    IRegistryReader Registry,
    Func<ProcessSpec, IConsoleProcess> StartConsole,
    GeometryUploader Geometry,
    DreamAirStateStore State,
    string BundledLayerDir,
    Func<string?> NvidiaWindowsVersion,
    Func<string?> SteamVrDir,
    Func<DreamAirState, string> BlobVariant,
    CalibrationStore? Calibration = null);

/// <summary>
/// The Pimax Dream Air: checks the PC, stops Pimax's software for the session and starts it again after, checks direct
/// mode when the NVIDIA driver changed, uploads the eye geometry, and gives Monado the layer's variables.
/// </summary>
public sealed class DreamAirModule(DreamAirDeps deps) : IHeadsetModule
{
    public const string ModuleId = "dreamair";

    /// <summary>
    /// The variables the Dream Air needs (Mirador.md §4.3): the launcher's, and the Dream Air drivers' log at info, whose
    /// lines the board and eye tracker status come from (their default, warn, leaves those out).
    /// </summary>
    public static IReadOnlyList<EnvVar> FixedEnvironment { get; } =
    [
        new("STEAMVR_LH_ENABLE", "1"),
        new("DREAMAIR_VK_LAYER", "1"),
        new("DISABLE_RTSS_LAYER", "1"),
        new("VK_LOADER_LAYERS_ALLOW", "VK_LAYER_DREAMAIR_nvapi_direct"),
        new("XRT_COMPOSITOR_FORCE_VK_DISPLAY", "0"),
        new("XRT_COMPOSITOR_USE_PRESENT_WAIT", "1"),
        new("U_PACING_COMP_PRESENT_TO_DISPLAY_OFFSET_MS", "8.9"),
        new("DREAMAIR_LOG", "info"),
    ];

    public string Id => ModuleId;

    public string DisplayName => "Pimax Dream Air";

    public bool UsesSteamVrLighthouse => true;

    public bool IsPresent(IReadOnlySet<UsbId> devices) =>
        devices.Contains(DreamAirIds.Board) || devices.Contains(DreamAirIds.EyeTracker);

    /// <summary>The checks of <see cref="DreamAirChecks"/>, then the layer's.</summary>
    /// <summary>The checks of <see cref="DreamAirChecks"/>, the layer's, and a warning while the headset's variant isn't known.</summary>
    public Task<IReadOnlyList<CheckResult>> CheckAsync(ModuleContext ctx, Profile profile, CancellationToken ct) =>
        Task.Run<IReadOnlyList<CheckResult>>(
            async () =>
            {
                await DetectVariantAsync(ctx, ct).ConfigureAwait(false);
                var state = LoadState();
                var variant = deps.BlobVariant(state);
                List<CheckResult> results = [.. Checks(ctx).Run(variant), LayerCheck(profile)];
                if (BlobChoice.Resolve(state, null).NeedsOwner)
                {
                    results.Add(new CheckResult(
                        "Headset variant",
                        StatusLevel.Warning,
                        $"Not known yet: Mirador uploads the eye geometry of {variant}, the tested one.",
                        "Pick yours in Eye tracking, or start Pimax Play once and Mirador reads it."));
                }

                return results;
            },
            ct);

    public async Task<ModuleSession> BeforeStartAsync(
        ModuleContext ctx, Profile profile, IProgress<string> progress, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct)
    {
        // Pimax's software still runs now: the last moment to read which geometry it uses.
        await DetectVariantAsync(ctx, ct).ConfigureAwait(false);
        var state = LoadState();
        var variant = deps.BlobVariant(state);

        // Everything that can refuse without changing the computer comes first.
        progress.Report("Checking the computer…");
        var checks = await Task.Run(() => Checks(ctx).Run(variant), ct).ConfigureAwait(false);
        if (checks.FirstOrDefault(c => c.Level == StatusLevel.Error) is { } failed)
        {
            throw new MiradorException(failed.Fix is null ? failed.Text : $"{failed.Text} {failed.Fix}");
        }

        var layerFolder = LayerFolder(profile);
        var layer = DreamAirLayer.Locate(layerFolder, deps.BundledLayerDir);
        var pimax = PimaxInstall.Locate(deps.Registry)
            ?? throw new MiradorException("Pimax Play isn't installed. Install Pimax Play: Mirador uses its eye tracking runtime.");

        ct.ThrowIfCancellationRequested();
        var snapshotProblem = SnapshotCalibrations(deps.Calibration);
        progress.Report("Stopping Pimax's software…");

        // Saved before asking: Mirador may be killed, or hear no answer, while the helper stops the service.
        var running = ctx.Processes.Snapshot();
        var seenRunning = ServiceRuns(running);
        if (seenRunning)
        {
            saveRecovery(DreamAirSession.Recovery(true));
        }

        // Not cancelled by Stop: Mirador must hear whether Pimax ran to start it again (the helper's own limit applies).
        var stop = await StopPimaxAsync(ctx, running).ConfigureAwait(false);

        // An answer says; a request that never arrived changed nothing; with no answer, what was seen running decides.
        var wasRunning = stop.WasRunning ?? (!stop.NotDelivered && seenRunning);
        try
        {
            if (wasRunning)
            {
                saveRecovery(DreamAirSession.Recovery(true));
            }

            if (!stop.Ok)
            {
                throw new MiradorException(stop.Error ?? "Mirador's helper service couldn't stop Pimax's software.");
            }

            saveRecovery(DreamAirSession.Recovery(wasRunning));
            ct.ThrowIfCancellationRequested();

            var driver = NvidiaDriverNow();
            if (driver != state.LastGoodDriver)
            {
                progress.Report("Checking direct mode with the new NVIDIA driver…");
                var check = await DisplayCheck.RunAsync(layer.CheckExe, deps.StartConsole, ct).ConfigureAwait(false);
                SaveCheckOutput(ctx, check);
                if (!check.Pass)
                {
                    throw new MiradorException(check.Finished
                        ? $"Direct mode doesn't work with NVIDIA driver {driver}: {Sentence(check.Message)}{(check.WhatToDo is null ? "" : " " + Sentence(Capitalized(check.WhatToDo)))}"
                        : $"Mirador couldn't check direct mode with the new NVIDIA driver {driver}: {Sentence(check.Message)} Try again.");
                }

                try
                {
                    deps.State.Update(s => s with { LastGoodDriver = driver });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Only costs a display check at the next Start.
                }
            }

            progress.Report("Uploading the eye geometry…");
            var upload = await deps.Geometry.UploadAsync(pimax, variant, ct).ConfigureAwait(false);
            var warnings = new List<SessionWarning>();
            if (snapshotProblem is not null)
            {
                warnings.Add(new SessionWarning("calibration", snapshotProblem));
            }

            if (RememberTracker(upload.TrackerSerial))
            {
                warnings.Add(new SessionWarning("geometry", DifferentHeadset));
            }

            var environment = layerFolder is null
                ? FixedEnvironment
                : [.. FixedEnvironment, new EnvVar("VK_ADD_IMPLICIT_LAYER_PATH", layer.ManifestDir)];
            return new DreamAirSession(ctx, environment, wasRunning, upload, () => SnapshotCalibrations(deps.Calibration), warnings);
        }
        catch
        {
            await UndoAsync(ctx, wasRunning, saveRecovery).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RecoverAsync(ModuleContext ctx, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        if (data.GetValueOrDefault(DreamAirSession.RecoveryKey) != "true")
        {
            return;
        }

        var reply = await ctx.Helper.SendAsync(HelperCommand.StartPimax, ct).ConfigureAwait(false);
        if (!reply.Ok)
        {
            throw new MiradorException($"Mirador stopped Pimax's service in an earlier session and couldn't start it again: {reply.Error}");
        }
    }

    public void AddDiagnostics(ModuleContext ctx, Profile profile, IDiagnosticsSink sink)
    {
        var text = new StringBuilder();
        text.Append("NVIDIA driver: ").AppendLine(Safe(() => NvidiaDriverNow()) ?? "none");
        var pimax = Safe(() => PimaxInstall.Locate(deps.Registry));
        text.Append("Pimax Play: ").AppendLine(pimax is null ? "not found" : $"{pimax.Version ?? "unknown version"} in {pimax.RuntimeDir}");
        text.Append("SteamVR: ").AppendLine(Safe(deps.SteamVrDir) ?? "not found");
        text.Append("Vulkan layer: ").AppendLine(LayerFolder(profile) ?? $"comes with Mirador ({deps.BundledLayerDir})");
        var state = LoadState();
        text.Append("Last good driver: ").AppendLine(state.LastGoodDriver ?? "none yet");
        text.Append("Eye geometry variant: ").AppendLine(deps.BlobVariant(state));
        sink.AddText("dreamair.txt", text.ToString());
        sink.AddFile("display-check.txt", DisplayCheckFile(ctx.Paths));
        sink.AddFile("geometry-runtime.txt", Path.Combine(ctx.Paths.GeometryDir, "runtime.log"));
    }

    /// <summary>
    /// The Diagnostics page's "Check display", while Monado is stopped (run it through
    /// <c>SessionController.RunWhileStoppedAsync</c>, which keeps Starts out meanwhile): stops Pimax's software (the
    /// check refuses while it holds the headset), runs the display check, and starts Pimax again if it ran. Like a Start,
    /// it saves that Pimax ran before stopping it, so that recovery restarts it if Mirador goes away meanwhile. A pass
    /// records the driver as good, so the next Start doesn't check again.
    /// </summary>
    /// <returns>The check's result, and what went wrong starting Pimax again (null when nothing did).</returns>
    public async Task<(DisplayCheckResult Check, string? PimaxProblem)> CheckDisplayAsync(
        ModuleContext ctx, Profile profile, Action<IReadOnlyDictionary<string, string>> saveRecovery, CancellationToken ct)
    {
        LayerFiles layer;
        try
        {
            layer = DreamAirLayer.Locate(LayerFolder(profile), deps.BundledLayerDir);
        }
        catch (MiradorException e)
        {
            return (new DisplayCheckResult(false, e.Message, null, "") { Finished = false }, null);
        }

        var running = ctx.Processes.Snapshot();
        var seenRunning = ServiceRuns(running);
        if (seenRunning)
        {
            saveRecovery(DreamAirSession.Recovery(true));
        }

        var stop = await StopPimaxAsync(ctx, running).ConfigureAwait(false);
        var wasRunning = stop.WasRunning ?? (!stop.NotDelivered && seenRunning);
        if (wasRunning)
        {
            saveRecovery(DreamAirSession.Recovery(true));
        }

        DisplayCheckResult check;
        string? startProblem = null;
        try
        {
            check = stop.Ok
                ? await DisplayCheck.RunAsync(layer.CheckExe, deps.StartConsole, ct).ConfigureAwait(false)
                : new DisplayCheckResult(false, stop.Error ?? "Pimax's software couldn't be stopped.", null, "") { Finished = false };
        }
        finally
        {
            // Also when the check was cancelled: Pimax goes back as it was, and only then is nothing left to undo.
            if (wasRunning)
            {
                var start = await ctx.Helper.SendAsync(HelperCommand.StartPimax, CancellationToken.None).ConfigureAwait(false);
                startProblem = start.Ok ? null : $"Mirador couldn't start Pimax's service again: {start.Error} Mirador tries again the next time it starts.";
            }

            if (startProblem is null)
            {
                saveRecovery(new Dictionary<string, string>());
            }
        }

        if (stop.Ok)
        {
            SaveCheckOutput(ctx, check);
        }

        if (check.Pass && Safe(NvidiaDriverNow) is { } driver)
        {
            try
            {
                deps.State.Update(s => s with { LastGoodDriver = driver });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (check, startProblem);
    }

    private static bool ServiceRuns(IReadOnlyList<ProcInfo> running) =>
        running.Any(p => string.Equals(p.Name, PimaxNames.Service, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Stops Pimax's service and processes through the helper. When none of them runs there is nothing to stop or to
    /// start again, so the helper isn't asked: a missing or broken helper then doesn't block a Start that doesn't need it.
    /// </summary>
    private static Task<HelperReply> StopPimaxAsync(ModuleContext ctx, IReadOnlyList<ProcInfo> running) =>
        ServiceRuns(running) || running.Any(p => PimaxNames.Processes.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            ? ctx.Helper.SendAsync(HelperCommand.StopPimax, CancellationToken.None)
            : Task.FromResult(new HelperReply(true, WasRunning: false));

    internal const string DifferentHeadset =
        "This looks like a different headset. Mirador reads its eye geometry from Pimax Play the next time Pimax runs.";

    /// <summary>
    /// Reads the eye geometry variant Pimax's eye runtime runs with, when it runs, and remembers it. A helper that
    /// can't answer leaves things as they were: the remembered or tested variant is used.
    /// </summary>
    private async Task DetectVariantAsync(ModuleContext ctx, CancellationToken ct)
    {
        IReadOnlyList<ProcInfo> running;
        try
        {
            running = ctx.Processes.Snapshot();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        if (!running.Any(p => string.Equals(p.Name, PimaxNames.EyeRuntimeProcess, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var reply = await ctx.Helper.SendAsync(HelperCommand.GetPimaxEyeBlob, ct).ConfigureAwait(false);
        if (!reply.Ok || reply.Value is not { } detected)
        {
            return;
        }

        try
        {
            deps.State.Update(s => BlobChoice.Resolve(s, detected).State);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Remembers the tracker's serial after an upload; whether that cleared a variant (a different headset).</summary>
    private bool RememberTracker(string? serial)
    {
        try
        {
            var cleared = false;
            deps.State.Update(s =>
            {
                var after = BlobChoice.AfterUpload(s, serial);
                cleared = BlobChoice.Cleared(s, after);
                return after;
            });
            return cleared;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps a copy of every tracker's calibration that changed (Monado may have replaced one with the tracker's own).
    /// Never fails a Start or a Stop: a copy that can't be made is only missing from the list.
    /// </summary>
    /// <returns>Null, or what went wrong, for a warning: Monado may replace a calibration that has no copy then.</returns>
    internal static string? SnapshotCalibrations(CalibrationStore? store)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            foreach (var serial in store.Serials())
            {
                store.SnapshotIfChanged(serial);
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Mirador couldn't keep a copy of the eye calibration ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    /// <summary>Where the last display check's output is kept, for the diagnostics.</summary>
    public static string DisplayCheckFile(AppPaths paths) => Path.Combine(paths.LogsDir, "display-check.txt");

    /// <summary>The profile's layer folder; null for the layer that comes with Mirador.</summary>
    public static string? LayerFolder(Profile profile) =>
        profile.ModuleOptions.GetValueOrDefault(DreamAirLayer.OptionKey) is { } folder && !string.IsNullOrWhiteSpace(folder)
            ? folder
            : null;

    private DreamAirChecks Checks(ModuleContext ctx) =>
        new(deps.Registry, ctx.Usb, File.Exists, deps.NvidiaWindowsVersion, deps.SteamVrDir);

    private CheckResult LayerCheck(Profile profile)
    {
        const string Label = "Vulkan layer";
        var folder = LayerFolder(profile);
        try
        {
            DreamAirLayer.Locate(folder, deps.BundledLayerDir);
            return new CheckResult(Label, StatusLevel.Ok, folder is null ? "Comes with Mirador" : $"From {folder}");
        }
        catch (MiradorException e)
        {
            return new CheckResult(Label, StatusLevel.Error, e.Message);
        }
    }

    /// <summary>NVIDIA's version of the driver, or Windows' when Mirador can't read it; null without an NVIDIA GPU.</summary>
    private string? NvidiaDriverNow() =>
        deps.NvidiaWindowsVersion() is { } windows ? NvidiaDriver.FromWindowsVersion(windows) ?? windows : null;

    /// <summary>The saved state; an empty one when the file can't be read now.</summary>
    private DreamAirState LoadState()
    {
        try
        {
            return deps.State.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DreamAirState();
        }
    }

    /// <summary>
    /// Undoes the steps of a Start that won't go on: starts Pimax's service again if it ran, and then saves that
    /// nothing is left to undo. When Pimax won't start, the saved data stays, so recovery tries again.
    /// </summary>
    private static async Task UndoAsync(ModuleContext ctx, bool pimaxWasRunning, Action<IReadOnlyDictionary<string, string>> saveRecovery)
    {
        if (pimaxWasRunning)
        {
            // Stop may have cancelled the Start: this step must happen anyway, within the helper's own time limit.
            var reply = await ctx.Helper.SendAsync(HelperCommand.StartPimax, CancellationToken.None).ConfigureAwait(false);
            if (!reply.Ok)
            {
                return;
            }
        }

        try
        {
            saveRecovery(new Dictionary<string, string>());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The refusal that led here is what the user needs to hear; recovery finds nothing to do next time.
        }
    }

    private static void SaveCheckOutput(ModuleContext ctx, DisplayCheckResult check)
    {
        try
        {
            var file = DisplayCheckFile(ctx.Paths);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, check.Output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary><paramref name="s"/> ending in a full stop, so that two of them read as two sentences.</summary>
    private static string Sentence(string s) => s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? s : s + ".";

    private static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static T? Safe<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is MiradorException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
