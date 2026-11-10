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

/// <summary>What the "Check build tools" page shows: Visual Studio, each of Mirador's tools, vcpkg.</summary>
public sealed record BuildToolsReport(VsPlan VisualStudio, IReadOnlyList<(ToolPin Pin, ToolStatus Status)> Tools, ToolStatus Vcpkg)
{
    /// <summary>Every part, in the page's order.</summary>
    public IReadOnlyList<ToolStatus> Parts =>
        [new ToolStatus(VsPlan.Name, VisualStudio.State, VisualStudio.Detail), .. Tools.Select(t => t.Status), Vcpkg];

    /// <summary>Whether a branch build has everything.</summary>
    public bool AllFound => Parts.All(p => p.State == ToolState.Found);
}

/// <summary>
/// The build tools, detected then offered (Mirador.md §4.11 item 49, "as rustup and flutter doctor do"): the check, and
/// "Install what's missing": Mirador's small tools and vcpkg first, as the user, then Visual Studio through its own
/// installer and one administrator prompt. Microsoft's compiler is never shipped or unpacked by Mirador.
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
    /// <exception cref="MiradorException">Another install is running.</exception>
    public async Task<IReadOnlyList<string>> InstallMissingAsync(BuildToolsReport report, Action<string> onLine, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref installing, 1) == 1)
        {
            throw new MiradorException("The build tools are being installed already: wait for that to finish.");
        }

        try
        {
            return await InstallCoreAsync(report, onLine, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref installing, 0);
        }
    }

    private int installing;

    /// <summary>Whether "Install what's missing" is running.</summary>
    public bool IsInstalling => Volatile.Read(ref installing) == 1;

    private async Task<IReadOnlyList<string>> InstallCoreAsync(BuildToolsReport report, Action<string> onLine, CancellationToken ct)
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

        if (report.VisualStudio.State != ToolState.Found)
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
                    onLine("Visual Studio is ready; Windows wants a restart before it is used.");
                    break;
                default:
                    throw new MiradorException($"Visual Studio's installer ended with code {code}: it may not have finished. Open the Visual Studio Installer to see what it says, or its logs in %TEMP% (dd_*.log).");
            }
        }
    }
}
