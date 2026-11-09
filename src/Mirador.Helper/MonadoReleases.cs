using System.IO.Compression;
using System.Security.Cryptography;
using Mirador.Ipc;

namespace Mirador.Helper;

/// <summary>Installs, updates and rolls back Mirador's Monado; the seam that lets the handler be tested without it.</summary>
public interface IMonadoInstaller
{
    /// <summary>The runtime manifest of the current version, or null when none is installed.</summary>
    string? CurrentManifest();

    /// <summary>Whether <paramref name="manifest"/> is one of Mirador's Monado's (any version, installed or not).</summary>
    bool IsMiradors(string manifest);

    /// <summary>The service's folder of the current version, or null when none is installed.</summary>
    string? CurrentServerDir();

    /// <summary>Installs <paramref name="version"/>, which must be one of Mirador's releases, and makes it current.</summary>
    Task<HelperReply> InstallAsync(string version, CancellationToken ct);

    /// <summary>Makes the previous version current again, and the current one the previous.</summary>
    HelperReply RollBack();
}

/// <summary>
/// Mirador's Monado in the helper (Mirador.md §4.11 item 55, Mirador_Plan.md Task 62): the nightly builds of upstream
/// <c>main</c> from Mirador's own releases, installed in <c>{app}\monado\&lt;version&gt;\</c>, which only administrators
/// can change, so that this Monado may run elevated and be the machine's runtime without any further approval.
/// The request names only a version. The list, the download address and the SHA-256 all come from GitHub, read here;
/// nothing is unpacked before the hash matches, no entry may leave its folder, and the version in use stays until the
/// new one is complete. One previous version is kept for "Roll back"; older ones go.
/// </summary>
/// <param name="http">For GitHub.</param>
/// <param name="monadoRoot"><c>{app}\monado</c>.</param>
/// <param name="standInExe">Mirador's stand-in (<c>{app}\standin\monado-service.exe</c>), put below each version's DLL.</param>
/// <param name="registry">OpenXR's runtime entries.</param>
/// <param name="userAgentVersion">The helper's version, for GitHub's User-Agent.</param>
public sealed class MonadoInstaller(HttpClient http, string monadoRoot, string standInExe, IRuntimeRegistry registry, string userAgentVersion)
    : IMonadoInstaller
{
    /// <summary>The most a build's zip, and what it unpacks to, may be.</summary>
    public const long MaxZipBytes = 512L * 1024 * 1024;

    /// <summary>The most a build may unpack to.</summary>
    public const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>How long GitHub may say nothing before a download is given up.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private readonly object gate = new();

    /// <summary>The installer of a normal install: <c>{app}\monado</c> and <c>{app}\standin</c>, one above the helper.</summary>
    public static MonadoInstaller ForInstall(HttpClient http, IRuntimeRegistry registry, string userAgentVersion) => new(
        http,
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "monado")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "standin", "monado-service.exe")),
        registry,
        userAgentVersion);

    public string? CurrentManifest()
    {
        var current = MiradorMonado.Read(monadoRoot).Current;
        var manifest = current is null ? null : MiradorMonado.ManifestOf(monadoRoot, current);
        return manifest is not null && File.Exists(manifest) ? manifest : null;
    }

    public bool IsMiradors(string manifest) => MiradorMonado.IsManifestOf(monadoRoot, manifest);

    public string? CurrentServerDir() =>
        CurrentManifest() is { } manifest ? Path.Combine(Path.GetDirectoryName(manifest)!, "bin") : null;

    public async Task<HelperReply> InstallAsync(string version, CancellationToken ct)
    {
        if (!MiradorMonado.IsVersion(version))
        {
            return new HelperReply(false, "That isn't a version of Mirador's Monado.");
        }

        var releases = await MonadoReleaseList.FetchAsync(http, userAgentVersion, ct).ConfigureAwait(false);
        if (releases is null)
        {
            return new HelperReply(false, "GitHub didn't answer, so Mirador's Monado wasn't installed. Try again later.");
        }

        if (releases.FirstOrDefault(r => r.Version == version) is not { } release)
        {
            return new HelperReply(false, $"{version} isn't one of Mirador's Monado builds on GitHub (any more), so it wasn't installed.");
        }

        if (!File.Exists(standInExe))
        {
            return new HelperReply(false, "This Mirador has no stand-in (standin\\monado-service.exe). Reinstall Mirador.");
        }

        var work = Path.Combine(monadoRoot, ".download");
        var zip = Path.Combine(work, version + ".zip");
        var part = Path.Combine(work, version);
        try
        {
            Directory.CreateDirectory(work);
            await DownloadAsync(release, zip, ct).ConfigureAwait(false);
            TryDeleteDirectory(part);
            Unpack(zip, part);
            Check(part);
            var standInDir = Directory.CreateDirectory(Path.Combine(part, "bin", "service")).FullName;
            File.Copy(standInExe, Path.Combine(standInDir, "monado-service.exe"), overwrite: true);

            lock (gate)
            {
                return MakeCurrent(version, part);
            }
        }
        catch (Exception e) when (e is MiradorInstallException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new HelperReply(false, e is MiradorInstallException ? e.Message : $"Mirador's Monado couldn't be installed ({e.Message.TrimEnd('.')}).");
        }
        finally
        {
            TryDelete(zip);
            TryDeleteDirectory(part);
        }
    }

    public HelperReply RollBack()
    {
        lock (gate)
        {
            var versions = MiradorMonado.Read(monadoRoot);
            if (versions.Previous is not { } previous || !File.Exists(MiradorMonado.ManifestOf(monadoRoot, previous)))
            {
                return new HelperReply(false, "There is no earlier version of Mirador's Monado to go back to.");
            }

            MiradorMonado.Write(monadoRoot, new MiradorMonadoVersions(previous, versions.Current));
            FollowActive(versions.Current, previous);
            return new HelperReply(true, Value: previous);
        }
    }

    /// <summary>Moves the unpacked <paramref name="part"/> into place as <paramref name="version"/> and makes it current.</summary>
    private HelperReply MakeCurrent(string version, string part)
    {
        var versions = MiradorMonado.Read(monadoRoot);
        var folder = MiradorMonado.FolderOf(monadoRoot, version);
        if (versions.Current == version && File.Exists(MiradorMonado.ManifestOf(monadoRoot, version)))
        {
            return new HelperReply(true, Value: version);
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.Move(part, folder);
        var previous = versions.Current is { } current && current != version ? current : versions.Previous;
        MiradorMonado.Write(monadoRoot, new MiradorMonadoVersions(version, previous));
        registry.SetAvailable(MiradorMonado.ManifestOf(monadoRoot, version), available: true);
        FollowActive(versions.Current, version);
        Prune(version, previous);
        return new HelperReply(true, Value: version);
    }

    /// <summary>
    /// Mirador's runtime stays the active one across an update or a roll back, when it was; and so does the bundled
    /// Monado of an older install (<c>{app}\monado\runtime</c>), whose files that install's update removed
    /// (Mirador_Plan.md Task 69).
    /// </summary>
    private void FollowActive(string? from, string to)
    {
        if (registry.GetActive() is not { } active)
        {
            return;
        }

        var full = Path.GetFullPath(active);
        var wasMiradors = from is not null && string.Equals(full, MiradorMonado.ManifestOf(monadoRoot, from), StringComparison.OrdinalIgnoreCase);
        var wasBundled = string.Equals(full, Path.Combine(monadoRoot, "runtime", "openxr_monado.json"), StringComparison.OrdinalIgnoreCase)
            && !File.Exists(full);
        if (wasMiradors || wasBundled)
        {
            registry.SetActive(MiradorMonado.ManifestOf(monadoRoot, to));
        }
    }

    /// <summary>Every version but the current and the previous goes, with its runtime entry; one in use stays for now.</summary>
    private void Prune(string current, string? previous)
    {
        foreach (var folder in Directory.GetDirectories(monadoRoot))
        {
            var name = Path.GetFileName(folder);
            if (!MiradorMonado.IsVersion(name) || name == current || name == previous)
            {
                continue;
            }

            registry.SetAvailable(MiradorMonado.ManifestOf(monadoRoot, name), available: false);
            TryDeleteDirectory(folder);
        }
    }

    private async Task DownloadAsync(MonadoRelease release, string file, CancellationToken userCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        var ct = linked.Token;
        try
        {
            linked.CancelAfter(IdleTimeout);
            using var response = await http.GetAsync(new Uri(release.ZipUrl), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MiradorInstallException($"GitHub didn't give Mirador's Monado ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > MaxZipBytes)
            {
                throw new MiradorInstallException("Mirador's Monado download is larger than Mirador allows, so it was not fetched.");
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
                    if (total > MaxZipBytes)
                    {
                        throw new MiradorInstallException("Mirador's Monado download is larger than Mirador allows, so it was stopped.");
                    }

                    linked.CancelAfter(IdleTimeout);
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            if (!string.Equals(Convert.ToHexString(hash.Hash!), release.ZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new MiradorInstallException("Mirador's Monado download doesn't match the SHA-256 GitHub lists for it, so it was not used.");
            }
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !userCt.IsCancellationRequested))
        {
            var why = e is OperationCanceledException ? "GitHub stopped answering" : e.Message.Trim().TrimEnd('.');
            throw new MiradorInstallException($"Mirador couldn't download its Monado ({why}).");
        }
    }

    private static void Unpack(string zipFile, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipFile);

        // Every path is checked, and the size added up, before anything is written.
        long size = 0;
        var targets = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new MiradorInstallException($"Mirador's Monado download has a file (\"{entry.FullName}\") that would land outside its folder, so it was not used.");
            }

            size += entry.Length;
            if (size > MaxUnpackedBytes)
            {
                throw new MiradorInstallException("Mirador's Monado download unpacks to more than Mirador allows, so it was not used.");
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

    private static void Check(string folder)
    {
        foreach (var file in new[] { "openxr_monado.json", Path.Combine("bin", "openxr_monado.dll"), Path.Combine("bin", "monado-service.exe") })
        {
            if (!File.Exists(Path.Combine(folder, file)))
            {
                throw new MiradorInstallException($"Mirador's Monado download has no {file}, so it was not used.");
            }
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
        }
    }

    private static void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A failure whose message is meant for the user as it is.</summary>
    private sealed class MiradorInstallException(string message) : Exception(message);
}
