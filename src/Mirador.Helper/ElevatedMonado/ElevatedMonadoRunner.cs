using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>
/// The real <see cref="IElevatedMonado"/> (Mirador.md section 4.10 item 46): one per machine, keyed by the caller's
/// SID. It runs only the bundled <c>monado\server\monado-service.exe</c> found from the helper's own install folder,
/// with the environment the helper builds (<see cref="ElevatedEnvironment"/>), in the caller's session with the
/// caller's linked (elevated) token, inside a kill-on-close job that also ends when the caller process exits. Its
/// Win32 path runs only on a PC; the decision logic it leans on is unit-tested.
/// </summary>
public sealed class ElevatedMonadoRunner : IElevatedMonado, IDisposable
{
    private const string BundledMissing =
        "This Monado has no monado-service.exe, so it can't be started as administrator. Install the bundled Monado again, or approve the build again, on the Profiles page.";

    private const string NoSteamVr =
        "SteamVR isn't installed, so Monado can't track the headset as administrator. Install SteamVR, or turn off running Monado as administrator.";

    private const string CantOpenUser =
        "Mirador's helper service couldn't open the signed-in user's session to start Monado as administrator.";

    private readonly ElevatedConsent consent;
    private readonly Func<string, string?> signer;
    private readonly string installDir;
    private readonly IMonadoInstaller? monado;
    private readonly IApprovedBuildStore? approved;
    private readonly string logDir;
    private readonly ConcurrentDictionary<string, ElevatedService> sessions = new();
    private readonly object gate = new();

    /// <param name="consent">Where users' consent lives, with the SteamVR files each allowed; the caller's SID must be allowed.</param>
    /// <param name="signer">The Authenticode signer subject of a file; the real one is <see cref="WinTrustSigner.SubjectOf"/>.</param>
    /// <param name="monado">The bundled Monado: its current version is the one started.</param>
    /// <param name="approved">The builds owners approved (to run as administrator, or to be the OpenXR runtime).</param>
    public ElevatedMonadoRunner(ElevatedConsent consent, Func<string, string?>? signer = null, IMonadoInstaller? monado = null, IApprovedBuildStore? approved = null)
    {
        this.approved = approved;
        this.consent = consent;
        this.signer = signer ?? WinTrustSigner.SubjectOf;
        this.monado = monado;
        installDir = HelperHandler.DefaultInstallDir;
        logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mirador", "monado");
    }

