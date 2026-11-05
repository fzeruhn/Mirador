namespace Mirador.Core.OpenVr;

/// <summary>
/// <c>Mirador.exe --restore-openvr</c>, run by the uninstaller as the user who uninstalls (Mirador.md §4.6 item 24):
/// takes Mirador's OpenComposite copy out of that user's OpenVR paths file, SteamVR first, then deletes the copy. The
/// copy is deleted only once the file no longer names it, so OpenVR games never point at a DLL that isn't there.
/// </summary>
public static class RestoreOpenVrCommand
{
    public const string Flag = "--restore-openvr";

    /// <returns>0 when done (or nothing to do); 1 when the paths file couldn't be read or written, and the copy stays.</returns>
    public static int Run(OpenVrPaths paths, string runtimeDir)
    {
        if (paths.Read().Problem is not null)
        {
            return 1;
        }

        if (paths.NamesOurs() && paths.UseSteamVr() is not null)
        {
            return 1;
        }

        try
        {
            if (Directory.Exists(runtimeDir))
            {
                Directory.Delete(runtimeDir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A game still holds the DLL: the copy stays behind, unused, in the user's own folder.
        }

        return 0;
    }
}
