using System.Security;

namespace Mirador.Core.SignIn;

/// <summary>
/// "Start Mirador when you sign in" (Mirador.md §4.8 item 33): a value in the user's Run key that starts
/// <c>Mirador.exe --sign-in</c>. Task Manager's Startup tab can switch it off without touching the Run value, so
/// <see cref="IsOn"/> honours that switch too and <see cref="TurnOn"/> clears it.
/// </summary>
public sealed class SignInStartup(IRunKeys keys, string exePath)
{
    public const string ValueName = "Mirador";

    /// <summary>The argument Windows starts Mirador with at sign-in.</summary>
    public const string Flag = "--sign-in";

    /// <summary>The uninstaller's step: take the Run value out.</summary>
    public const string RemoveFlag = "--remove-sign-in";

    /// <summary>What Windows runs at sign-in.</summary>
    public string Command => $"\"{exePath}\" {Flag}";

    /// <summary>Whether Mirador will start at sign-in: the Run value is this exe's, and Task Manager hasn't disabled it.</summary>
    public bool IsOn
    {
        get
        {
            try
            {
                if (!string.Equals(keys.GetRun(ValueName), Command, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Task Manager keeps 12 bytes per entry: an even first byte (02, 06) is enabled, an odd one (03, 07) disabled.
                return keys.GetApproved(ValueName) is not { Length: > 0 } approved || (approved[0] & 1) == 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                throw new MiradorException($"Mirador couldn't read whether it starts at sign-in ({Reason(e)}).");
            }
        }
    }

    public void TurnOn()
    {
        try
        {
            keys.SetRun(ValueName, Command);
            keys.DeleteApproved(ValueName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new MiradorException($"Mirador couldn't set itself to start at sign-in ({Reason(e)}). Try again.");
        }
    }

    public void TurnOff()
    {
        try
        {
            keys.DeleteRun(ValueName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new MiradorException($"Mirador couldn't stop itself from starting at sign-in ({Reason(e)}). Try again.");
        }
    }

    private static string Reason(Exception e) => e.Message.Trim().TrimEnd('.');
}
