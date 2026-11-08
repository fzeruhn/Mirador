using Mirador.Core.Elevation;
using Mirador.Core.OpenVr;
using Mirador.Core.Runtime;
using Mirador.Core.SignIn;

namespace Mirador.Core;

/// <summary>What <c>Mirador.exe</c> was started for.</summary>
/// <param name="Start">Start Monado (<c>--start</c>): the tray's Start from a second Mirador, or a shortcut.</param>
/// <param name="Background">Start without the window (<c>--background</c>): the stand-in, or Windows' startup.</param>
/// <param name="SetRuntime">The manifest of <c>--set-runtime &lt;manifest&gt;</c>, the elevated step; empty when the flag came without one.</param>
/// <param name="RestoreOpenVr"><c>--restore-openvr</c>: the uninstaller's step that puts OpenVR games back on SteamVR.</param>
/// <param name="SignIn"><c>--sign-in</c>: Windows started Mirador at sign-in, which may mean no window (App settings, "Start minimized").</param>
/// <param name="RemoveSignIn"><c>--remove-sign-in</c>: the uninstaller's step that stops Mirador starting at sign-in.</param>
/// <param name="AllowElevatedMonado"><c>--allow-elevated-monado</c>: the elevated step (and the installer's) that records this user's consent to run Monado as administrator.</param>
public sealed record AppArgs(bool Start, bool Background, string? SetRuntime, bool RestoreOpenVr = false, bool SignIn = false, bool RemoveSignIn = false, bool AllowElevatedMonado = false)
{
    /// <summary>Reads the command line; flags are matched without regard to case, and anything else is ignored.</summary>
    public static AppArgs Parse(string[] args)
    {
        bool start = false, background = false, restoreOpenVr = false;
        var signIn = false;
        var removeSignIn = false;
        var allowElevatedMonado = false;
        string? setRuntime = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--start":
                    start = true;
                    break;
                case "--background":
                    background = true;
                    break;
                case SetRuntimeCommand.Flag:
                    setRuntime = i + 1 < args.Length ? args[++i] : "";
                    break;
                case RestoreOpenVrCommand.Flag:
                    restoreOpenVr = true;
                    break;
                case SignInStartup.Flag:
                    signIn = true;
                    break;
                case SignInStartup.RemoveFlag:
                    removeSignIn = true;
                    break;
                case AllowElevatedCommand.Flag:
                    allowElevatedMonado = true;
                    break;
            }
        }

        return new AppArgs(start, background, setRuntime, restoreOpenVr, signIn, removeSignIn, allowElevatedMonado);
    }
}
