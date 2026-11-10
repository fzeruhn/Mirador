using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mirador.Ipc;

/// <summary>
/// Finds SteamVR as Monado does, then checks and holds its Lighthouse driver DLLs (Mirador.md section 4.10 item 46).
/// Steam's folder is writable by every user, so before Monado loads <c>driver_lighthouse.dll</c> and the DLLs beside
/// it, the helper opens each without write or delete sharing (so none can be swapped or its folder renamed) and accepts
/// each that is signed by Valve Corp. or is the very copy the user allowed: its SHA-256, taken from the held handle,
/// equals the one recorded at the consent. The consent step (<c>--allow-elevated-monado</c>) records those hashes with
/// <see cref="UnsignedFiles"/>, through the same opening, signer check and hashing. Shared by the helper and Mirador
/// so that the two can't drift.
/// </summary>
public static class SteamVrDriverCheck
{
    /// <summary>The longest list of file names a refusal spells out; the rest are counted (a reply is one line of 4 KB).</summary>
    private const int NamesShown = 200;

    /// <summary>
    /// SteamVR's folder as Monado's <c>steamvr_lh</c> finds it: <c>STEAMVR_PATH</c> is not used here (the helper never
    /// trusts the request's environment), only the user's Steam folder and its <c>libraryfolders.vdf</c>.
    /// </summary>
    public static string? Find(string? steamPath, Func<string, string?> readFile, Func<string, bool> dirExists) =>
        SteamVr.FindInstall(steamPath, steamVrPathEnv: null, readFile, dirExists);

    /// <summary>
    /// The helper's check at an elevated Start. Opens every <c>*.dll</c> in <c>drivers\lighthouse\bin\win64</c> without
    /// write or delete sharing and keeps them open. Then, from the held handles, it reads where each file really is
    /// (<c>GetFinalPathNameByHandle</c>): <c>driver_lighthouse.dll</c>'s real folder must be laid out as
    /// <c>…\drivers\lighthouse\bin\win64</c>, every other DLL must really be in that same folder (not a link to a file
    /// elsewhere), and the SteamVR folder above it is what Monado is told (<c>SteamVrPath</c>), never the path as found
    /// through the user's Steam settings: a junction on that path could be pointed elsewhere after the check, but a
    /// folder that holds open files can't be renamed, so the real path stays put while held. Each DLL then passes when
    /// its Authenticode signer (through <paramref name="signer"/>, asked for the real path) is Valve's
    /// (<see cref="IsValve"/>), or when its SHA-256, read from the held handle, equals <paramref name="approved"/>'s line
    /// for its name. On success the open handles are returned in <c>Hold</c> and <c>Refusal</c> is null. Any DLL that
    /// passes neither way refuses with <see cref="HelperRefusal.SteamVrChanged"/>, naming every such file; a folder or
    /// file that can't be read, or a file or folder that isn't where it seems, refuses without a typed reason. A refusal
    /// releases every handle and has no <c>SteamVrPath</c>.
    /// </summary>
    /// <param name="approved">The caller's recorded lines (<see cref="ElevatedConsent.ApprovedFiles"/>), never a request's.</param>
    public static SteamVrDriverHold Verify(
        string steamVrPath, Func<string, string?> signer, IReadOnlyDictionary<string, string> approved) =>
        Verify(steamVrPath, signer, approved, FinalPath);

