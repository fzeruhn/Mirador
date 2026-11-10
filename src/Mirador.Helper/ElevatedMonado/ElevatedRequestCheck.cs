namespace Mirador.Helper.ElevatedMonado;

/// <summary>Decides whether an elevated-start request may go ahead, and if not why.</summary>
public static class ElevatedRequestCheck
{
    public const string NotAllowed = "Monado may run as administrator only after you allowed it: turn on the profile's Run as administrator and save it.";
    public const string NotMirador = "Only Mirador may start Monado as administrator.";
    public const string NoAdmin = "Running Monado as administrator needs an administrator account.";
    public const string AlreadyRunning = "Monado already runs as administrator.";

    /// <summary>The refusal the user should see when the caller's SID isn't the session user's (checked in the runner).</summary>
    public const string NotTheUser = "Only the signed-in user may start Monado as administrator.";

    /// <summary>
    /// The refusal the user should see, or null when the start may go ahead. The checks are, in order: the caller
    /// consented, the caller is the installed <c>Mirador.exe</c>, the caller has an administrator (linked) token, and no
    /// elevated Monado already runs for this caller. The caller's SID being the session user's is checked in the runner,
    /// where the session token is known (<see cref="NotTheUser"/>).
    /// </summary>
    /// <param name="consented">Whether the caller's SID has its value in the consent key.</param>
    /// <param name="imagePath">The caller process's program file, from the pipe (never from the request); null when unknown.</param>
    /// <param name="installDir">The folder the installed <c>Mirador.exe</c> is in (the helper's own install, one level up).</param>
    /// <param name="hasLinkedToken">Whether the caller's token has a linked (elevated) token.</param>
    /// <param name="running">Whether an elevated Monado already runs for this caller.</param>
    public static string? Check(bool consented, string? imagePath, string installDir, bool hasLinkedToken, bool running)
    {
        if (!consented)
        {
            return NotAllowed;
        }

        if (!Mirador.Ipc.InstalledMirador.IsInstalledCopy(imagePath, installDir))
        {
            return NotMirador;
        }

        if (!hasLinkedToken)
        {
            return NoAdmin;
        }

        return running ? AlreadyRunning : null;
    }
}
