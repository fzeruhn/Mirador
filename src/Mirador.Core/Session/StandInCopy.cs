using Mirador.Ipc;
using System.Security.Cryptography;
using Mirador.Core.Modules;

namespace Mirador.Core.Session;

/// <summary>
/// Puts Mirador's stand-in (<see cref="Monado.StandIn"/>) into a build folder, where Monado's runtime DLL looks for the
/// service when a game finds no service running: under its own name, which Mirador's builds look for, and as
/// <c>monado-service.exe</c>, which builds configured as upstream does look for.
/// </summary>
internal static class StandInCopy
{
    /// <summary>
    /// Copies <paramref name="standInExe"/> into <paramref name="standInDir"/> (creating it) under each of
    /// <see cref="Monado.StandIn.FileNames"/> where the copy there is missing or different. Without it games just can't
    /// start Monado from that build; Monado itself still starts. Each copy is written next to its place and then moved
    /// there, so a game that starts it meanwhile never runs half a program, and it never replaces
    /// <paramref name="serviceExe"/>, the build's real service.
    /// </summary>
    /// <returns>Null when the stand-in is in place, else the <c>standin</c> status line that says why not.</returns>
    public static StatusLine? Place(string standInExe, string standInDir, string serviceExe)
    {
        if (!File.Exists(standInExe))
        {
            return new StatusLine("standin", "Games", StatusLevel.Info, "Games can't start Monado from this build: the stand-in is missing.");
        }

        StatusLine? line = null;
        foreach (var name in Monado.StandIn.FileNames)
        {
            var target = Path.Combine(standInDir, name);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(serviceExe), StringComparison.OrdinalIgnoreCase))
            {
                line = new StatusLine(
                    "standin",
                    "Games",
                    StatusLevel.Warning,
                    $"Games may not start Monado from this build through Mirador: unless it was configured with {Monado.ServiceLaunch.ConfigureOption}, its runtime DLL starts {serviceExe} itself.");
                continue;
            }

            var temp = target + ".new";
            try
            {
                if (!File.Exists(target) || !SameContent(standInExe, target))
                {
                    Directory.CreateDirectory(standInDir);
                    File.Copy(standInExe, temp, overwrite: true);
                    File.Move(temp, target, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Cleanup.TryDelete(temp);
                return new StatusLine(
                    "standin",
                    "Games",
                    StatusLevel.Warning,
                    $"Games may not start Monado from this build: Mirador couldn't copy its stand-in to {standInDir} ({e.Message.Trim().TrimEnd('.')}).");
            }
        }

        return line;
    }

    private static bool SameContent(string a, string b)
    {
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }
}
