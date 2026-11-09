using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mirador.Ipc;

/// <summary>
/// A build the owner approved to run as administrator or as the machine's OpenXR runtime (Mirador.md §4.11 item 51):
/// a copy in <c>{app}\builds\&lt;profile&gt;\&lt;commit&gt;\</c>, which only administrators can change, and the SHA-256 of
/// each of its files, kept in <c>HKLM\SOFTWARE\Mirador\ApprovedBuilds\&lt;profile&gt;</c>.
/// </summary>
/// <param name="ProfileId">The profile the build is for.</param>
/// <param name="Commit">The commit it was built from (or <c>folder</c> for a build folder with no git).</param>
/// <param name="Folder">The approved copy.</param>
/// <param name="Files">Each file of the copy, by its path relative to <see cref="Folder"/>, with its SHA-256 (lower case).</param>
public sealed record ApprovedBuild(string ProfileId, string Commit, string Folder, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The copy's runtime manifest (Monado's install layout).</summary>
    public string Manifest => Path.Combine(Folder, "openxr_monado.json");

    /// <summary>The copy's service folder.</summary>
    public string ServerDir => Path.Combine(Folder, "bin");
}

/// <summary>Where approved builds are recorded; only an administrator can write it.</summary>
public interface IApprovedBuildStore
{
    ApprovedBuild? Get(string profileId);

    void Set(ApprovedBuild build);

    void Remove(string profileId);
}

/// <summary>
/// Approving a build and checking an approved one (Mirador_Plan.md Task 65). <see cref="Approve"/> runs elevated, in
/// Mirador's <c>--approve-build</c> step after one UAC prompt; the helper only reads and checks (<see cref="Verify"/>)
/// before every elevated Start and before making the copy the OpenXR runtime.
/// </summary>
public static partial class ApprovedBuilds
{
    /// <summary>The folder of all approved builds below Mirador's install folder.</summary>
    public static string Root(string appDir) => Path.Combine(appDir, "builds");

    /// <summary>Whether <paramref name="id"/> is a profile id (a GUID's digits or a default profile's name): never a path.</summary>
    public static bool IsProfileId(string? id) => id is not null && ProfileIdPattern().IsMatch(id);