    /// <summary>For tests: <paramref name="finalPath"/> stands in for <c>GetFinalPathNameByHandle</c> on a held file.</summary>
    internal static SteamVrDriverHold Verify(
        string steamVrPath, Func<string, string?> signer, IReadOnlyDictionary<string, string> approved, Func<FileStream, string?> finalPath)
    {
        var win64 = SteamVr.DriverLighthouseBinDir(steamVrPath);
        if (!Directory.Exists(win64))
        {
            return Refuse($"SteamVR's Lighthouse driver folder is missing ({win64}). Install or repair SteamVR.");
        }

        string[] dlls;
        try
        {
            dlls = Dlls(win64);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse($"Mirador couldn't read SteamVR's driver folder {win64} ({Trim(e.Message)}). Install or repair SteamVR.");
        }

        if (dlls.Length == 0)
        {
            return Refuse($"SteamVR has no Lighthouse driver in {win64}. Install or repair SteamVR.");
        }

        // Every DLL held first, so that none of them, and none of the folders above them, can be swapped or renamed
        // while the rest is checked.
        var holder = new Holder();
        var held = new List<(string Name, FileStream Stream)>();
        foreach (var dll in dlls)
        {
            var name = Path.GetFileName(dll);
            try
            {
                var stream = Open(dll);
                holder.Add(stream);
                held.Add((name, stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                holder.Dispose();
                return Refuse($"Mirador couldn't open SteamVR's driver file {name} to check it ({Trim(e.Message)}).");
            }
        }

        var (realPaths, realSteamVr, problem) = Resolve(held, win64, finalPath);
        if (problem is not null || realPaths is null || realSteamVr is null)
        {
            holder.Dispose();
            return Refuse(problem ?? $"Mirador couldn't tell where SteamVR's Lighthouse driver really is ({win64}).");
        }

        var changed = new List<string>();
        for (var i = 0; i < held.Count; i++)
        {
            var (name, stream) = held[i];
            if (IsValve(signer(realPaths[i])))
            {
                continue;
            }

            string sha256;
            try
            {
                sha256 = Sha256(stream);
            }
            catch (IOException e)
            {
                holder.Dispose();
                return Refuse($"Mirador couldn't read SteamVR's driver file {name} to check it ({Trim(e.Message)}).");
            }

            if (!approved.TryGetValue(name, out var allowed) || !string.Equals(allowed, sha256, StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        }

        if (changed.Count > 0)
        {
            holder.Dispose();
            return new SteamVrDriverHold(new Holder(), new HelperReply(
                false,
                $"SteamVR's Lighthouse driver changed since you allowed Monado as administrator ({Names(changed)}).",
                Refusal: HelperRefusal.SteamVrChanged), null);
        }

        return new SteamVrDriverHold(holder, null, realSteamVr);
    }

    /// <summary>
    /// Where the held files really are: each one's real path, and the SteamVR folder three levels above
    /// <c>driver_lighthouse.dll</c>'s real <c>drivers\lighthouse\bin\win64</c>; or why not.
    /// </summary>
    private static (string[]? RealPaths, string? SteamVr, string? Problem) Resolve(
        List<(string Name, FileStream Stream)> held, string win64, Func<FileStream, string?> finalPath)
    {
        var realPaths = new string[held.Count];
        for (var i = 0; i < held.Count; i++)
        {
            if (WithoutPrefix(finalPath(held[i].Stream)) is not { Length: > 0 } real)
            {
                return (null, null, $"Mirador couldn't tell where SteamVR's driver file {held[i].Name} really is.");
            }

            realPaths[i] = real;
        }

        var driver = held.FindIndex(h => string.Equals(h.Name, "driver_lighthouse.dll", StringComparison.OrdinalIgnoreCase));
        if (driver < 0)
        {
            return (null, null, $"SteamVR has no driver_lighthouse.dll in {win64}. Install or repair SteamVR.");
        }

        var realWin64 = Path.GetDirectoryName(realPaths[driver]);
        var bin = Parent(realWin64, "win64");
        var lighthouse = Parent(bin, "bin");
        var drivers = Parent(lighthouse, "lighthouse");
        var steamVr = Parent(drivers, "drivers");
        if (realWin64 is null || steamVr is null)
        {
            return (null, null, $"SteamVR's Lighthouse driver isn't in a SteamVR folder ({realWin64 ?? realPaths[driver]}). Install or repair SteamVR.");
        }

        for (var i = 0; i < held.Count; i++)
        {
            var name = held[i].Name;
            if (!string.Equals(Path.GetDirectoryName(realPaths[i]), realWin64, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(realPaths[i]), name, StringComparison.OrdinalIgnoreCase))
            {
                return (null, null,
                    $"SteamVR's driver file {name} is a link to another file ({realPaths[i]}), so Mirador won't run Monado as administrator with it. Install or repair SteamVR.");
            }
        }

        return (realPaths, steamVr, null);
    }

    /// <summary>The folder above <paramref name="folder"/> when <paramref name="folder"/> is named <paramref name="expected"/>, else null.</summary>
    private static string? Parent(string? folder, string expected) =>
        folder is not null && string.Equals(Path.GetFileName(folder), expected, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(folder)
            : null;

    /// <summary><c>\\?\C:\…</c> as <c>C:\…</c>, and <c>\\?\UNC\server\…</c> as <c>\\server\…</c>.</summary>
    private static string? WithoutPrefix(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>The held file's real, normalised path (<c>GetFinalPathNameByHandle</c>, DOS drive letters), or null.</summary>
    private static string? FinalPath(FileStream held)
    {
        const uint FileNameNormalized = 0;
        const uint VolumeNameDos = 0;
        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(held.SafeFileHandle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            if (length > 32768)
            {
                return null;
            }

            buffer = new char[length + 1];
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, [System.Runtime.InteropServices.Out] char[] path, uint length, uint flags);

    /// <summary>
    /// The consent step's list: file name to lower-case hex SHA-256 of each <c>*.dll</c> in
    /// <c>drivers\lighthouse\bin\win64</c> whose signer isn't Valve's, each opened and checked as <see cref="Verify"/>
    /// does it and hashed from the held handle. Empty when the folder is missing (no Lighthouse driver to allow).
    /// </summary>
    /// <exception cref="IOException">The folder can't be listed, or a file in it can't be opened or read: the list would
    /// be incomplete, so there is none. The message is plain and names the file.</exception>
    public static IReadOnlyDictionary<string, string> UnsignedFiles(string steamVrPath, Func<string, string?> signer)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var win64 = SteamVr.DriverLighthouseBinDir(steamVrPath);
        if (!Directory.Exists(win64))
        {
            return files;
        }

        string[] dlls;
        try
        {
            dlls = Dlls(win64);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Mirador couldn't read SteamVR's driver folder {win64} ({Trim(e.Message)}), so Monado was not allowed to run as administrator. Try again; if it keeps failing, repair SteamVR.", e);
        }

        // All held until every one is checked, as at a Start.
        using var holder = new Holder();
        foreach (var dll in dlls)
        {
            var name = Path.GetFileName(dll);
            try
            {
                var held = Open(dll);
                holder.Add(held);
                if (!IsValve(signer(dll)))
                {
                    files[name] = Sha256(held);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Mirador couldn't open SteamVR's driver file {name} to check it ({Trim(e.Message)}), so Monado was not allowed to run as administrator. Close SteamVR and Steam, then try again.", e);
            }
        }

        return files;
    }

    /// <summary>Valve's signing certificate names this organisation in both its CN and its O (checked 2026-11-07 on
    /// <c>driver_lighthouse.dll</c>: <c>CN=Valve Corp., O=Valve Corp., L=Bellevue, S=Washington, C=US</c>).</summary>
    internal const string ValveName = "Valve Corp.";

    /// <summary>Whether a signer subject is Valve's (<see cref="SignerName.Is"/> with <see cref="ValveName"/>).</summary>
    internal static bool IsValve(string? subject) => SignerName.Is(subject, ValveName);

    /// <summary>Every <c>*.dll</c> in the folder, in a fixed order.</summary>
    private static string[] Dlls(string win64) =>
        [.. Directory.GetFiles(win64, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Read access, read sharing only: another program can read it too, but none may write to, delete or rename it, or
    /// rename its folder, while the handle is open.
    /// </summary>
    private static FileStream Open(string dll) => new(dll, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>The SHA-256 of the held file's bytes, read through its handle (not the path again), as lower-case hex.</summary>
    private static string Sha256(FileStream held)
    {
        held.Position = 0;
        var hash = SHA256.HashData(held);
        held.Position = 0;
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// The names, comma-separated, cut short (with how many more) past <see cref="NamesShown"/> characters. The first is
    /// always shown (a file name is at most 255 characters), so the line stays well inside the reply's 4 KB however
    /// the names escape in JSON.
    /// </summary>
    private static string Names(List<string> names)
    {
        var shown = new List<string>();
        var length = 0;
        foreach (var name in names)
        {
            if (shown.Count > 0 && length + name.Length > NamesShown)
            {
                break;
            }

            shown.Add(name);
            length += name.Length + 2;
        }

        var text = string.Join(", ", shown);
        return shown.Count < names.Count ? $"{text} and {names.Count - shown.Count} more" : text;
    }

    private static SteamVrDriverHold Refuse(string message) => new(new Holder(), new HelperReply(false, message), null);

    private static string Trim(string message) => message.Trim().TrimEnd('.');

    /// <summary>Holds the open driver-file handles; disposing releases them.</summary>
    private sealed class Holder : IDisposable
    {
        private readonly List<FileStream> handles = [];

        public void Add(FileStream handle) => handles.Add(handle);

        public void Dispose()
        {
            foreach (var handle in handles)
            {
                try
                {
                    handle.Dispose();
                }
                catch (IOException)
                {
                }
            }

            handles.Clear();
        }
    }
}

/// <summary>
/// What <see cref="SteamVrDriverCheck.Verify"/> found: the held driver files (release by disposing), why the elevated Start
/// is refused (null when it may go on), and the real SteamVR folder to give Monado as <c>STEAMVR_PATH</c> (null on a
/// refusal), read from the held files rather than taken from the path they were found by.
/// </summary>
public sealed record SteamVrDriverHold(IDisposable Hold, HelperReply? Refusal, string? SteamVrPath)
{
    public void Deconstruct(out IDisposable hold, out HelperReply? refusal)
    {
        hold = Hold;
        refusal = Refusal;
    }
}

/// <summary>The organisation an Authenticode signer subject (<see cref="WinTrustSigner.SubjectOf"/>) names.</summary>
public static class SignerName
{
    private const string CommonNameOid = "2.5.4.3";
    private const string OrganizationOid = "2.5.4.10";

    /// <summary>
    /// Whether <paramref name="subject"/> is <paramref name="organisation"/>'s: parsed as a distinguished name (not
    /// compared as text, so <c>CN=Valve Corp.evil</c> or a quoted <c>CN="Valve Corp., O=Valve Corp."</c> don't pass), it
    /// must have exactly one CN and exactly one O, each equal to <paramref name="organisation"/>, wherever they sit in
    /// the name. A name Windows can't parse, or one with a multi-valued RDN, is refused.
    /// </summary>
    public static bool Is(string? subject, string organisation)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        X500DistinguishedName name;
        try
        {
            name = new X500DistinguishedName(subject);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return false;
        }

        var commonNames = new List<string?>();
        var organizations = new List<string?>();
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements)
            {
                return false;
            }

            switch (rdn.GetSingleElementType().Value)
            {
                case CommonNameOid:
                    commonNames.Add(rdn.GetSingleElementValue());
                    break;
                case OrganizationOid:
                    organizations.Add(rdn.GetSingleElementValue());
                    break;
            }
        }

        return commonNames.Count == 1 && organizations.Count == 1 && commonNames[0] == organisation && organizations[0] == organisation;
    }
}
