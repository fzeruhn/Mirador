using System.Security.Principal;
using Mirador.Ipc;

namespace Mirador.Core.Elevation;

/// <summary>
/// <c>Mirador.exe --allow-elevated-monado</c>: the elevated step that records the running user's consent to run Monado
/// as administrator. It runs through UAC (an administrator approved it) as the user themselves, so the SID it records
/// is the user's own and nothing is taken from the command line.
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
    /// Allows <paramref name="user"/>. Returns 0, or 1 after handing the reason to <paramref name="logError"/> (the
    /// caller writes it to <c>mirador.log</c>). Never throws: this runs elevated and its exit code is all the caller learns.
    /// </summary>
    public static int Run(ElevatedConsent consent, SecurityIdentifier? user, Action<string> logError)
    {
        if (user is null)
        {
            logError("Mirador couldn't tell which user is running it, so Monado was not allowed to run as administrator.");
            return Failed;
        }

        try
        {
            consent.Allow(user);
            return Done;
        }
        catch (Exception e)
        {
            logError($"Mirador couldn't record the consent to run Monado as administrator ({e.Message.Trim().TrimEnd('.')}).");
            return Failed;
        }
    }
}
