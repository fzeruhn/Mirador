namespace Mirador.Ipc;

/// <summary>
/// The one test for "this is the installed Mirador" (Mirador.md section 4.10 item 46), shared by the helper, which
/// starts Monado as administrator only for that caller, and by Mirador, which picks the elevated path (and Home's
/// "Runs as you" line) with it, so the two can't disagree.
/// </summary>
public static class InstalledMirador
{
    /// <summary>
    /// Whether <paramref name="exePath"/> is <c>Mirador.exe</c> in <paramref name="installDir"/>, the
    /// helper's own folder (where <c>HKLM\SOFTWARE\Mirador</c> <c>AppPath</c> points in a normal install). Compared as full
    /// paths, ignoring case as Windows does; false for null or a path that can't be read.
    /// </summary>
    public static bool IsInstalledCopy(string? exePath, string? installDir)
    {
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(installDir))
        {
            return false;
        }

        try
        {
            var expected = Path.GetFullPath(Path.Combine(installDir, "Mirador.exe"));
            return string.Equals(expected, Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