    public HelperReply Start(ElevatedStart start, CallerInfo caller)
    {
        lock (gate)
        {
            try
            {
                return StartCore(start, caller);
            }
            catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Fail($"Mirador's helper service couldn't start Monado as administrator ({e.Message.Trim().TrimEnd('.')}).");
            }
        }
    }

    public HelperReply Stop(CallerInfo caller)
    {
        if (sessions.TryGetValue(caller.Sid.Value, out var service))
        {
            service.Stop();
        }

        return new HelperReply(true);
    }

    public HelperReply Wait(CallerInfo caller)
    {
        if (!sessions.TryGetValue(caller.Sid.Value, out var service))
        {
            // Nothing started (or it ended and was replaced): report ended so the caller stops polling.
            return new HelperReply(true, Value: "0");
        }

        var code = service.ExitCodeIfEnded();
        return new HelperReply(true, Value: code?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        foreach (var service in sessions.Values)
        {
            service.Dispose();
        }

        sessions.Clear();
    }

    private HelperReply StartCore(ElevatedStart start, CallerInfo caller)
    {
        var sid = caller.Sid.Value;
        var consented = consent.IsAllowed(caller.Sid);

        // Cheap checks first, before touching any token: consent and that the caller is the installed Mirador.
        if (ElevatedRequestCheck.Check(consented, caller.ImagePath, installDir, hasLinkedToken: true, running: false) is { } pre)
        {
            return Fail(pre);
        }

        if (!WtsQueryUserToken((uint)caller.SessionId, out var userToken) || userToken.IsInvalid)
        {
            userToken?.Dispose();
            return Fail(CantOpenUser);
        }

        using (userToken)
        {
            // The caller's SID must be the session user's, checked before the linked token is used.
            using (var identity = new WindowsIdentity(userToken.DangerousGetHandle()))
            {
                if (identity.User != caller.Sid)
                {
                    return Fail(ElevatedRequestCheck.NotTheUser);
                }
            }

            using var linked = LinkedPrimaryToken(userToken);
            var running = sessions.TryGetValue(sid, out var existing) && !existing.HasEnded;
            if (ElevatedRequestCheck.Check(consented, caller.ImagePath, installDir, hasLinkedToken: linked is { IsInvalid: false }, running) is { } refusal)
            {
                return Fail(refusal);
            }

            // Mirador's Monado's current version, read at each Start: an update or a roll back may have changed it. Or a
            // profile's approved build, every file checked against its approval at each Start.
            string serverDir;
            if (start.Build is { Length: > 0 } buildProfile)
            {
                // The caller's own approval only: the record is per user.
                if (approved is null || !ApprovedBuilds.IsProfileId(buildProfile) || approved.Get(caller.Sid.Value, buildProfile) is not { } build)
                {
                    return Fail("This profile's build isn't approved to run as administrator.");
                }

                if (ApprovedBuilds.Verify(build, installDir) is { } problem)
                {
                    return Fail(problem);
                }

                serverDir = build.ServerDir;
            }
            else
            {
                if (monado?.CurrentServerDir() is not { } current)
                {
                    return Fail(BundledMissing);
                }

                serverDir = current;
            }

            var serviceExe = Path.Combine(serverDir, "monado-service.exe");
            if (!File.Exists(serviceExe))
            {
                return Fail(BundledMissing);
            }

            var folders = ReadUserFolders(userToken);
            var lighthouse = start.Module is PimaxEnvironment.ModuleId;

            IDisposable hold = EmptyHold.Instance;
            string steamVrPath;
            if (lighthouse)
            {
                var steamPath = ReadCallerSteamPath(userToken);
                var found = SteamVrDriverCheck.Find(steamPath, ReadTextOrNull, Directory.Exists);
                if (found is null)
                {
                    return Fail(NoSteamVr);
                }

                // Each DLL must be Valve's, or the very copy this caller allowed: the list is read for the pipe client's
                // own SID (never from the request), and each hash is taken from the handle that is held until Monado is up.
                var check = SteamVrDriverCheck.Verify(found, signer, consent.ApprovedFiles(caller.Sid));
                if (check.Refusal is not null || check.SteamVrPath is null)
                {
                    check.Hold.Dispose();
                    return check.Refusal ?? Fail("Mirador couldn't tell where SteamVR's Lighthouse driver really is.");
                }

                // Monado is told the folder the held files are really in, never the path found through the user's Steam
                // settings: a junction on that path could be pointed elsewhere after the check.
                hold = check.Hold;
                steamVrPath = check.SteamVrPath;
            }
            else
            {
                // A path that doesn't exist, under the server folder: Monado never finds SteamVR through the user's HKCU.
                steamVrPath = Path.Combine(serverDir, "SteamVR-not-used");
            }

            try
            {
                IReadOnlyDictionary<string, string> env;
                try
                {
                    env = ElevatedEnvironment.Build(start, folders, serverDir, steamVrPath, AdministratorProtection.IsOn());
                }
                catch (ArgumentException e)
                {
                    return Fail(e.Message);
                }

                var logFile = PrepareLogFile(caller.Sid);
                var pipeName = MonadoPipeName.NameFor(env, AnsiCodePage.RoundTrip);
                var service = ElevatedService.Launch(serviceExe, serverDir, env, linked!, caller.ProcessId, logFile, hold, pipeName);
                hold = EmptyHold.Instance; // the service owns the hold now
                if (sessions.TryRemove(sid, out var previous))
                {
                    previous.Dispose();
                }

                sessions[sid] = service;
                return new HelperReply(true, Value: service.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            finally
            {
                hold.Dispose();
            }
        }
    }

    /// <summary>
    /// The caller's own folders and temp, from its environment block (<see cref="UserFolders.FromBlock"/>). Not
    /// <c>GetTempPath2</c> while impersonating: that reads the helper's own (SYSTEM) environment, never the user's.
    /// </summary>
    private static UserFolders ReadUserFolders(SafeAccessTokenHandle userToken) =>
        UserFolders.FromBlock(EnvironmentBlock(userToken));

    private static Dictionary<string, string> EnvironmentBlock(SafeAccessTokenHandle userToken)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!CreateEnvironmentBlock(out var block, userToken, false))
        {
            return found;
        }

        try
        {
            var length = 0;
            while (Marshal.ReadInt16(block, length * 2) != 0 || Marshal.ReadInt16(block, (length + 1) * 2) != 0)
            {
                length++;
            }

            var text = Marshal.PtrToStringUni(block, length + 1) ?? "";
            foreach (var entry in text.Split('\0'))
            {
                var eq = entry.IndexOf('=');
                if (eq > 0)
                {
                    found[entry[..eq]] = entry[(eq + 1)..];
                }
            }
        }
        finally
        {
            DestroyEnvironmentBlock(block);
        }

        return found;
    }

    private static string? ReadCallerSteamPath(SafeAccessTokenHandle userToken) =>
        AsUser(userToken, () =>
        {
            if (RegOpenCurrentUser(0x20019 /* KEY_READ */, out var hive) != 0)
            {
                return null;
            }

            try
            {
                using var root = RegistryKey.FromHandle(new SafeRegistryHandle(hive, ownsHandle: true));
                using var key = root.OpenSubKey(@"Software\Valve\Steam");
                return key?.GetValue("SteamPath") as string;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        });

    /// <summary>Runs <paramref name="read"/> while impersonating the caller, then reverts; null when impersonation fails.</summary>
    private static T? AsUser<T>(SafeAccessTokenHandle userToken, Func<T?> read)
    {
        if (!ImpersonateLoggedOnUser(userToken))
        {
            return default;
        }

        try
        {
            return read();
        }
        finally
        {
            RevertToSelf();
        }
    }

    /// <summary>The caller's linked (elevated) token as a primary token, or null when there is none (a standard account).</summary>
    private static SafeAccessTokenHandle? LinkedPrimaryToken(SafeAccessTokenHandle userToken)
    {
        const int TokenLinkedToken = 19;
        var size = IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(userToken, TokenLinkedToken, buffer, (uint)size, out _))
            {
                return null;
            }

            var linked = Marshal.ReadIntPtr(buffer);
            if (linked == IntPtr.Zero)
            {
                return null;
            }

            using var impersonation = new SafeAccessTokenHandle(linked);
            const uint TokenAllAccess = 0xF01FF;
            const int SecurityImpersonation = 2;
            const int TokenPrimary = 1;
            if (!DuplicateTokenEx(impersonation, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
            {
                return null;
            }

            return primary;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes <c>%ProgramData%\Mirador\monado</c> with SYSTEM and Administrators full control and the caller read only,
    /// inheritance off, and empties <c>&lt;sid&gt;.log</c>. A folder that already exists but isn't a plain directory
    /// owned by SYSTEM or Administrators is refused (never followed, deleted or recreated by SYSTEM).
    /// </summary>
    private string PrepareLogFile(SecurityIdentifier sid)
    {
        var parent = Directory.GetParent(logDir)!.FullName;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // A missing folder is made with its protected DACL in the same call, so it never has a moment with
        // %ProgramData%'s inherited rights (Users may write data and attributes there, enough to turn an empty folder
        // into a junction). Each folder is checked again after it is made: one someone else made first is refused.
        var parentSecurity = AdminOnly(system, admins);
        parentSecurity.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        CheckNotTampered(parent);
        if (!Directory.Exists(parent))
        {
            new DirectoryInfo(parent).Create(parentSecurity);
        }

        CheckNotTampered(parent);

        var security = AdminOnly(system, admins);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        CheckNotTampered(logDir);
        if (!Directory.Exists(logDir))
        {
            new DirectoryInfo(logDir).Create(security);
        }

        CheckNotTampered(logDir);

        // Only now, on a plain directory owned by SYSTEM or Administrators that no user can write to, is the DACL
        // applied again (to add this caller's read).
        new DirectoryInfo(logDir).SetAccessControl(security);

        var logFile = Path.Combine(logDir, sid.Value + ".log");
        File.WriteAllText(logFile, "");
        return logFile;
    }

    /// <summary>SYSTEM and Administrators full control, inheritance off, owner SYSTEM.</summary>
    private static DirectorySecurity AdminOnly(SecurityIdentifier system, SecurityIdentifier admins)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(system);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Refuses a folder that is a link/junction or isn't owned by SYSTEM or Administrators.</summary>
    private static void CheckNotTampered(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var info = new DirectoryInfo(folder);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"The log folder {folder} is a link or junction; Mirador won't write Monado's log through it. Delete it.");
        }

        var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (owner != system && owner != admins)
        {
            throw new UnauthorizedAccessException($"The log folder {folder} isn't owned by the system; Mirador won't write Monado's log there. Delete it.");
        }
    }

    private static HelperReply Fail(string message) => new(false, message);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    private static bool WtsQueryUserToken(uint sessionId, out SafeAccessTokenHandle token) => WTSQueryUserToken(sessionId, out token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, uint length, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateLoggedOnUser(SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegOpenCurrentUser(uint access, out IntPtr key);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    /// <summary>A hold that holds nothing (no SteamVR to keep).</summary>
    private sealed class EmptyHold : IDisposable
    {
        public static EmptyHold Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
