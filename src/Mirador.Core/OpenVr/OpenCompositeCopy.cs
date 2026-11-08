using System.Security.Cryptography;

namespace Mirador.Core.OpenVr;

/// <summary>What <see cref="OpenCompositeCopy.Update"/> did.</summary>
public enum CopyOutcome
{
    /// <summary>The copy matched the bundle already; nothing was written.</summary>
    Current,

    /// <summary>The copy was made or brought up to date.</summary>
    Updated,

    /// <summary>A game holds the copy's DLL: the old copy stays, and the next try may succeed.</summary>
    InUse,

    /// <summary>This Mirador has no bundled OpenComposite (a dev run).</summary>
    NoBundle,
}

/// <summary>
/// Mirador's per-user copy of the bundled OpenComposite (Mirador.md §4.6 item 20). OpenComposite writes
/// <c>applist.json</c> next to its DLL at every game start and ends the game if it can't, so it can't run from the
/// admin-only install folder. Only <c>bin\vrclient_x64.dll</c> and <c>bin\version.txt</c> are ever written; the files
/// OpenComposite and the owner put there (<c>applist.json</c>, <c>apps-config.json</c>, <c>opencomposite.ini</c>) stay.
/// </summary>
/// <param name="bundleDir">The bundled OpenComposite: <c>{app}\opencomposite</c>, with <c>bin\</c> and <c>SOURCE.txt</c>.</param>
/// <param name="runtimeDir">The copy: the OpenVR runtime folder, with <c>bin\</c> below it.</param>
public sealed class OpenCompositeCopy(string bundleDir, string runtimeDir)
{
    private const string Dll = "vrclient_x64.dll";

    private static readonly string[] Files = ["version.txt", Dll];

    /// <summary>OpenComposite's own log: <c>%LOCALAPPDATA%\OpenComposite\logs\opencomposite.log</c>.</summary>
    public static string LogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenComposite", "logs", "opencomposite.log");

    /// <summary>The copy's folder, the one the OpenVR paths file names.</summary>
    public string RuntimeDir => runtimeDir;

    /// <summary>Whether this Mirador has OpenComposite in its bundle.</summary>
    public bool HasBundle => File.Exists(Path.Combine(bundleDir, "bin", Dll));

    /// <summary>Whether the copy's DLL is there.</summary>
    public bool HasCopy => File.Exists(Path.Combine(runtimeDir, "bin", Dll));

    /// <summary>The copy's OpenComposite, from its <c>SOURCE.txt</c>; null when it has no record (made before Phase 6, or by hand).</summary>
    public OpenCompositeSource? CopySource => ReadSource(runtimeDir);

    /// <summary>Whether the copy is a release downloaded from GitHub: its version is newer than the bundle's.</summary>
    public bool HoldsDownload => CopySource?.Version is { } version && OpenCompositeSource.Compare(version, BundleSource?.Version) > 0;

    /// <summary>Where downloaded releases are unpacked: <c>{runtimeDir}\download\&lt;version&gt;\OpenComposite</c>.</summary>
    public string DownloadRoot => Path.Combine(runtimeDir, "download");

    /// <summary>
    /// At Mirador's launch and at "Set OpenComposite": brings the copy up to date with the newest of the bundle and the
    /// unpacked downloads that are newer than the bundle. It is written when it has no record, an older version, or the
    /// same version with other bytes; a newer copy stays. IO problems other than a held DLL are thrown.
    /// </summary>
    public CopyOutcome Update()
    {
        if (!HasBundle)
        {
            return CopyOutcome.NoBundle;
        }

        return Sync(Candidate(), force: false);
    }

    /// <summary>The same as <see cref="Update"/> from <paramref name="releaseDir"/>, an unpacked release (<c>OpenComposite\</c> with <c>bin\</c>).</summary>
    public CopyOutcome Install(string releaseDir) => Sync(releaseDir, force: false);

    /// <summary>
    /// Goes back to the bundled OpenComposite whatever the versions, and deletes the downloads. When a game holds the DLL
    /// nothing changes (the downloads stay, so the copy would stay as it is).
    /// </summary>
    public CopyOutcome UseBundled()
    {
        if (!HasBundle)
        {
            return CopyOutcome.NoBundle;
        }

        var outcome = Sync(bundleDir, force: true);
        if (outcome == CopyOutcome.InUse)
        {
            return outcome;
        }

        if (Directory.Exists(DownloadRoot))
        {
            Directory.Delete(DownloadRoot, recursive: true);
        }

        // A record or licence the bundle doesn't have would describe the wrong OpenComposite.
        foreach (var stale in Directory.EnumerateFiles(runtimeDir, "*.txt").Where(f => IsOurs(Path.GetFileName(f)) && !File.Exists(Path.Combine(bundleDir, Path.GetFileName(f)))))
        {
            File.Delete(stale);
        }

        return outcome;
    }

    private static bool IsOurs(string name) =>
        name.Equals("SOURCE.txt", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("NOTES_", StringComparison.OrdinalIgnoreCase);

    private string Candidate()
    {
        var best = bundleDir;
        var bestVersion = BundleSource?.Version;
        if (!Directory.Exists(DownloadRoot))
        {
            return best;
        }

        foreach (var folder in Directory.EnumerateDirectories(DownloadRoot))
        {
            var release = Path.Combine(folder, "OpenComposite");
            // Only a finished unpack: its folder is named for its version. "<ver>.part" (a crash mid-unpack) never is.
            if (!File.Exists(Path.Combine(release, "bin", Dll))
                || ReadSource(release)?.Version is not { } version
                || !string.Equals(Path.GetFileName(folder), version, StringComparison.Ordinal))
            {
                continue;
            }

            if (OpenCompositeSource.Compare(version, bestVersion) > 0)
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

    /// <summary>The bundle's OpenComposite: its <c>SOURCE.txt</c>'s first line, or null when it is missing or says neither form.</summary>
    public OpenCompositeSource? BundleSource
    {
        get
        {
            return ReadSource(bundleDir);
        }
    }

    /// <summary>The OpenComposite commit the bundle was built from.</summary>
    public string? Commit() => BundleSource?.Commit;

    /// <summary>The line for <c>versions.txt</c>: <c>OpenComposite: &lt;version (commit)&gt;</c>.</summary>
    public string VersionLine() => $"OpenComposite: {BundleSource?.Describe() ?? "not in this build"}";

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
