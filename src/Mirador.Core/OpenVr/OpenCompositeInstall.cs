using System.Text.RegularExpressions;
using Mirador.Core.Build;
using Mirador.Core.Diagnostics;
using Mirador.Ipc;

namespace Mirador.Core.OpenVr;

/// <summary>What <see cref="OpenCompositeInstall.InstallAsync"/> did.</summary>
public enum InstallOutcome
{
    /// <summary>The release is installed.</summary>
    Installed,

    /// <summary>A game holds OpenComposite's DLL: nothing was changed, and a later try may succeed.</summary>
    InUse,
}

/// <summary>
/// OpenComposite as its own installer puts it for this user: <see cref="DefaultFolder"/>, with <c>bin\vrclient_x64.dll</c>
/// and a <c>SOURCE.txt</c> whose first line names the release. Mirador gets and updates it by running the fork's
/// <c>OpenComposite-Setup.exe</c> silently; OpenComposite's own uninstall entry removes it. Mirador keeps no copy.
/// </summary>
/// <param name="folder">The installer's folder.</param>
/// <param name="runner">Runs the installer; null: <see cref="CommandRunner"/>.</param>
public sealed partial class OpenCompositeInstall(string folder, ICommandRunner? runner = null)
{
    private readonly ICommandRunner runner = runner ?? new CommandRunner();

    /// <summary>The most the installer may be.</summary>
    public const long MaxSetupBytes = 64 * 1024 * 1024;

    /// <summary>How long the installer may run.</summary>
    public static readonly TimeSpan SetupLimit = TimeSpan.FromMinutes(5);

    private const string Dll = "vrclient_x64.dll";

    // What games load: the 64-bit DLL, and the 32-bit one beside it.
    private static readonly string[] Dlls = [Dll, "vrclient.dll"];
    private const string Host = "github.com";

    private int installing;

    /// <summary>
    /// Where the installer puts OpenComposite, always: <c>%LOCALAPPDATA%\Programs\OpenComposite</c>. The OpenVR paths file
    /// names it once OpenComposite is in front.
    /// </summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenComposite");

    /// <summary>OpenComposite's own log: <c>%LOCALAPPDATA%\OpenComposite\logs\opencomposite.log</c>.</summary>
    public static string LogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenComposite", "logs", "opencomposite.log");

    /// <summary>The installer's folder.</summary>
    public string Folder => folder;

    /// <summary>Whether OpenComposite is installed: its DLL is there.</summary>
    public bool IsInstalled => File.Exists(DllPath);

    /// <summary>The installed release, from its <c>SOURCE.txt</c>; null when there is none or it can't be read.</summary>
    public OpenCompositeSource? Source
    {
        get
        {
            try
            {
                var file = Path.Combine(folder, "SOURCE.txt");
                return File.Exists(file) ? OpenCompositeSource.Read(File.ReadAllText(file)) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Whether a game holds one of OpenComposite's DLLs now (they can't be replaced then).</summary>
    public bool InUse => Dlls.Any(name => Held(Path.Combine(folder, "bin", name)));

    private static bool Held(string dll)
    {
        if (!File.Exists(dll))
        {
            return false;
        }

        try
        {
            using var held = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // read-only, say: the installer says so if it matters
        }
    }

    /// <summary>The line for <c>versions.txt</c> and Diagnostics.</summary>
    public string VersionLine() =>
        Source is { } source ? $"OpenComposite: {source.Describe()}" : IsInstalled ? "OpenComposite: installed, version unknown" : "OpenComposite: not installed";

    private string DllPath => Path.Combine(folder, "bin", Dll);

    /// <summary>
    /// Downloads <paramref name="release"/>'s installer into <paramref name="downloadDir"/>, checks it against GitHub's
    /// SHA-256, runs it silently (<c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS</c>: it never closes a
    /// game, nor switches OpenVR games by itself) and checks that the release it names is installed. While a game holds
    /// OpenComposite nothing is downloaded or run.
    /// </summary>
    /// <exception cref="MiradorException">
    /// The release can't be trusted, fetched or installed, or another install is running; the message says why.
    /// </exception>
    public async Task<InstallOutcome> InstallAsync(HttpClient http, Release release, string downloadDir, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref installing, 1) == 1)
        {
            throw new MiradorException("OpenComposite is being installed already: wait for that to finish.");
        }

        try
        {
            return await InstallCoreAsync(http, release, downloadDir, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref installing, 0);
        }
    }

    private async Task<InstallOutcome> InstallCoreAsync(HttpClient http, Release release, string downloadDir, CancellationToken ct)
    {
        if (release.InstallerUrl is null || release.InstallerSha256 is null)
        {
            throw new MiradorException($"OpenComposite {Plain(release.Tag)} has no installer that Mirador can check.");
        }

        // The requested address, not wherever GitHub redirects to: GitHub serves its release files from its own file host.
        if (!Uri.TryCreate(release.InstallerUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new MiradorException("OpenComposite's installer isn't on GitHub, so Mirador won't fetch it.");
        }

        var version = Plain(release.Tag);
        if (!SafeName().IsMatch(version))
        {
            throw new MiradorException($"OpenComposite's release name \"{release.Tag}\" isn't a version Mirador understands.");
        }

        if (InUse)
        {
            return InstallOutcome.InUse;
        }

        Directory.CreateDirectory(downloadDir);
        var setup = Path.Combine(downloadDir, $"OpenComposite-Setup-{version}.exe");
        var part = setup + ".tmp";
        try
        {
            await CheckedDownload.ToFileAsync(http, url, part, MaxSetupBytes, release.InstallerSha256, "OpenComposite's installer", m => new MiradorException(m), ct)
                .ConfigureAwait(false);
            File.Move(part, setup, overwrite: true);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(SetupLimit);
            int code;
            try
            {
                code = await runner.RunAsync(
                    new CommandSpec(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS"], downloadDir),
                    _ => { },
                    limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new MiradorException($"OpenComposite's installer didn't finish within {SetupLimit.TotalMinutes:0} minutes.");
            }

            if (code != 0)
            {
                throw new MiradorException(InUse
                    ? "A game started using OpenComposite meanwhile, so it wasn't updated. Close the game and try again."
                    : $"OpenComposite's installer ended with code {code}, so OpenComposite may not have changed. If OpenComposite's own window is open, close it and try again.");
            }

            if (Source?.Version != version)
            {
                throw new MiradorException($"OpenComposite's installer ran, but {Folder} doesn't hold OpenComposite {version}.");
            }

            return InstallOutcome.Installed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MiradorException($"Mirador couldn't run OpenComposite's installer ({e.Message.Trim().TrimEnd('.')}).");
        }
        finally
        {
            Cleanup.TryDelete(part);
            Cleanup.TryDelete(setup);
        }
    }

    private static string Plain(string tag) => tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]*$")]
    private static partial Regex SafeName();
}
