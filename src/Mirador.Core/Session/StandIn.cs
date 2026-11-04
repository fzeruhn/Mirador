using System.Security.Cryptography;
using Mirador.Core.Modules;

namespace Mirador.Core.Session;

/// <summary>
/// The stand-in: a small <c>monado-service.exe</c> that Monado's runtime DLL starts when a game finds no service
/// running, and that asks Mirador to Start. A build folder needs Mirador's copy of it in its stand-in folder.
/// </summary>
internal static class StandIn
{
    private const string FileName = "monado-service.exe";

    /// <summary>
    /// Copies <paramref name="standInExe"/> into <paramref name="standInDir"/> (creating it) when the copy there is
    /// missing or different. Without it games just can't start Monado from that build; Monado itself still starts.
    /// </summary>
    /// <returns>Null when the stand-in is in place, else the <c>standin</c> status line that says why not.</returns>
    public static StatusLine? Place(string standInExe, string standInDir)
    {
        if (!File.Exists(standInExe))
        {
            return new StatusLine("standin", "Games", StatusLevel.Info, "Games can't start Monado from this build: the stand-in is missing.");
        }

        var target = Path.Combine(standInDir, FileName);
        try
        {
            if (!File.Exists(target) || !SameContent(standInExe, target))
            {
                Directory.CreateDirectory(standInDir);
                File.Copy(standInExe, target, overwrite: true);
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new StatusLine(
                "standin",
                "Games",
                StatusLevel.Warning,
                $"Games may not start Monado from this build: Mirador couldn't copy its stand-in to {standInDir} ({e.Message.Trim().TrimEnd('.')}).");
        }
    }

    private static bool SameContent(string a, string b)
    {
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }
}
