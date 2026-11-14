using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mirador.Ipc;

/// <summary>
/// A build a user approved to run as administrator or as the machine's OpenXR runtime (Mirador.md §4.11 item 51): a
/// copy in <c>{app}\builds\&lt;user SID&gt;\&lt;profile&gt;\&lt;commit&gt;\</c>, which only administrators can change, and
/// the SHA-256 of each of its files, kept in <c>HKLM\SOFTWARE\Mirador\ApprovedBuilds\&lt;user SID&gt;\&lt;profile&gt;</c>.
/// Per user: profile ids such as <c>custom</c> are the same for everyone, and one user's approval is not another's.
/// </summary>
/// <param name="Owner">The SID of the user who approved it.</param>
/// <param name="ProfileId">The profile the build is for.</param>
/// <param name="Commit">The commit it was built from (or <c>folder</c> for a build folder with no git).</param>
/// <param name="Folder">The approved copy.</param>
/// <param name="Files">Each file of the copy, by its path relative to <see cref="Folder"/>, with its SHA-256 (lower case).</param>
public sealed record ApprovedBuild(string Owner, string ProfileId, string Commit, string Folder, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The copy's runtime manifest, which Mirador writes itself at the approval (it names the copy's own DLL).</summary>
    public string Manifest => Path.Combine(Folder, "openxr_monado.json");

    /// <summary>The copy's service folder.</summary>
    public string ServerDir => Path.Combine(Folder, "bin");

    /// <summary>
    /// Where the copy's runtime DLL looks for the service when a game finds no service running: Mirador's stand-in,
    /// which the approval puts there.
    /// </summary>
    public string StandInDir => Path.Combine(ServerDir, "service");
}

/// <summary>Where approved builds are recorded; only an administrator can write it.</summary>
public interface IApprovedBuildStore
{
    ApprovedBuild? Get(string owner, string profileId);

    void Set(ApprovedBuild build);

    void Remove(string owner, string profileId);

    /// <summary>The profiles <paramref name="owner"/> has an approval recorded for.</summary>
    IReadOnlyList<string> ProfilesOf(string owner);
}

/// <summary>
/// Approving a build and checking an approved one. <see cref="Approve"/> runs elevated, in
/// Mirador's <c>--approve-build</c> step after one UAC prompt; the helper only reads and checks (<see cref="Verify"/>)
/// before every elevated Start and before making the copy the OpenXR runtime.
/// </summary>
public static partial class ApprovedBuilds
{
    /// <summary>The DLL the approved copy's manifest names, relative to the copy.</summary>
    public static readonly string RuntimeDll = Path.Combine("bin", "openxr_monado.dll");

    /// <summary>The folder of all approved builds below Mirador's install folder.</summary>
    public static string Root(string appDir) => Path.Combine(appDir, "builds");

    /// <summary>The folder of one user's approved builds of one profile.</summary>
    public static string ProfileRoot(string appDir, string owner, string profileId) => Path.Combine(Root(appDir), owner, profileId);

    /// <summary>Whether <paramref name="id"/> is a profile id (a GUID's digits or a default profile's name): never a path.</summary>
    public static bool IsProfileId(string? id) => id is not null && ProfileIdPattern().IsMatch(id);

    /// <summary>Whether <paramref name="sid"/> looks like a SID: never a path.</summary>
    public static bool IsOwner(string? sid) => sid is not null && OwnerPattern().IsMatch(sid);

