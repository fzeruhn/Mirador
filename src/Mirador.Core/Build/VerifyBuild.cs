using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mirador.Core.Monado;

namespace Mirador.Core.Build;

/// <summary>How a step of "Verify build for Windows" ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerifyOutcome>))]
public enum VerifyOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>One step of a verify run, in plain words.</summary>
public sealed record VerifyStep(string Name, VerifyOutcome Outcome, string Detail);

/// <summary>A verify run of one build (its <see cref="Key"/>: the commit, else the service's hash).</summary>
/// <param name="HasElevationFix">
/// What the elevated step said about the elevation fix (Mirador.md §10.4): true when an unelevated game connected to the
/// build running as administrator, false when it couldn't although the same game ran with the build as the user; null
/// when the step didn't run or couldn't tell.
/// </param>
/// <param name="LogFile">The run's own log (every line it wrote), which Diagnostics shows; null for an older result.</param>
public sealed record VerifyResult(string Key, DateTimeOffset At, IReadOnlyList<VerifyStep> Steps, bool? HasElevationFix = null, string? LogFile = null)
{
    /// <summary>Whether a step failed.</summary>
    public bool HasFailure() => Steps.Any(s => s.Outcome == VerifyOutcome.Failed);

    /// <summary>
    /// The result Diagnostics shows: the <paramref name="wanted"/> profile's (its card sent the user there) when it has
    /// one, else the newest of any profile; null when no profile has one.
    /// </summary>
    public static (string ProfileId, VerifyResult Result)? ForDiagnostics(
        IEnumerable<(string ProfileId, VerifyResult? Result)> results, string? wanted)
    {
        var known = results.Where(r => r.Result is not null).Select(r => (r.ProfileId, Result: r.Result!)).ToList();
        foreach (var r in known)
        {
            if (r.ProfileId == wanted)
            {
                return r;
            }
        }

        return known.Count == 0 ? null : known.OrderByDescending(r => r.Result.At).First();
    }

    /// <summary>
    /// The profile card's line: passed or failed and <paramref name="when"/> (the run's time as the user reads it); the
    /// details are on Diagnostics, which a failure or a skipped check points to.
    /// </summary>
    public string Verdict(string when)
    {
        var skipped = Steps.Count(s => s.Outcome == VerifyOutcome.Skipped);
        return HasFailure() ? $"Failed on {when}: Diagnostics has what went wrong."
            : skipped > 0 ? $"Passed on {when}, with {skipped} {(skipped == 1 ? "check" : "checks")} skipped: Diagnostics says why."
            : $"Passed on {when}.";
    }

    /// <summary>One line for the profile.</summary>
    public string Summary()
    {
        var failed = Steps.Where(s => s.Outcome == VerifyOutcome.Failed).Select(s => s.Name).ToList();
        var skipped = Steps.Count(s => s.Outcome == VerifyOutcome.Skipped);
        return failed.Count > 0 ? $"Failed: {string.Join("; ", failed)}."
            : skipped > 0 ? $"Every check that ran passed; {skipped} skipped."
            : "Every check passed.";
    }

    public void Save(string file) => JsonFile.Save(file, this, VerifyJsonContext.Default.VerifyResult);

    /// <summary>The result in <paramref name="file"/>, or null when there is none or it can't be read.</summary>
    public static VerifyResult? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), VerifyJsonContext.Default.VerifyResult) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>The result line of <c>xr-check</c> (<c>tools\xr-check</c>): <c>XR-CHECK {json}</c>.</summary>
/// <param name="Submitted">How many of the frames rendered a layer; -1 from an older xr-check that didn't say.</param>
public sealed record XrCheckReport(string Api, bool Ok, int Frames, string Runtime, string System, string Error, int Submitted = -1)
{
    public const string Prefix = "XR-CHECK ";

    /// <summary>The last result line in <paramref name="lines"/>, or null when there is none it can read.</summary>
    public static XrCheckReport? Parse(IEnumerable<string> lines)
    {
        var line = lines.LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line[Prefix.Length..], VerifyJsonContext.Default.XrCheckReport);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>An elevated, headless Monado for the verify run, or why there isn't one.</summary>
/// <param name="Failed">Whether it was tried and failed (the step fails), rather than not possible here (skipped).</param>
public sealed record ElevatedVerify(IAsyncDisposable? Running, string? WhyNot, bool Failed = false);

/// <summary>What a verify run needs from Mirador: Monado started headless three ways. Disposing stops each.</summary>
public interface IVerifyServices
{
    /// <summary>Starts the build's own service as the user, with the null compositor and the simulated headset.</summary>
    /// <exception cref="MiradorException">It didn't come up; the message says why.</exception>
    Task<IAsyncDisposable> StartHeadlessAsync(MonadoInstall install, CancellationToken ct);

    /// <summary>
    /// While the wait lives, a stand-in of <paramref name="install"/> asking Mirador to start Monado gets it headless
    /// (as <see cref="StartHeadlessAsync"/>) instead of the profile's normal start; the wait says whether it asked.
    /// </summary>
    StandInWait ExpectStandIn(MonadoInstall install);

