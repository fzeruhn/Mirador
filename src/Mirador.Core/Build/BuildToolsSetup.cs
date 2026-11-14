namespace Mirador.Core.Build;

/// <summary>Runs a program through Windows' administrator prompt (UAC) and waits for it to end.</summary>
public interface IElevatedLauncher
{
    /// <summary>Runs <paramref name="spec"/> elevated and returns its exit code.</summary>
    /// <exception cref="ElevationDeclinedException">The prompt was declined; nothing ran.</exception>
    /// <exception cref="MiradorException">It couldn't be started.</exception>
    Task<int> RunAsync(CommandSpec spec, CancellationToken ct);
}

/// <summary>The administrator prompt was declined.</summary>
public sealed class ElevationDeclinedException() : Exception("The administrator prompt was declined.");

/// <summary>The build tools as they are: Visual Studio, each of Mirador's tools, vcpkg.</summary>
public sealed record BuildToolsReport(VsPlan VisualStudio, IReadOnlyList<(ToolPin Pin, ToolStatus Status)> Tools, ToolStatus Vcpkg)
{
    /// <summary>Every part, in the page's order.</summary>
    public IReadOnlyList<ToolStatus> Parts =>
        [new ToolStatus(VsPlan.Name, VisualStudio.State, VisualStudio.Detail), .. Tools.Select(t => t.Status), Vcpkg];

    /// <summary>Whether a branch build has everything.</summary>
    public bool AllFound => Parts.All(p => p.State == ToolState.Found);

    /// <summary>
    /// Null when a branch build has everything; else, in a sentence, which parts are missing and which need updating
    /// (at another version than the one Mirador's builds use).
    /// </summary>
    public string? Problem()
    {
        var missing = Parts.Where(p => p.State == ToolState.Missing).Select(p => p.Name).ToList();
        var outdated = Parts.Where(p => p.State == ToolState.Fixable).Select(p => p.Name).ToList();
        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"missing: {string.Join(", ", missing)}");
        }

        if (outdated.Count > 0)
        {
            parts.Add($"to update: {string.Join(", ", outdated)}");
        }

        return parts.Count == 0 ? null : $"Mirador's build tools aren't all ready ({string.Join("; ", parts)}).";
    }
}

