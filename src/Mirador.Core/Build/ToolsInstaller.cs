using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mirador.Core.Build;

/// <summary>
/// Installs Mirador's own build tools under <c>%LOCALAPPDATA%\Mirador\tools</c> (Mirador.md §4.11 item 49): each pinned
/// zip from its GitHub release, checked by its SHA-256 before anything is unpacked, no entry leaving its folder; and
/// vcpkg, cloned whole over https and checked out at Mirador's pinned commit. A failure leaves the old copy as it was.
/// </summary>
public sealed class ToolsInstaller(HttpClient http, ICommandRunner runner, string toolsDir, string vcpkgCommit, Action<string, string>? moveDirectory = null)
{
    /// <summary>How often a folder move is tried (a virus scanner may hold a fresh file for a moment), 200 ms apart.</summary>
    public const int MoveAttempts = 5;

    private readonly Action<string, string> move = moveDirectory ?? Directory.Move;

    /// <summary>The most a tool's zip may be.</summary>
    public const long MaxDownloadBytes = 256L * 1024 * 1024;

    /// <summary>The most a tool's zip may unpack to.</summary>
    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;

    /// <summary>Where vcpkg comes from.</summary>
    public const string VcpkgUrl = "https://github.com/microsoft/vcpkg.git";

    /// <summary>Downloads, checks and unpacks <paramref name="pin"/> into its folder, replacing an older copy.</summary>
    /// <exception cref="MiradorException">It can't be fetched or trusted; the message says why. The old copy stays.</exception>
    public async Task InstallAsync(ToolPin pin, Action<string> onLine, CancellationToken ct)
    {
        var url = Downloads.GitHubUrl(pin.Url, pin.Name);
        Directory.CreateDirectory(toolsDir);
        var final = Path.Combine(toolsDir, pin.Folder);
        var zipFile = final + ".zip.part";
        var staging = final + ".new";
        var old = final + ".old";
        try
        {
            onLine($"Downloading {pin.Name} {pin.Version} from GitHub…");
            await Downloads.ToFileAsync(http, url, zipFile, MaxDownloadBytes, pin.Sha256, pin.Name, ct).ConfigureAwait(false);

            onLine($"Unpacking {pin.Name}…");
            DeleteFolder(staging);
            Unpack(zipFile, staging, pin.Name, pin.Exe);
            if (!File.Exists(Path.Combine(staging, pin.Exe)))
            {
                throw new MiradorException($"The {pin.Name} download isn't the one Mirador expects: it has no {pin.Exe}.");
            }

            await File.WriteAllTextAsync(
                Path.Combine(staging, BuildToolsCheck.MarkerFile),
                JsonSerializer.Serialize(new BuildToolsCheck.Marker(pin.Version, pin.Sha256.ToLowerInvariant())),
                CancellationToken.None).ConfigureAwait(false);

            // The old copy goes aside whole first: something may be using it (a build), and then nothing changes. If the
            // new one then can't take its place, the old one goes back.
            DeleteFolder(old);
            var hadOld = Directory.Exists(final);
            if (hadOld && !TryMove(final, old))
            {
                throw new MiradorException($"Mirador's {pin.Name} is in use (a build may be running): try again when it is done.");
            }

            if (!TryMove(staging, final))
            {
                if (hadOld && !TryMove(old, final))
                {
                    throw new MiradorException($"Mirador couldn't put {pin.Name} in place, nor its old copy back: it is in {old}. Try \"Install what's missing\" again.");
                }

                throw new MiradorException($"Mirador couldn't put {pin.Name} in place (another program may be holding it): try again.");
            }

            onLine($"{pin.Name} {pin.Version} is ready.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new MiradorException($"Mirador couldn't unpack {pin.Name} ({e.Message.Trim().TrimEnd('.')}).");
        }
        finally
        {
            TryDelete(zipFile);
            TryDeleteFolder(staging);
            if (Directory.Exists(final))
            {
                TryDeleteFolder(old);
            }
        }
    }

    private bool TryMove(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move(from, to);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == MoveAttempts)
                {
                    return false;
                }

                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Clones vcpkg whole (its manifests need its history) with <paramref name="git"/>, or fetches an existing clone,
    /// and checks out Mirador's pinned commit; the commit reached is checked, so a clone elsewhere isn't used.
    /// </summary>
    /// <exception cref="MiradorException">A git step failed, or vcpkg isn't at the pinned commit; the message says which.</exception>
    public async Task InstallVcpkgAsync(string git, Action<string> onLine, CancellationToken ct)
    {
        if (!BuildToolsCheck.Commit().IsMatch(vcpkgCommit))
        {
            throw new MiradorException("Mirador's vcpkg pin (tools\\vcpkg-commit.txt) isn't a commit, so vcpkg wasn't fetched.");
        }

        Directory.CreateDirectory(toolsDir);
        var final = Path.Combine(toolsDir, "vcpkg");
        var existing = Directory.Exists(Path.Combine(final, ".git"));
        var work = existing ? final : final + ".new";
        var lastLine = "";
        void Line(string line)
        {
            lastLine = line;
            onLine(line);
        }

        // Never a prompt for a name or password, no helper of the user's asked, and https only (as branch builds).
        var environment = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" };
        async Task Git(string step, string? where, params string[] args)
        {
            var code = await runner.RunAsync(
                new CommandSpec(git, ["-c", "credential.helper=", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", .. args], where, environment),
                Line, ct).ConfigureAwait(false);
            if (code != 0)
            {
                var why = lastLine.Trim();
                throw new MiradorException($"Mirador couldn't {step} vcpkg (git exit code {code}{(why.Length > 0 ? ": " + why : "")}).");
            }
        }

        try
        {
            if (existing)
            {
                onLine("Fetching vcpkg from GitHub…");
                await Git("fetch", work, "fetch", "--quiet", VcpkgUrl).ConfigureAwait(false);
            }
            else
            {
                DeleteFolder(work);
                onLine("Cloning vcpkg from GitHub (all of it: its manifests need its history)…");
                await Git("clone", toolsDir, "clone", "--quiet", "--no-checkout", VcpkgUrl, work).ConfigureAwait(false);
            }

            await Git("check out", work, "-c", "advice.detachedHead=false", "checkout", "--quiet", "--force", "--detach", vcpkgCommit).ConfigureAwait(false);

            var head = new List<string>();
            var code = await runner.RunAsync(new CommandSpec(git, ["rev-parse", "HEAD"], work, environment), head.Add, ct).ConfigureAwait(false);
            if (code != 0 || head.FirstOrDefault()?.Trim() is not { } reached || !string.Equals(reached, vcpkgCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new MiradorException($"vcpkg didn't end at Mirador's pinned commit {vcpkgCommit[..7]}, so it isn't used.");
            }

            if (existing)
            {
                // vcpkg's own program was built from the sources of the commit before: the recipe bootstraps it again
                // from these when it is missing, so the new ports never run under an older vcpkg.
                File.Delete(Path.Combine(work, "vcpkg.exe"));
            }

            if (!existing && !TryMove(work, final))
            {
                throw new MiradorException("Mirador couldn't put vcpkg in place (another program may be holding it): try again.");
            }

            onLine($"vcpkg is ready at commit {vcpkgCommit[..7]}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MiradorException($"Mirador couldn't put vcpkg in place ({e.Message.Trim().TrimEnd('.')}).");
        }
        finally
        {
            if (!existing)
            {
                TryDeleteFolder(final + ".new");
            }
        }
    }

    private static void Unpack(string zipFile, string folder, string name, string exe)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipFile);

        // A zip whose every entry is in one top folder holding the program (CMake's) is unpacked without that folder.
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        var first = names.Select(n => n.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        var strip = first.Count == 1 && names.All(n => n.Contains('/')) && names.Contains(first[0] + "/" + exe.Replace('\\', '/'), StringComparer.Ordinal)
            ? first[0] + "/"
            : "";

        // Every path is checked, and the size added up, before anything is written.
        long size = 0;
        var targets = new List<(ZipArchiveEntry Entry, string Path)>();
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            var entry = zip.Entries[i];
            var relative = names[i][strip.Length..];
            if (relative.Length == 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative))
            {
                throw new MiradorException($"The {name} download has a file (\"{entry.FullName}\") that would land outside its folder, so it was not used.");
            }

            size += entry.Length;
            if (size > MaxUnpackedBytes)
            {
                throw new MiradorException($"The {name} download unpacks to more than Mirador allows, so it was not used.");
            }

            targets.Add((entry, target));
        }

        Directory.CreateDirectory(root);
        foreach (var (entry, target) in targets)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            DeleteFolder(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next try, which deletes it first.
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next try, which overwrites it.
        }
    }
}

/// <summary>
/// Microsoft's Build Tools bootstrapper (<c>vs_buildtools.exe</c>), downloaded from Microsoft as upstream's CI does and
/// run only when Windows says Microsoft signed it.
/// </summary>
public static class VsBootstrapper
{
    /// <summary>The Build Tools for Visual Studio 2022, as upstream's CI installs them.</summary>
    public const string Url = "https://aka.ms/vs/17/release/vs_buildtools.exe";

    /// <summary>The most the bootstrapper may be.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Downloads the bootstrapper into a new, empty folder of its own under <paramref name="downloadRoot"/> (so no DLL
    /// planted earlier sits beside it when it runs elevated), opens it without write or delete sharing, and only then
    /// checks its signer with <paramref name="signerOf"/> (<c>WinTrustSigner.SubjectOf</c>): its CN and O must both be
    /// Microsoft Corporation. The file stays held until the returned handle is disposed, after the installer has ended,
    /// so the bytes checked are the bytes that run; the folder then goes.
    /// </summary>
    /// <exception cref="MiradorException">It couldn't be fetched or isn't Microsoft's; nothing is kept.</exception>
    public static async Task<BootstrapperFile> DownloadAsync(HttpClient http, string downloadRoot, Func<string, string?> signerOf, CancellationToken ct)
    {
        var folder = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "vs_buildtools.exe");
        FileStream? held = null;
        try
        {
            await Downloads.ToFileAsync(http, new Uri(Url), file, MaxBytes, null, "Visual Studio Build Tools installer", ct).ConfigureAwait(false);
            held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Mirador.Ipc.SignerName.Is(signerOf(file), "Microsoft Corporation"))
            {
                throw new MiradorException("The Visual Studio Build Tools installer Mirador downloaded isn't signed by Microsoft, so it was not run.");
            }

            return new BootstrapperFile(file, held);
        }
        catch
        {
            held?.Dispose();
            BootstrapperFile.TryDeleteFolder(folder);
            throw;
        }
    }
}