    /// <summary>Starts the build as administrator through the helper, headless; or says why it can't.</summary>
    Task<ElevatedVerify> StartElevatedHeadlessAsync(MonadoInstall install, CancellationToken ct);
}

/// <summary>
/// The stand-in start Verify waits for (<see cref="IVerifyServices.ExpectStandIn"/>): the first ask starts Monado
/// headless, later ones find it starting or running; ending the wait waits for a start under way and stops what it
/// started, so nothing outlives Verify; an ask after the end is refused.
/// </summary>
public sealed class StandInWait(MonadoInstall install, Func<CancellationToken, Task<IAsyncDisposable>> start) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAsyncDisposable? started;
    private bool ended;

    public MonadoInstall Install { get; } = install;

    /// <summary>Whether a stand-in asked.</summary>
    public bool WasAsked { get; private set; }

    /// <summary>Why the start for the stand-in failed, when it did.</summary>
    public string? Failure { get; private set; }

    /// <summary>Called first when the wait ends (Mirador stops routing stand-ins to it).</summary>
    public Action? Ended { get; set; }

    /// <summary>Starts Monado headless for the stand-in unless it is started.</summary>
    /// <exception cref="MiradorException">The wait has ended, or the start failed (kept in <see cref="Failure"/>).</exception>
    public async Task StartAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ended)
            {
                throw new MiradorException("Verify isn't waiting for a game any more.");
            }

            WasAsked = true;
            if (started is null)
            {
                try
                {
                    started = await start(ct).ConfigureAwait(false);
                }
                catch (MiradorException e)
                {
                    Failure = e.Message;
                    throw;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Ended?.Invoke();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ended = true;
            if (started is not null)
            {
                await started.DisposeAsync().ConfigureAwait(false);
                started = null;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// "Verify build for Windows" (Mirador.md §4.11 item 49): what opens sessions or needs an administrator, which the
/// checks after every build can't do. With <c>xr-check</c>, Mirador's small OpenXR client: a session in D3D11 and one in
/// Vulkan against the service with Monado's null compositor; a game's start through the stand-in; and the service as
/// administrator with the client as the user, which tells whether the build has the elevation fix (§10.4). One step
/// failing doesn't stop the others. The results are kept by the build's commit.
/// </summary>
public sealed class VerifyBuild(ICommandRunner runner, string xrCheck, TimeProvider time)
{
    public const string D3D11Step = "A session in D3D11";
    public const string VulkanStep = "A session in Vulkan";
    public const string StandInStep = "A game's start through the stand-in";
    public const string ElevatedStep = "Monado as administrator, the game as you";

    /// <summary>Frames per headless session.</summary>
    public const int Frames = 300;

    /// <summary>Frames for the stand-in and elevated steps: enough to know the game connected and ran.</summary>
    public const int ShortFrames = 90;

    /// <summary>The longest one client run may take (the stand-in's start included), in seconds.</summary>
    public const int ClientTimeoutSeconds = 120;

    /// <summary>Runs every step against <paramref name="install"/>.</summary>
    /// <exception cref="OperationCanceledException">Cancelled; whatever ran has been stopped.</exception>
    public async Task<VerifyResult> RunAsync(MonadoInstall install, string key, IVerifyServices services, Action<string> onLine, CancellationToken ct)
    {
        var steps = new List<VerifyStep>();

        // 1. Two sessions against the service as the user, with no headset or display.
        onLine("Starting Monado with its null compositor and a simulated headset…");
        IAsyncDisposable? headless = null;
        try
        {
            headless = await services.StartHeadlessAsync(install, ct).ConfigureAwait(false);
        }
        catch (MiradorException e)
        {
            var why = "Monado didn't start headless: " + e.Message;
            steps.Add(new VerifyStep(D3D11Step, VerifyOutcome.Failed, why));
            steps.Add(new VerifyStep(VulkanStep, VerifyOutcome.Failed, why));
        }

        if (headless is not null)
        {
            await using (headless.ConfigureAwait(false))
            {
                steps.Add(await SessionStepAsync(D3D11Step, "d3d11", Frames, install, onLine, ct).ConfigureAwait(false));
                ct.ThrowIfCancellationRequested();
                steps.Add(await SessionStepAsync(VulkanStep, "vulkan", Frames, install, onLine, ct).ConfigureAwait(false));
                ct.ThrowIfCancellationRequested();
            }
        }

        // 2. A game's start: no service runs; the runtime starts the stand-in, which asks Mirador.
        var standIn = Path.Combine(install.StandInDir, StandIn.FileName);
        if (!File.Exists(standIn))
        {
            steps.Add(new VerifyStep(StandInStep, VerifyOutcome.Failed,
                $"There is no stand-in below this runtime ({install.StandInDir}), so games can't start Monado through Mirador."));
        }
        else
        {
            onLine("Starting a game with no Monado running, as a game would…");
            var wait = services.ExpectStandIn(install);
            await using (wait.ConfigureAwait(false))
            {
                var step = await SessionStepAsync(StandInStep, "d3d11", ShortFrames, install, onLine, ct).ConfigureAwait(false);
                if (step.Outcome == VerifyOutcome.Passed)
                {
                    steps.Add(wait.WasAsked
                        ? step with { Detail = "A game started Monado through the stand-in and ran. " + step.Detail }
                        : step with { Outcome = VerifyOutcome.Failed, Detail = "The game ran, but its stand-in never asked Mirador: was another Monado running?" });
                }
                else
                {
                    steps.Add(wait.Failure is { } why
                        ? step with { Detail = $"The stand-in asked, but Mirador couldn't start Monado for it: {why}" }
                        : wait.WasAsked ? step : step with { Detail = step.Detail + " Its stand-in never asked Mirador." });
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        // 3. Monado as administrator, the game as the user: only a build with the elevation fix lets it connect.
        onLine("Starting Monado as administrator, headless…");
        var elevated = await services.StartElevatedHeadlessAsync(install, ct).ConfigureAwait(false);
        bool? hasFix = null;
        if (elevated.Running is null)
        {
            steps.Add(new VerifyStep(
                ElevatedStep, elevated.Failed ? VerifyOutcome.Failed : VerifyOutcome.Skipped, elevated.WhyNot ?? "Monado can't run as administrator here."));
        }
        else
        {
            await using (elevated.Running.ConfigureAwait(false))
            {
                var step = await SessionStepAsync(ElevatedStep, "d3d11", ShortFrames, install, onLine, ct).ConfigureAwait(false);
                var ranAsYou = steps.Any(s => s.Name == D3D11Step && s.Outcome == VerifyOutcome.Passed);
                if (step.Outcome == VerifyOutcome.Passed)
                {
                    hasFix = true;
                    steps.Add(step with { Detail = "A game running as you connected to Monado running as administrator: this build has the elevation fix." });
                }
                else if (ranAsYou)
                {
                    hasFix = false;
                    steps.Add(step with { Detail = $"A game running as you couldn't use Monado running as administrator, though it ran with Monado as you: this build lacks the elevation fix. {step.Detail}" });
                }
                else
                {
                    steps.Add(step with { Detail = $"Couldn't tell whether this build has the elevation fix: the game failed with Monado as you too. {step.Detail}" });
                }
            }
        }

        foreach (var step in steps)
        {
            onLine($"{step.Name}: {step.Outcome}. {step.Detail}");
        }

        return new VerifyResult(key, time.GetUtcNow(), steps, hasFix);
    }

    private async Task<VerifyStep> SessionStepAsync(string name, string api, int frames, MonadoInstall install, Action<string> onLine, CancellationToken ct)
    {
        var output = new List<string>();
        var spec = new CommandSpec(
            xrCheck,
            ["--api", api, "--frames", frames.ToString(System.Globalization.CultureInfo.InvariantCulture), "--timeout", ClientTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            null,
            new Dictionary<string, string?> { ["XR_RUNTIME_JSON"] = install.Manifest });
        int code;
        try
        {
            code = await runner.RunAsync(spec, line =>
            {
                lock (output)
                {
                    output.Add(line);
                }

                // The result line is summed up in the step; the rest is the client's own log.
                if (!line.StartsWith(XrCheckReport.Prefix, StringComparison.Ordinal))
                {
                    onLine(line);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (MiradorException e)
        {
            return new VerifyStep(name, VerifyOutcome.Failed, e.Message);
        }

        XrCheckReport? report;
        lock (output)
        {
            report = XrCheckReport.Parse(output);
        }

        if (report is null)
        {
            return new VerifyStep(name, VerifyOutcome.Failed, $"xr-check gave no result (exit code {code}).");
        }

        return report.Ok && code == 0
            ? new VerifyStep(name, VerifyOutcome.Passed,
                $"{report.Frames} frames{(report.Submitted >= 0 ? $" ({report.Submitted} rendered)" : "")} with {report.System} ({report.Runtime}).")
            : new VerifyStep(name, VerifyOutcome.Failed, $"{report.Error} (after {report.Frames} frames).");
    }

    /// <summary>
    /// The key a build's results are kept under: its commit (<c>build-info.json</c>, else <c>checks.json</c> beside the
    /// runtime manifest), else the SHA-256 of its service.
    /// </summary>
    public static string KeyFor(MonadoInstall install)
    {
        var dir = Path.GetDirectoryName(install.Manifest) ?? "";
        if (BuildInfo.Load(dir)?.Commit is { Length: > 0 } commit)
        {
            return commit;
        }

        if (BuildCheckResult.Load(dir)?.Commit is { Length: > 0 } checkedCommit)
        {
            return checkedCommit;
        }

        try
        {
            using var stream = File.OpenRead(install.ServiceExe);
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream))[..16];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(VerifyResult))]
[JsonSerializable(typeof(XrCheckReport))]
internal sealed partial class VerifyJsonContext : JsonSerializerContext;
