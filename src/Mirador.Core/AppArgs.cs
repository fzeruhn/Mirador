using Mirador.Core.Elevation;
using Mirador.Core.OpenVr;
using Mirador.Core.Runtime;
using Mirador.Core.SignIn;

namespace Mirador.Core;

/// <summary>What <c>Mirador.exe</c> was started for.</summary>
/// <param name="Start">Start Monado (<c>--start</c>): the tray's Start from a second Mirador, or a shortcut.</param>
/// <param name="Background">Start without the window (<c>--background</c>): the stand-in, or Windows' startup.</param>
/// <param name="SignIn"><c>--sign-in</c>: Windows started Mirador at sign-in, which may mean no window (App settings, "Start minimized").</param>
/// <param name="RemoveSignIn"><c>--remove-sign-in</c>: the uninstaller's step that stops Mirador starting at sign-in.</param>
/// <param name="AllowElevatedMonado"><c>--allow-elevated-monado</c>: the elevated step (and the installer's) that records this user's consent to run Monado as administrator.</param>
/// <param name="ApproveBuild"><c>--approve-build &lt;profile&gt; &lt;commit&gt; &lt;install folder&gt;</c>: the elevated step that approves a profile's build (Mirador.md §4.11 item 51); null without it, and with fewer values.</param>
public sealed record AppArgs(bool Start, bool Background, bool SignIn = false, bool RemoveSignIn = false, bool AllowElevatedMonado = false, (string ProfileId, string Commit, string Source)? ApproveBuild = null)
{
    /// <summary>Reads the command line; flags are matched without regard to case, and anything else is ignored.</summary>
    public static AppArgs Parse(string[] args)
    {
        bool start = false, background = false;
        var signIn = false;
        var removeSignIn = false;
        var allowElevatedMonado = false;
        (string, string, string)? approveBuild = null;
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
                case SignInStartup.Flag:
                    signIn = true;
                    break;
                case SignInStartup.RemoveFlag:
                    removeSignIn = true;
                    break;
                case AllowElevatedCommand.Flag:
                    allowElevatedMonado = true;
                    break;
                case ApproveBuildFlag when i + 3 < args.Length:
                    approveBuild = (args[i + 1], args[i + 2], args[i + 3]);
                    i += 3;
                    break;
            }
        }

        return new AppArgs(start, background, signIn, removeSignIn, allowElevatedMonado, approveBuild);
    }

    /// <summary>The flag of the approval step.</summary>
    public const string ApproveBuildFlag = "--approve-build";
}
