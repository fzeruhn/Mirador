using System.Security.Cryptography;

namespace Mirador.Core.OpenVr;

/// <summary>What <see cref="OpenCompositeCopy.Update"/> did.</summary>
public enum CopyOutcome
{
    /// <summary>The copy was up to date already; nothing was written.</summary>
    Current,

    /// <summary>The copy was made or brought up to date.</summary>
    Updated,

    /// <summary>A game holds the copy's DLL: the old copy stays, and the next try may succeed.</summary>
    InUse,

    /// <summary>There is no OpenComposite to copy yet: none downloaded ("Get OpenComposite", Mirador.md §4.11 item 56).</summary>
    NoneYet,

    /// <summary>"Roll back" found no earlier OpenComposite to go back to.</summary>
    NothingEarlier,
}

/// <summary>
/// Mirador's per-user copy of OpenComposite (Mirador.md §4.6 item 20): a release downloaded from the fork ("Get
/// OpenComposite", the update; §4.11 item 56). OpenComposite writes
/// <c>applist.json</c> next to its DLL at every game start and ends the game if it can't, so it can't run from the
/// admin-only install folder. Only <c>bin\vrclient_x64.dll</c> and <c>bin\version.txt</c> are ever written; the files
/// OpenComposite and the owner put there (<c>applist.json</c>, <c>apps-config.json</c>, <c>opencomposite.ini</c>) stay.
/// </summary>
/// <param name="runtimeDir">The copy: the OpenVR runtime folder, with <c>bin\</c> below it.</param>
public sealed class OpenCompositeCopy(string runtimeDir)
{
    private const string Dll = "vrclient_x64.dll";

    private static readonly string[] Files = ["version.txt", Dll];

    /// <summary>OpenComposite's own log: <c>%LOCALAPPDATA%\OpenComposite\logs\opencomposite.log</c>.</summary>
    public static string LogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenComposite", "logs", "opencomposite.log");

    /// <summary>The copy's folder, the one the OpenVR paths file names.</summary>
    public string RuntimeDir => runtimeDir;

    /// <summary>Whether the copy's DLL is there.</summary>
    public bool HasCopy => File.Exists(Path.Combine(runtimeDir, "bin", Dll));

    /// <summary>The copy's OpenComposite, from its <c>SOURCE.txt</c>; null when it has no record (made before Phase 6, or by hand).</summary>
    public OpenCompositeSource? CopySource => ReadSource(runtimeDir);

    /// <summary>Whether the copy is a release downloaded from GitHub (it has its record), so "Roll back" can apply.</summary>
    public bool HoldsDownload => CopySource?.Version is not null;

    /// <summary>Whether there is OpenComposite to copy: a finished download.</summary>
    public bool HasSomethingToCopy => Downloads().Any();

    /// <summary>How many downloaded releases are kept: the one in use and one for "Roll back".</summary>
    public const int KeptDownloads = 2;

    /// <summary>Where downloaded releases are unpacked: <c>{runtimeDir}\download\&lt;version&gt;\OpenComposite</c>.</summary>
    public string DownloadRoot => Path.Combine(runtimeDir, "download");

    /// <summary>
    /// At Mirador's launch and at "Set OpenComposite": brings the copy up to date with the newest of the unpacked
    /// downloads. It is written when it has no record, an older version, or the
    /// same version with other bytes; a newer copy stays. IO problems other than a held DLL are thrown.
    /// </summary>
    public CopyOutcome Update() => Candidate() is { } from ? Sync(from, force: false) : CopyOutcome.NoneYet;

    /// <summary>
    /// The same as <see cref="Update"/> from <paramref name="releaseDir"/>, an unpacked release (<c>OpenComposite\</c>
    /// with <c>bin\</c>). Once it is in place, only it and the release before it stay downloaded.
    /// </summary>
    public CopyOutcome Install(string releaseDir)
    {
        var outcome = Sync(releaseDir, force: false);
        if (outcome != CopyOutcome.InUse)
        {
            PruneDownloads();
        }

        return outcome;
    }

    /// <summary>
    /// "Roll back": goes back to the release before the one in the copy, from the downloads, and deletes the newer
    /// download so the launch doesn't bring it back. When a game holds the DLL nothing changes.
    /// </summary>
    public CopyOutcome RollBack()
    {
        var have = CopySource?.Version;
        var candidates = Downloads()
            .Where(d => have is null || OpenCompositeSource.Compare(d.Version, have) < 0)
            .OrderByDescending(d => d.Version, Comparer<string>.Create((a, b) => OpenCompositeSource.Compare(a, b)))
            .Take(1)
            .ToList();
        if (candidates.Count == 0)
        {
            return CopyOutcome.NothingEarlier;
        }

        var earlier = candidates[0];

        var outcome = Sync(earlier.Folder, force: true);
        if (outcome == CopyOutcome.InUse)
        {
            return outcome;
        }

        foreach (var newer in Downloads().Where(d => OpenCompositeSource.Compare(d.Version, earlier.Version) > 0))
        {
            Directory.Delete(Path.GetDirectoryName(newer.Folder)!, recursive: true);
        }

        return outcome;
    }

