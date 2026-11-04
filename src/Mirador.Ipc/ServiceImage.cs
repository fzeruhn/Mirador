namespace Mirador.Ipc;

/// <summary>Reads a Windows service's <c>ImagePath</c>, the command line its program is started with.</summary>
public static class ServiceImage
{
    /// <summary>
    /// The program an <c>ImagePath</c> runs, without its quotes and arguments: <c>"C:\…\x.exe" -k</c>, or unquoted up
    /// to the first <c>.exe</c>; <c>\??\</c> in front is dropped. Null for an empty value.
    /// </summary>
    public static string? ProgramOf(string? imagePath)
    {
        var text = imagePath?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? text[1..] : text[1..close];
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? text : text[..(exe + 4)];
    }
}
