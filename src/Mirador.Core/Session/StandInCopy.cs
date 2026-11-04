using System.Security.Cryptography;
using Mirador.Core.Modules;

namespace Mirador.Core.Session;

/// <summary>
/// Puts Mirador's stand-in (<see cref="Monado.StandIn"/>) into a build folder, where Monado's runtime DLL looks for
/// <c>monado-service.exe</c> when a game finds no service running.
/// </summary>
internal static class StandInCopy
{
    private const string FileName = "monado-service.exe";

    /// <summary>
    /// Copies <paramref name="standInExe"/> into <paramref name="standInDir"/> (creating it) when the copy there is
    /// missing or different. Without it games just can't start Monado from that build; Monado itself still starts.
    /// The copy is written next to its place and then moved there, so a game that starts it meanwhile never runs half a
    /// program, and it never replaces <paramref name="serviceExe"/>, the build's real service.
    /// </summary>
    /// <returns>Null when the stand-in is in place, else the <c>standin</c> status line that says why not.</returns>
    public static StatusLine? Place(string standInExe, string standInDir, string serviceExe)
    {
        if (!File.Exists(standInExe))
        {
            return new StatusLine("standin", "Games", StatusLevel.Info, "Games can't start Monado from this build: the stand-in is missing.");
        }

        var target = Path.Combine(standInDir, FileName);
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(serviceExe), StringComparison.OrdinalIgnoreCase))
        {
            return new StatusLine(
                "standin",
                "Games",
                StatusLevel.Warning,
                $"Games can't start Monado from this build: its runtime DLL would start {serviceExe} itself, without Mirador.");
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

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new StatusLine(
                "standin",
                "Games",
                StatusLevel.Warning,
                $"Games may not start Monado from this build: Mirador couldn't copy its stand-in to {standInDir} ({e.Message.Trim().TrimEnd('.')}).");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next copy to replace.
        }
    }

    private static bool SameContent(string a, string b)
    {
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }
}