/// <summary>The checked bootstrapper, held open (read sharing only) until disposed; then its folder is deleted.</summary>
public sealed class BootstrapperFile(string path, FileStream held) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        held.Dispose();
        TryDeleteFolder(System.IO.Path.GetDirectoryName(Path)!);
    }

    internal static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A new folder is used each time; this one is left for Windows' disk clean-up.
        }
    }
}

/// <summary>Mirador's checked downloads for the build tools.</summary>
internal static class Downloads
{
    /// <summary>How long the server may say nothing before a download is given up.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary><paramref name="url"/> when it is https on github.com (the address asked for, not where GitHub redirects).</summary>
    public static Uri GitHubUrl(string url, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? uri
            : throw new MiradorException($"{name}'s download isn't on GitHub, so Mirador won't fetch it.");

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="file"/>, at most <paramref name="maxBytes"/>, and checks its
    /// SHA-256 when one is given; a mismatch or failure deletes the file.
    /// </summary>
    public static async Task ToFileAsync(HttpClient http, Uri url, string file, long maxBytes, string? sha256, string name, CancellationToken userCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        var ct = linked.Token;
        try
        {
            linked.CancelAfter(IdleTimeout);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MiradorException($"The {name} download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new MiradorException($"The {name} download is larger than Mirador allows, so it was not fetched.");
            }

            using var hash = SHA256.Create();
            await using (var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await network.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new MiradorException($"The {name} download is larger than Mirador allows, so it was stopped.");
                    }

                    linked.CancelAfter(IdleTimeout);
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            if (sha256 is not null && !string.Equals(Convert.ToHexString(hash.Hash!), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new MiradorException($"The {name} download doesn't match the SHA-256 Mirador pins for it, so it was not used.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !userCt.IsCancellationRequested))
        {
            TryDelete(file);
            var why = e is OperationCanceledException ? "the server stopped answering" : e.Message.Trim().TrimEnd('.');
            throw new MiradorException($"Mirador couldn't download {name} ({why}).");
        }
        catch
        {
            TryDelete(file);
            throw;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next download.
        }
    }
}
