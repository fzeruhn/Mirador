using System.Runtime.InteropServices;

// The only DLLs loaded by name here are kernel32 and its kin; look for them in System32 and nowhere else.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Mirador.Ipc;

/// <summary>
/// The trip a string takes through Windows' narrow ("A") functions: converted to the system's ANSI code page and
/// back. Monado reads its temp folder with <c>getenv</c>/<c>GetTempPath2A</c> and creates its pipe with
/// <c>CreateNamedPipeA</c>, so a folder with characters the code page lacks (<c>Ł</c> on a Western Windows) ends up as
/// their best fit (<c>L</c>) or <c>?</c>. Both Mirador and the helper must name the pipe the same way Monado does.
/// </summary>
public static class AnsiCodePage
{
    /// <summary><c>CP_ACP</c>: the system's ANSI code page, the one the narrow functions use.</summary>
    private const uint CodePageAnsi = 0;

    /// <summary>
    /// <paramref name="text"/> as a program that works through the narrow functions sees it. Returns
    /// <paramref name="text"/> unchanged when Windows can't convert it.
    /// </summary>
    public static string RoundTrip(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var size = WideCharToMultiByte(CodePageAnsi, 0, text, text.Length, null, 0, 0, 0);
        if (size <= 0)
        {
            return text;
        }

        var bytes = new byte[size];
        if (WideCharToMultiByte(CodePageAnsi, 0, text, text.Length, bytes, size, 0, 0) != size)
        {
            return text;
        }

        var length = MultiByteToWideChar(CodePageAnsi, 0, bytes, size, null, 0);
        if (length <= 0)
        {
            return text;
        }

        var chars = new char[length];
        return MultiByteToWideChar(CodePageAnsi, 0, bytes, size, chars, length) == length ? new string(chars) : text;
    }

    // CharSet.Unicode: under the default (Ansi) a char[] is marshalled as one byte per character.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string wide, int wideLength, [Out] byte[]? multi,
        int multiLength, nint defaultChar, nint usedDefaultChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] multi, int multiLength, [Out] char[]? wide, int wideLength);
}
