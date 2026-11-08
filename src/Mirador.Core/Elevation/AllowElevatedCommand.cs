using System.Security.Principal;
using Mirador.Ipc;

namespace Mirador.Core.Elevation;

/// <summary>
/// <c>Mirador.exe --allow-elevated-monado</c>: the elevated step that records the running user's consent to run Monado
/// as administrator, with SteamVR's Lighthouse DLLs that Valve didn't sign as they are now (Mirador.md section 4.10
/// item 46). It runs through UAC (an administrator approved it) as the user themselves, so the SID it records is the
/// user's own and nothing is taken from the command line.
/// </summary>
public static class AllowElevatedCommand
{
    public const string Flag = "--allow-elevated-monado";

    internal const int Done = 0;
    internal const int Failed = 1;

    /// <summary>The running user's SID, or null when Windows doesn't say.</summary>
    public static SecurityIdentifier? CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    /// <summary>
    /// The unsigned Lighthouse files on this PC, found as the helper finds them: the running user's <c>SteamPath</c>
    /// (<c>HKCU\Software\Valve\Steam</c>), then its <c>libraryfolders.vdf</c>, never <c>STEAMVR_PATH</c>; signers through
    /// <see cref="WinTrustSigner.SubjectOf"/>, as the helper checks them.
    /// </summary>
    /// <exception cref="IOException">A file in the driver folder couldn't be opened or read; the message is plain.</exception>
    public static IReadOnlyDictionary<string, string> UnsignedLighthouseFiles() =>
        UnsignedLighthouseFiles(SteamVr.SteamPathFromRegistry(), ReadTextOrNull, Directory.Exists, WinTrustSigner.SubjectOf);

    /// <summary>
    /// Name to SHA-256 of every DLL in SteamVR's Lighthouse driver folder whose signer isn't Valve Corp.
    /// (<see cref="SteamVrDriverCheck.UnsignedFiles"/>, the helper's own check). Empty when SteamVR isn't found: the
    /// consent still holds for profiles without Lighthouse.
    /// </summary>
    /// <exception cref="IOException">A file in the driver folder couldn't be opened or read: the list would be incomplete.</exception>
    public static IReadOnlyDictionary<string, string> UnsignedLighthouseFiles(
        string? steamPath, Func<string, string?> readFile, Func<string, bool> dirExists, Func<string, string?> signer)
    {
        if (SteamVrDriverCheck.Find(steamPath, readFile, dirExists) is not { } steamVr)
        {
            return new Dictionary<string, string>();
        }

        return SteamVrDriverCheck.UnsignedFiles(steamVr, signer);
    }

    /// <summary>
    /// Allows <paramref name="user"/>, with the files <paramref name="unsignedFiles"/> finds (it replaces the list
    /// recorded before). Returns 0, or 1 after handing the reason to <paramref name="logError"/> (the caller writes it
    /// to <c>mirador.log</c>). When the files can't all be checked, nothing is recorded and it returns 1: an approval with
    /// part of the list must not look like one that worked. Never throws: this runs elevated and its exit code is all the
    /// caller learns.
    /// </summary>
    public static int Run(
        ElevatedConsent consent, SecurityIdentifier? user, Func<IReadOnlyDictionary<string, string>> unsignedFiles, Action<string> logError)
    {
        if (user is null)
        {
            logError("Mirador couldn't tell which user is running it, so Monado was not allowed to run as administrator.");
            return Failed;
        }

        IReadOnlyDictionary<string, string> files;
        try
        {
            files = unsignedFiles();
        }
        catch (IOException e)
        {
            // Already a plain sentence naming the file.
            logError(e.Message);
            return Failed;
        }
        catch (Exception e)
        {
            logError($"Mirador couldn't check SteamVR's Lighthouse files ({e.Message.Trim().TrimEnd('.')}), so Monado was not allowed to run as administrator.");
            return Failed;
        }

        try
        {
            consent.Allow(user, files);
            return Done;
        }
        catch (Exception e)
        {
            logError($"Mirador couldn't record the consent to run Monado as administrator ({e.Message.Trim().TrimEnd('.')}).");
            return Failed;
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
}
