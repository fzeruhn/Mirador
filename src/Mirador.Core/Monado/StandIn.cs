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

    /// <summary>The stand-in's file name, the service's: <c>monado-service.exe</c>.</summary>
    public const string FileName = "monado-service.exe";

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

    /// <summary>
    /// The program file of the process <paramref name="processId"/> when it is a stand-in (a game's runtime DLL started
    /// it, so it is the one beside that game's Monado); null for any other caller, or one that can't be looked at.
    /// </summary>
    /// <param name="imagePathOf">The program file of a process, as <see cref="IProcessList.ImagePathOf"/> gives it.</param>
    public static string? ProgramOf(int? processId, Func<int, string?> imagePathOf) =>
        processId is { } id && imagePathOf(id) is { } path && IsStandIn(path) ? path : null;

    /// <summary>
    /// Whether <paramref name="standIn"/> is the stand-in of <paramref name="install"/> (in its <see cref="MonadoInstall.StandInDir"/>):
    /// the same file, however the two paths are written; by the paths when either file can't be opened.
    /// </summary>
    public static bool BelongsTo(string standIn, MonadoInstall install)
    {
        var expected = Path.Combine(install.StandInDir, FileName);
        return Native.Win32.FileIdentity(standIn) is { } a && Native.Win32.FileIdentity(expected) is { } b
            ? a == b
            : string.Equals(Path.GetFullPath(standIn), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }
}