    /// <summary>
    /// Copies the install in <paramref name="source"/> to <c>{appDir}\builds\&lt;profile&gt;\&lt;commit&gt;\</c>, hashes the
    /// copy and records it, replacing the profile's earlier approved build. A link or junction anywhere in the source is
    /// refused: it could point the copy at files outside the build.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source can't be approved; the message says why.</exception>
    public static ApprovedBuild Approve(string profileId, string commit, string source, string appDir, IApprovedBuildStore store)
    {
        if (!IsProfileId(profileId))
        {
            throw new InvalidOperationException("That isn't a profile.");
        }

        if (!CommitPattern().IsMatch(commit))
        {
            throw new InvalidOperationException("That isn't a commit.");
        }

        var from = Path.GetFullPath(source);
        foreach (var file in new[] { "openxr_monado.json", Path.Combine("bin", "openxr_monado.dll"), Path.Combine("bin", "monado-service.exe") })
        {
            if (!File.Exists(Path.Combine(from, file)))
            {
                throw new InvalidOperationException($"The build in {from} has no {file}: build it again first.");
            }
        }

        var profileRoot = Path.Combine(Root(appDir), profileId);
        var target = Path.Combine(profileRoot, commit);
        var staging = target + ".new";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Copy(new DirectoryInfo(from), staging);
        var files = Hash(staging);

        // Only now the earlier approval goes: the profile's other copies, and this commit's if it was approved before.
        store.Remove(profileId);
        if (Directory.Exists(profileRoot))
        {
            foreach (var old in Directory.GetDirectories(profileRoot).Where(d => !string.Equals(d, staging, StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Delete(old, recursive: true);
            }
        }

        Directory.Move(staging, target);
        var build = new ApprovedBuild(profileId, commit, target, files);
        store.Set(build);
        return build;
    }

    /// <summary>
    /// Null when <paramref name="build"/> is still exactly what was approved: in its place under
    /// <paramref name="appDir"/>, with every recorded file there with its recorded hash, and no program or library
    /// added. Otherwise why not, in plain words.
    /// </summary>
    public static string? Verify(ApprovedBuild build, string appDir)
    {
        var expected = Path.Combine(Root(appDir), build.ProfileId, build.Commit);
        if (!IsProfileId(build.ProfileId) || !CommitPattern().IsMatch(build.Commit)
            || !string.Equals(Path.GetFullPath(build.Folder), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            return "The approved build's record doesn't name its own folder. Approve the build again.";
        }

        if (!Directory.Exists(build.Folder) || new DirectoryInfo(build.Folder).LinkTarget is not null)
        {
            return "The approved build is gone. Approve the build again.";
        }

        foreach (var (relative, hash) in build.Files)
        {
            var path = Path.GetFullPath(Path.Combine(build.Folder, relative));
            if (!path.StartsWith(Path.GetFullPath(build.Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || !string.Equals(HashOf(path), hash, StringComparison.OrdinalIgnoreCase))
            {
                return $"The approved build changed since it was approved ({relative}). Approve it again.";
            }
        }

        foreach (var file in Directory.EnumerateFiles(build.Folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(build.Folder, file);
            if (!build.Files.ContainsKey(relative) && IsCode(file))
            {
                return $"A file was added to the approved build since it was approved ({relative}). Approve it again.";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the build in <paramref name="sourceInstall"/> (the profile's build as it is now) is the one approved: its
    /// runtime DLL and service have the approved copy's hashes. A rebuild is new code and runs as the user until approved.
    /// </summary>
    public static bool Matches(ApprovedBuild build, string sourceInstall)
    {
        foreach (var relative in new[] { Path.Combine("bin", "openxr_monado.dll"), Path.Combine("bin", "monado-service.exe") })
        {
            var file = Path.Combine(sourceInstall, relative);
            if (!build.Files.TryGetValue(relative, out var hash) || !File.Exists(file)
                || !string.Equals(HashOf(file), hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The SHA-256 of a file, lower case.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool IsCode(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".manifest" or ".ps1" or ".bat" or ".cmd";

    private static void Copy(DirectoryInfo from, string to)
    {
        if (from.LinkTarget is not null)
        {
            throw new InvalidOperationException($"{from.FullName} is a link: Mirador approves only a build's own files.");
        }

        Directory.CreateDirectory(to);
        foreach (var file in from.EnumerateFiles())
        {
            if (file.LinkTarget is not null)
            {
                throw new InvalidOperationException($"{file.FullName} is a link: Mirador approves only a build's own files.");
            }

            file.CopyTo(Path.Combine(to, file.Name));
        }

        foreach (var dir in from.EnumerateDirectories())
        {
            Copy(dir, Path.Combine(to, dir.Name));
        }
    }

    private static Dictionary<string, string> Hash(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), HashOf, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ProfileIdPattern();

    [GeneratedRegex(@"^([0-9a-f]{7,40}|folder)$")]
    private static partial Regex CommitPattern();
}

/// <summary>
/// The approved builds in <c>HKLM\SOFTWARE\Mirador\ApprovedBuilds</c> (64-bit view): a key per profile with
/// <c>Commit</c> and <c>Folder</c>, and its <c>Files</c> subkey with a value per file (relative path → SHA-256).
/// </summary>
public sealed class RegistryApprovedBuildStore : IApprovedBuildStore
{
    /// <summary>The key's path under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public const string KeyPath = @"SOFTWARE\Mirador\ApprovedBuilds";

    private readonly RegistryHive hive;
    private readonly string keyPath;

    public RegistryApprovedBuildStore()
        : this(RegistryHive.LocalMachine, KeyPath)
    {
    }

    /// <summary>For tests: the key somewhere a test may write (under <c>HKEY_CURRENT_USER</c>).</summary>
    internal RegistryApprovedBuildStore(RegistryHive hive, string keyPath)
    {
        this.hive = hive;
        this.keyPath = keyPath;
    }

    public ApprovedBuild? Get(string profileId)
    {
        if (!ApprovedBuilds.IsProfileId(profileId))
        {
            return null;
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{keyPath}\{profileId}");
        using var files = key?.OpenSubKey("Files");
        if (key?.GetValue("Commit") is not string commit || key.GetValue("Folder") is not string folder || files is null)
        {
            return null;
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in files.GetValueNames())
        {
            if (files.GetValue(name) is string hash)
            {
                hashes[name] = hash;
            }
        }

        return new ApprovedBuild(profileId, commit, folder, hashes);
    }

    public void Set(ApprovedBuild build)
    {
        Remove(build.ProfileId);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey($@"{keyPath}\{build.ProfileId}", writable: true);
        key.SetValue("Commit", build.Commit, RegistryValueKind.String);
        key.SetValue("Folder", build.Folder, RegistryValueKind.String);
        using var files = key.CreateSubKey("Files", writable: true);
        foreach (var (name, hash) in build.Files)
        {
            files.SetValue(name, hash, RegistryValueKind.String);
        }
    }

    public void Remove(string profileId)
    {
        if (!ApprovedBuilds.IsProfileId(profileId))
        {
            return;
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree($@"{keyPath}\{profileId}", throwOnMissingSubKey: false);
    }
}