    /// <summary>
    /// Copies the install in <paramref name="source"/> to <c>{appDir}\builds\&lt;owner&gt;\&lt;profile&gt;\&lt;commit&gt;\</c>,
    /// writes the copy's runtime manifest itself (naming the copy's own DLL, whatever the build's manifest said), hashes
    /// the copy and records it. Only then do the profile's earlier copies go, as far as they can (one still in use stays
    /// until the next approval). A link or junction anywhere in the source is refused: it could point the copy at files
    /// outside the build.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source can't be approved; the message says why.</exception>
    public static ApprovedBuild Approve(string owner, string profileId, string commit, string source, string appDir, IApprovedBuildStore store)
    {
        if (!IsOwner(owner) || !IsProfileId(profileId))
        {
            throw new InvalidOperationException("That isn't a user's profile.");
        }

        if (!CommitPattern().IsMatch(commit))
        {
            throw new InvalidOperationException("That isn't a commit.");
        }

        var from = Path.GetFullPath(source);
        foreach (var file in new[] { "openxr_monado.json", RuntimeDll, Path.Combine("bin", "monado-service.exe") })
        {
            if (!File.Exists(Path.Combine(from, file)))
            {
                throw new InvalidOperationException($"The build in {from} has no {file}: build it again first.");
            }
        }

        var profileRoot = ProfileRoot(appDir, owner, profileId);
        var target = Path.Combine(profileRoot, commit);
        var staging = target + ".new";
        Cleanup.TryDeleteDirectory(staging);
        Copy(new DirectoryInfo(from), staging);
        WriteManifest(staging);
        PlaceStandIn(appDir, staging);
        var files = Hash(staging);

        // The new copy in place and recorded first; an earlier copy of the same commit is moved aside whole.
        if (Directory.Exists(target))
        {
            Directory.Move(target, target + ".old-" + Guid.NewGuid().ToString("N"));
        }

        Directory.Move(staging, target);
        var build = new ApprovedBuild(owner, profileId, commit, target, files);
        store.Set(build);

        foreach (var old in Directory.GetDirectories(profileRoot).Where(d => !string.Equals(d, target, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveWhole(old, appDir);
        }

        SweepLeftovers(appDir, store);
        return build;
    }

    /// <summary>
    /// Takes an approval back: its record first (from then on nothing runs it), then its copy, as far as it can go now
    /// (a copy still in use stays until the next approval or the uninstall).
    /// </summary>
    public static void Forget(string owner, string profileId, string appDir, IApprovedBuildStore store)
    {
        if (!IsOwner(owner) || !IsProfileId(profileId))
        {
            return;
        }

        store.Remove(owner, profileId);
        RemoveWhole(ProfileRoot(appDir, owner, profileId), appDir);
        SweepLeftovers(appDir, store);
    }

    /// <summary>
    /// Null when <paramref name="build"/> is still exactly what was approved: in its place under
    /// <paramref name="appDir"/>, its manifest naming its own DLL, every recorded file there with its recorded hash, and
    /// no program or library added. Otherwise why not, in plain words.
    /// </summary>
    public static string? Verify(ApprovedBuild build, string appDir)
    {
        if (!IsOwner(build.Owner) || !IsProfileId(build.ProfileId) || !CommitPattern().IsMatch(build.Commit)
            || !string.Equals(Path.GetFullPath(build.Folder), Path.GetFullPath(Path.Combine(ProfileRoot(appDir, build.Owner, build.ProfileId), build.Commit)), StringComparison.OrdinalIgnoreCase))
        {
            return "The approved build's record doesn't name its own folder. Approve the build again.";
        }

        if (!Directory.Exists(build.Folder) || new DirectoryInfo(build.Folder).LinkTarget is not null)
        {
            return "The approved build is gone. Approve the build again.";
        }

        var root = Path.GetFullPath(build.Folder) + Path.DirectorySeparatorChar;
        foreach (var (relative, hash) in build.Files)
        {
            var path = Path.GetFullPath(Path.Combine(build.Folder, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || !string.Equals(HashOf(path), hash, StringComparison.OrdinalIgnoreCase))
            {
                return $"The approved build changed since it was approved ({relative}). Approve it again.";
            }
        }

        if (!build.Files.ContainsKey("openxr_monado.json") || LibraryOf(build.Manifest) is not { } library
            || !string.Equals(library, Path.Combine(build.Folder, RuntimeDll), StringComparison.OrdinalIgnoreCase)
            || !build.Files.ContainsKey(RuntimeDll))
        {
            return "The approved build's manifest doesn't name its own DLL. Approve the build again.";
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
    /// <param name="hashOf">The SHA-256 of a file, lower case: <see cref="HashOf"/>, or a cache in front of it.</param>
    public static bool Matches(ApprovedBuild build, string sourceInstall, Func<string, string>? hashOf = null)
    {
        hashOf ??= HashOf;
        foreach (var relative in new[] { RuntimeDll, Path.Combine("bin", "monado-service.exe") })
        {
            var file = Path.Combine(sourceInstall, relative);
            if (!build.Files.TryGetValue(relative, out var hash) || !File.Exists(file)
                || !string.Equals(hashOf(file), hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the copy's manifest names the copy's own DLL in a way OpenXR programs can load (a quick look, for the app;
    /// <see cref="Verify"/> checks every file): a path with forward slashes isn't, so such a copy counts as not approved.
    /// </summary>
    public static bool NamesOwnDll(ApprovedBuild build) =>
        LibraryOf(build.Manifest) is { } library && string.Equals(library, Path.Combine(build.Folder, RuntimeDll), StringComparison.OrdinalIgnoreCase);

    /// <summary>The SHA-256 of a file, lower case.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// The copy's manifest, written by Mirador: a build's own may name a DLL anywhere (an absolute path into the user's
    /// build folder), and every OpenXR program would load that. The path has backslashes: the OpenXR loader joins it to
    /// the manifest's folder and loads it, and with forward slashes in that path Windows doesn't look in the DLL's own
    /// folder for the DLLs it needs, so it fails to load.
    /// </summary>
    private static void WriteManifest(string folder) =>
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"),
            """{"file_format_version": "1.0.0", "runtime": {"name": "Monado (Mirador, approved build)", "library_path": "bin\\openxr_monado.dll"}}""");

    /// <summary>
    /// Mirador's own stand-in (<c>{app}\standin</c>) where the copy's runtime DLL looks for the service, whatever the build
    /// had there: a game that starts while Monado is stopped then asks Mirador, as with any other Monado. Without it in
    /// Mirador's folder (a development copy), the copy keeps what the build had.
    /// </summary>
    private static void PlaceStandIn(string appDir, string copy)
    {
        var standIn = Path.Combine(appDir, "standin", StandInNames.FileName);
        if (File.Exists(standIn))
        {
            var dir = Path.Combine(copy, "bin", "service");
            Directory.CreateDirectory(dir);
            foreach (var name in StandInNames.FileNames)
            {
                File.Copy(standIn, Path.Combine(dir, name), overwrite: true);
            }
        }
    }

    /// <summary>
    /// The full path of the DLL a manifest names, or null when it can't be read or can't be loaded as written (a forward
    /// slash: see <see cref="WriteManifest"/>).
    /// </summary>
    private static string? LibraryOf(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var library = doc.RootElement.GetProperty("runtime").GetProperty("library_path").GetString();
            return library is null || Path.IsPathRooted(library) || library.Contains('/') ? null : Path.GetFullPath(library, Path.GetDirectoryName(manifest)!);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsCode(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".manifest" or ".ps1" or ".bat" or ".cmd";

    private static void Copy(DirectoryInfo from, string to) => CopyUnder(from, to, FinalPath(from.FullName, folder: true));

    // The checks by path below refuse a link that is there when they look; the step runs elevated while the user's own
    // programs can still change the build folder, so each file is also opened once and judged by where its handle
    // really is (a folder swapped for a junction after its check leads outside the build's own folder), and its bytes
    // are copied from that same handle.
    private static void CopyUnder(DirectoryInfo from, string to, string root)
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

            using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!IsUnder(FinalPath(source.SafeFileHandle), root))
            {
                throw new InvalidOperationException($"{file.FullName} leads outside the build's folder: Mirador approves only a build's own files.");
            }

            using var target = new FileStream(Path.Combine(to, file.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }

        foreach (var dir in from.EnumerateDirectories())
        {
            CopyUnder(dir, Path.Combine(to, dir.Name), root);
        }
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a folder really is, links and junctions on its way resolved.</summary>
    private static string FinalPath(string path, bool folder)
    {
        const uint ReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
        using var handle = CreateFileW(path, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, folder ? BackupSemantics : 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException($"{path} can't be opened (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
        }

        return FinalPath(handle);
    }

    /// <summary>Where an open file or folder really is, as a plain path (without <c>\\?\</c>).</summary>
    private static string FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }

        if (length == 0 || length > buffer.Length)
        {
            throw new InvalidOperationException($"Mirador couldn't tell where a file of the build is (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
        }

        var path = new string(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, [System.Runtime.InteropServices.Out] char[] path, uint length, uint flags);

    private static Dictionary<string, string> Hash(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f), HashOf, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Moves <paramref name="folder"/> aside whole (into <c>{app}\builds\.old-*</c>), then deletes it. A copy the
    /// elevated Monado runs from (its working folder is inside) or with a file held open can't be moved, so it stays
    /// whole, never half deleted, until a later approval or forget sweeps it (<see cref="SweepLeftovers"/>) or the
    /// uninstall. A game that has only loaded the copy's DLL doesn't stop the move: the DLL stays behind aside.
    /// </summary>
    private static void RemoveWhole(string folder, string appDir)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var aside = Path.Combine(Root(appDir), ".old-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(folder, aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Cleanup.TryDeleteDirectory(aside);
    }

    /// <summary>
    /// What an earlier <see cref="RemoveWhole"/> moved aside but couldn't delete, and, of every user, the profile folders
    /// that no approval is recorded for any more (one that couldn't be moved when it was forgotten).
    /// </summary>
    private static void SweepLeftovers(string appDir, IApprovedBuildStore store)
    {
        var root = Root(appDir);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var left in Directory.GetDirectories(root, ".old-*"))
        {
            Cleanup.TryDeleteDirectory(left);
        }

        foreach (var user in Directory.GetDirectories(root).Where(d => IsOwner(Path.GetFileName(d))))
        {
            var owner = Path.GetFileName(user);
            foreach (var profile in Directory.GetDirectories(user))
            {
                if (IsProfileId(Path.GetFileName(profile)) && store.Get(owner, Path.GetFileName(profile)) is null)
                {
                    RemoveWhole(profile, appDir);
                }
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex ProfileIdPattern();

    [GeneratedRegex(@"^S-1(-[0-9]{1,10}){1,15}\z")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^([0-9a-f]{7,40}|folder)\z")]
    private static partial Regex CommitPattern();
}

/// <summary>
/// The approved builds in <c>HKLM\SOFTWARE\Mirador\ApprovedBuilds</c> (64-bit view): a key per user SID and profile with
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

    public ApprovedBuild? Get(string owner, string profileId)
    {
        if (!ApprovedBuilds.IsOwner(owner) || !ApprovedBuilds.IsProfileId(profileId))
        {
            return null;
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{keyPath}\{owner}\{profileId}");
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

        return new ApprovedBuild(owner, profileId, commit, folder, hashes);
    }

    public void Set(ApprovedBuild build)
    {
        Remove(build.Owner, build.ProfileId);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey($@"{keyPath}\{build.Owner}\{build.ProfileId}", writable: true);
        key.SetValue("Commit", build.Commit, RegistryValueKind.String);
        key.SetValue("Folder", build.Folder, RegistryValueKind.String);
        using var files = key.CreateSubKey("Files", writable: true);
        foreach (var (name, hash) in build.Files)
        {
            files.SetValue(name, hash, RegistryValueKind.String);
        }
    }

    public void Remove(string owner, string profileId)
    {
        if (!ApprovedBuilds.IsOwner(owner) || !ApprovedBuilds.IsProfileId(profileId))
        {
            return;
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree($@"{keyPath}\{owner}\{profileId}", throwOnMissingSubKey: false);
    }

    public IReadOnlyList<string> ProfilesOf(string owner)
    {
        if (!ApprovedBuilds.IsOwner(owner))
        {
            return [];
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{keyPath}\{owner}");
        return key is null ? [] : [.. key.GetSubKeyNames().Where(ApprovedBuilds.IsProfileId)];
    }
}
