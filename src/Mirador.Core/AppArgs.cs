using Mirador.Core.Runtime;

namespace Mirador.Core;

/// <summary>What <c>Mirador.exe</c> was started for.</summary>
/// <param name="Start">Start Monado (<c>--start</c>): the tray's Start from a second Mirador, or a shortcut.</param>
/// <param name="Background">Start without the window (<c>--background</c>): the stand-in, or Windows' startup.</param>
/// <param name="SetRuntime">The manifest of <c>--set-runtime &lt;manifest&gt;</c>, the elevated step; empty when the flag came without one.</param>
public sealed record AppArgs(bool Start, bool Background, string? SetRuntime)
{
    /// <summary>Reads the command line; flags are matched without regard to case, and anything else is ignored.</summary>
    public static AppArgs Parse(string[] args)
    {
        bool start = false, background = false;
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
            }
        }

        return new AppArgs(start, background, setRuntime);
    }
}