/// <summary>
/// The build tools a branch build uses, pinned and installed when a build needs them, as a build wrapper does: Mirador's
/// small tools and vcpkg first, as the user, then Visual Studio through its own installer and one administrator prompt.
/// Microsoft's compiler is never shipped or unpacked by Mirador.
/// </summary>
/// <param name="runner">Runs vswhere and git.</param>
/// <param name="http">Downloads from GitHub, and Microsoft's bootstrapper.</param>
/// <param name="elevated">Runs Visual Studio's installer through UAC.</param>
/// <param name="toolsDir">Mirador's tools (<see cref="AppPaths.ToolsDir"/>).</param>
/// <param name="vcpkgCommit">The pinned vcpkg commit (<c>{app}\tools\vcpkg-commit.txt</c>).</param>
/// <param name="vswhere">vswhere (<see cref="VsWhere.DefaultPath"/>).</param>
/// <param name="vsInstallerDir">Visual Studio's installer folder, whose <c>setup.exe</c> modifies an install.</param>
/// <param name="signerOf">The Authenticode signer of a file (<c>WinTrustSigner.SubjectOf</c>).</param>
/// <param name="pins">The tools (<see cref="ToolPins.All"/>).</param>
public sealed class BuildToolsSetup(
    ICommandRunner runner,
    HttpClient http,
    IElevatedLauncher elevated,
    string toolsDir,
    string vcpkgCommit,
    string vswhere,
    string vsInstallerDir,
    Func<string, string?> signerOf,
    IReadOnlyList<ToolPin> pins)
{
    // One build gets the tools ready at a time; another waits, then finds them ready.
    private readonly SemaphoreSlim preparing = new(1, 1);

    // Visual Studio's installer asked for a restart (3010): until then vswhere may not list what it added.
    private volatile bool restartAsked;

    private const string RestartNeeded =
        "Visual Studio's installer wants Windows restarted before its new parts can be used: restart Windows, then build again.";

    /// <summary>
    /// Gets every part ready for a branch build: what is missing, or at another version than the one builds use, is
    /// installed first, its lines on <paramref name="onLine"/> (the build's log).
    /// </summary>
    /// <returns>Null when everything is ready; else why not, in a sentence that says what to do.</returns>
    public async Task<string?> PrepareAsync(Action<string> onLine, CancellationToken ct)
    {
        if (!preparing.Wait(0))
        {
            onLine("Another build is getting the build tools ready: waiting for it…");
            await preparing.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var report = await CheckAsync(ct).ConfigureAwait(false);
            if (report.AllFound)
            {
                return null;
            }

            // Until Windows restarts, what Visual Studio's installer added may not show yet: not installed again.
            if (restartAsked && report.Parts.All(p => p.State == ToolState.Found || p.Name == VsPlan.Name))
            {
                return RestartNeeded;
            }

            onLine($"Getting the build tools ready first: {string.Join(", ", report.Parts.Where(p => p.State != ToolState.Found).Select(p => p.Name))}.");
            var failures = await InstallMissingAsync(report, onLine, ct).ConfigureAwait(false);
            if (failures.Count > 0)
            {
                return $"{string.Join(' ', failures)} Build again to try once more.";
            }

            var after = await CheckAsync(ct).ConfigureAwait(false);
            if (after.Problem() is not { } problem)
            {
                return null;
            }

            var onlyVisualStudio = after.Parts.All(p => p.State == ToolState.Found || p.Name == VsPlan.Name);
            return restartAsked && onlyVisualStudio ? RestartNeeded
                : onlyVisualStudio ? "Visual Studio's installer finished, but its C++ tools don't show yet: open the Visual Studio Installer to check them, then build again."
                : $"{problem} Build again to try once more.";
        }
        finally
        {
            preparing.Release();
        }
    }

    /// <summary>The state of every part now.</summary>
    public async Task<BuildToolsReport> CheckAsync(CancellationToken ct)
    {
        var vs = VsPlan.For(await VsWhere.FindAsync(runner, vswhere, ct).ConfigureAwait(false));
        var check = new BuildToolsCheck(toolsDir, vcpkgCommit);
        return new BuildToolsReport(vs, [.. pins.Select(p => (p, check.Tool(p)))], check.Vcpkg());
    }

    /// <summary>
    /// Installs every part of <paramref name="report"/> that isn't found, each on its own: one that fails doesn't stop
    /// the others.
    /// </summary>
    /// <returns>What failed, one plain sentence per part; empty when everything was installed.</returns>
    public async Task<IReadOnlyList<string>> InstallMissingAsync(BuildToolsReport report, Action<string> onLine, CancellationToken ct)
    {
        var failures = new List<string>();
        var installer = new ToolsInstaller(http, runner, toolsDir, vcpkgCommit);
        async Task Try(Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (MiradorException e)
            {
                onLine(e.Message);
                failures.Add(e.Message);
            }
        }

        foreach (var (pin, status) in report.Tools.Where(t => t.Status.State != ToolState.Found))
        {
            await Try(() => installer.InstallAsync(pin, onLine, ct)).ConfigureAwait(false);
        }

        if (report.Vcpkg.State != ToolState.Found)
        {
            var git = Path.Combine(toolsDir, "git", "cmd", "git.exe");
            await Try(() => installer.InstallVcpkgAsync(File.Exists(git) ? git : "git", onLine, ct)).ConfigureAwait(false);
        }

        // After a restart was asked for, Visual Studio's installer isn't run again until Windows has restarted.
        if (report.VisualStudio.State != ToolState.Found && !restartAsked)
        {
            await Try(() => InstallVisualStudioAsync(report.VisualStudio, onLine, ct)).ConfigureAwait(false);
        }

        return failures;
    }

    private async Task InstallVisualStudioAsync(VsPlan plan, Action<string> onLine, CancellationToken ct)
    {
        // Microsoft's bootstrapper runs elevated from a user folder, as Microsoft's own instructions run it: in a new
        // folder of its own, held without write sharing from the signer check until it ends (VsBootstrapper), and
        // Windows' prompt shows its publisher.
        BootstrapperFile? bootstrapper = null;
        if (plan.State == ToolState.Missing)
        {
            onLine("Downloading Microsoft's Build Tools installer…");
            bootstrapper = await VsBootstrapper.DownloadAsync(http, Path.Combine(toolsDir, "download"), signerOf, ct).ConfigureAwait(false);
        }

        using (bootstrapper)
        {
            onLine(plan.State == ToolState.Missing
                ? "Installing Microsoft's Build Tools (Windows asks for an administrator; the installer shows its progress)…"
                : $"Adding {string.Join(" and ", plan.Missing.Select(VsComponents.Describe))} to {plan.Install!.Name} (Windows asks for an administrator)…");
            int code;
            try
            {
                code = await elevated.RunAsync(plan.Command(vsInstallerDir, bootstrapper?.Path ?? ""), ct).ConfigureAwait(false);
            }
            catch (ElevationDeclinedException)
            {
                throw new MiradorException("The administrator prompt was declined, so Visual Studio wasn't changed.");
            }

            switch (code)
            {
                case 0:
                    onLine("Visual Studio is ready.");
                    break;
                case 3010:
                    restartAsked = true;
                    onLine("Visual Studio is ready; Windows wants a restart before it is used.");
                    break;
                default:
                    throw new MiradorException($"Visual Studio's installer ended with code {code}: it may not have finished. Open the Visual Studio Installer to see what it says, or its logs in %TEMP% (dd_*.log).");
            }
        }
    }
}