    /// <summary>The finished downloads: <c>download\&lt;version&gt;\OpenComposite</c> whose SOURCE.txt names that version.</summary>
    private IEnumerable<(string Folder, string Version)> Downloads()
    {
        if (!Directory.Exists(DownloadRoot))
        {
            yield break;
        }

        foreach (var folder in Directory.EnumerateDirectories(DownloadRoot))
        {
            var release = Path.Combine(folder, "OpenComposite");
            // Only a finished unpack: its folder is named for its version. "<ver>.part" (a crash mid-unpack) never is.
            if (File.Exists(Path.Combine(release, "bin", Dll))
                && ReadSource(release)?.Version is { } version
                && string.Equals(Path.GetFileName(folder), version, StringComparison.Ordinal))
            {
                yield return (release, version);
            }
        }
    }

    /// <summary>All but the newest <see cref="KeptDownloads"/> downloads go; one a game holds stays until next time.</summary>
    private void PruneDownloads()
    {
        var old = Downloads()
            .OrderByDescending(d => d.Version, Comparer<string>.Create((a, b) => OpenCompositeSource.Compare(a, b)))
            .Skip(KeptDownloads)
            .ToList();
        foreach (var (folder, _) in old)
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The newest of the downloads, or null when there is none.</summary>
    private string? Candidate()
    {
        string? best = null;
        string? bestVersion = null;
        foreach (var (release, version) in Downloads())
        {
            if (best is null || OpenCompositeSource.Compare(version, bestVersion) > 0)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    private CopyOutcome Sync(string from, bool force)
    {
        if (!File.Exists(Path.Combine(from, "bin", Dll)))
        {
            throw new IOException($"{from} has no {Dll}.");
        }

        if (!force && HasCopy && CopySource is { } have && OpenCompositeSource.Compare(have.Version, ReadSource(from)?.Version) > 0)
        {
            return CopyOutcome.Current;
        }

        var target = Directory.CreateDirectory(Path.Combine(runtimeDir, "bin")).FullName;
        var updated = false;
        foreach (var name in Files)
        {
            var source = Path.Combine(from, "bin", name);
            if (!File.Exists(source))
            {
                continue;
            }

            var written = Write(source, Path.Combine(target, name));
            if (written is null)
            {
                return CopyOutcome.InUse;
            }

            updated |= written.Value;
        }

        // SOURCE.txt last: the record only changes once the DLL has.
        var texts = Directory.EnumerateFiles(from, "*.txt").OrderBy(f => Path.GetFileName(f) == "SOURCE.txt");
        foreach (var source in texts)
        {
            updated |= Write(source, Path.Combine(runtimeDir, Path.GetFileName(source))) ?? false;
        }

        return updated ? CopyOutcome.Updated : CopyOutcome.Current;
    }

    /// <summary>Writes <paramref name="dest"/> through a <c>.new</c> file when it is missing or differs: whether it did, or null when a game holds it.</summary>
    private static bool? Write(string source, string dest)
    {
        if (File.Exists(dest) && SameContent(source, dest))
        {
            return false;
        }

        var staged = dest + ".new";
        File.Copy(source, staged, overwrite: true);
        try
        {
            File.Move(staged, dest, overwrite: true);
        }
        catch (Exception e) when (File.Exists(dest) && (e is IOException or UnauthorizedAccessException))
        {
            // A game has the DLL open or loaded (a loaded one is "access denied"): keep the old one for now.
            File.Delete(staged);
            return null;
        }

        return true;
    }

    private static OpenCompositeSource? ReadSource(string folder)
    {
        var file = Path.Combine(folder, "SOURCE.txt");
        return File.Exists(file) ? OpenCompositeSource.Read(File.ReadAllText(file)) : null;
    }

    /// <summary>The line for <c>versions.txt</c>: <c>OpenComposite: &lt;version (commit)&gt;</c>, as About says.</summary>
    public string VersionLine() => CopySource is { } source ? $"OpenComposite: {source.Describe()}" : "OpenComposite: none yet";

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
        {
            return false;
        }

        using var first = File.OpenRead(a);
        using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }
}
