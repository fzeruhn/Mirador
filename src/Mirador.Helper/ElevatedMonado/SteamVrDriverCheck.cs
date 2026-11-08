using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>
/// Finds SteamVR as Monado does, then checks and holds its Lighthouse driver DLLs (Mirador.md section 4.10 item 46).
/// Steam's folder is writable by every user, so before Monado loads <c>driver_lighthouse.dll</c> and the DLLs beside
/// it, the helper opens each without write or delete sharing (so none can be swapped or its folder renamed) and checks
/// that every one is signed by Valve Corp. The handles stay open in the returned <c>Hold</c> until Monado is up or the
/// timeout; a failed check returns no handles and the reason.
/// </summary>
public static class SteamVrDriverCheck
{
    /// <summary>
    /// SteamVR's folder as Monado's <c>steamvr_lh</c> finds it: <c>STEAMVR_PATH</c> is not used here (the helper never
    /// trusts the request's environment), only the caller's Steam folder and its <c>libraryfolders.vdf</c>.
    /// </summary>
    public static string? Find(string? steamPath, Func<string, string?> readFile, Func<string, bool> dirExists) =>
        SteamVr.FindInstall(steamPath, steamVrPathEnv: null, readFile, dirExists);

    /// <summary>
    /// Opens every <c>*.dll</c> in <c>drivers\lighthouse\bin\win64</c> without write or delete sharing and checks its
    /// Authenticode signer subject (through <paramref name="signer"/>) is Valve's (<see cref="IsValve"/>). On success the
    /// open handles are returned in <c>Hold</c>; on any problem the handles opened so far are released and the reason is
    /// returned.
    /// </summary>
    public static (IDisposable Hold, string? Problem) Verify(string steamVrPath, Func<string, string?> signer)
    {
        var win64 = SteamVr.DriverLighthouseBinDir(steamVrPath);
        if (!Directory.Exists(win64))
        {
            return (new Holder(), $"SteamVR's Lighthouse driver folder is missing ({win64}). Install or repair SteamVR.");
        }

        string[] dlls;
        try
        {
            dlls = Directory.GetFiles(win64, "*.dll");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new Holder(), $"Mirador couldn't read SteamVR's driver folder {win64} ({Trim(e.Message)}). Install or repair SteamVR.");
        }

        if (dlls.Length == 0)
        {
            return (new Holder(), $"SteamVR has no Lighthouse driver in {win64}. Install or repair SteamVR.");
        }

        var holder = new Holder();
        foreach (var dll in dlls.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Read access, read sharing only: another program can read it too, but none may write to, delete or
                // rename it, or rename the folder, while the handle is open.
                holder.Add(new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                holder.Dispose();
                return (new Holder(), $"Mirador couldn't open SteamVR's driver file {Path.GetFileName(dll)} to check it ({Trim(e.Message)}).");
            }

            var subject = signer(dll);
            if (!IsValve(subject))
            {
                holder.Dispose();
                var by = subject is null ? "" : $" (its signer is {subject})";
                return (new Holder(), $"SteamVR's driver file {Path.GetFileName(dll)} isn't signed by Valve Corp.{by}. Install or repair SteamVR.");
            }
        }

        return (holder, null);
    }

    /// <summary>Valve's signing certificate names this organisation in both its CN and its O (checked 2026-11-07 on
    /// <c>driver_lighthouse.dll</c>: <c>CN=Valve Corp., O=Valve Corp., L=Bellevue, S=Washington, C=US</c>).</summary>
    internal const string ValveName = "Valve Corp.";

    private const string CommonNameOid = "2.5.4.3";
    private const string OrganizationOid = "2.5.4.10";

    /// <summary>
    /// Whether a signer subject is Valve's: parsed as a distinguished name (not compared as text, so
    /// <c>CN=Valve Corp.evil</c> or a quoted <c>CN="Valve Corp., O=Valve Corp."</c> don't pass), it must have exactly one
    /// CN and exactly one O, each equal to <see cref="ValveName"/>, wherever they sit in the name. A name Windows can't
    /// parse, or one with a multi-valued RDN, is refused.
    /// </summary>
    internal static bool IsValve(string? subject)
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

        return commonNames is [ValveName] && organizations is [ValveName];
    }

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
