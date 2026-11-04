using System.Diagnostics;

namespace Mirador.Core.Monado;

/// <summary>
/// Mirador's stand-in: the small program that sits where Monado's runtime DLL looks for <c>monado-service.exe</c> and
/// asks Mirador to start Monado when a game finds none running. It runs under that same name, so it is told from a
/// real Monado by its file: its version resource's description is <see cref="FileDescription"/>.
/// </summary>
public static class StandIn
{
    /// <summary>The <c>FileDescription</c> in the stand-in's version resource.</summary>
    public const string FileDescription = "Mirador stand-in";

    /// <summary>Whether the program file at <paramref name="imagePath"/> is Mirador's stand-in; false for any file that can't be read as one.</summary>
    public static bool IsStandIn(string imagePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(imagePath).FileDescription == FileDescription;
        }
        catch (Exception)
        {
            return false; // missing, unreadable or not a program: not the stand-in
        }
    }
}
