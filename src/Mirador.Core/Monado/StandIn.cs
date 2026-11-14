using System.Diagnostics;

namespace Mirador.Core.Monado;

/// <summary>
/// Mirador's stand-in: the small program that sits where Monado's runtime DLL looks for the service and asks Mirador to
/// start Monado when a game finds none running, under the names of <see cref="Mirador.Ipc.StandInNames"/>. It is told
/// from a real Monado by its file: its version resource's description is <see cref="FileDescription"/>.
/// </summary>
public static class StandIn
{
    /// <summary>The <c>FileDescription</c> in the stand-in's version resource.</summary>
    public const string FileDescription = "Mirador stand-in";

    /// <inheritdoc cref="Mirador.Ipc.StandInNames.FileName"/>
    public const string FileName = Mirador.Ipc.StandInNames.FileName;

    /// <inheritdoc cref="Mirador.Ipc.StandInNames.ExecutableName"/>
    public const string ExecutableName = Mirador.Ipc.StandInNames.ExecutableName;

    /// <inheritdoc cref="Mirador.Ipc.StandInNames.FileNames"/>
    public static IReadOnlyList<string> FileNames => Mirador.Ipc.StandInNames.FileNames;

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
    /// Whether <paramref name="standIn"/> is the stand-in of <paramref name="install"/> (in one of its
    /// <see cref="MonadoInstall.StandInDirs"/>: its own, or its approved copy's): the same file, however the two paths
    /// are written; by the paths when either file can't be opened.
    /// </summary>
    public static bool BelongsTo(string standIn, MonadoInstall install) =>
        install.StandInDirs.Any(dir => FileNames.Any(name => IsFile(standIn, Path.Combine(dir, name))));

    private static bool IsFile(string standIn, string expected) =>
        Native.Win32.FileIdentity(standIn) is { } a && Native.Win32.FileIdentity(expected) is { } b
            ? a == b
            : string.Equals(Path.GetFullPath(standIn), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
}
