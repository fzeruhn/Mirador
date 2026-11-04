using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mirador.Core;

/// <summary>
/// The signed-in user's own environment, as Windows builds it for a new program of theirs: not Mirador's, which may be
/// a game's when the game's stand-in started Mirador (review 2026-11-04).
/// </summary>
public static class UserEnvironment
{
    /// <summary>
    /// The user's default environment (<c>CreateEnvironmentBlock</c> with this process's token, nothing inherited). When
    /// Windows won't build it, Mirador's own environment instead, so that Monado still gets one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Default()
    {
        try
        {
            return FromWindows();
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return Current();
        }
    }

    /// <summary>This process's environment.</summary>
    public static IReadOnlyDictionary<string, string> Current()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                found[name] = value;
            }
        }

        return found;
    }

    /// <summary>
    /// Reads an environment block: <c>NAME=value</c> strings, each ended by a null, the whole ended by another. Entries
    /// that start with <c>=</c> (the per-drive current folders) are left out.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Parse(string block)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block.Split('\0'))
        {
            if (entry.Length == 0 || entry[0] == '=')
            {
                continue;
            }

            var equals = entry.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            found[entry[..equals]] = entry[(equals + 1)..];
        }

        return found;
    }

    private static IReadOnlyDictionary<string, string> FromWindows()
    {
        const uint TokenQuery = 0x0008, TokenDuplicate = 0x0002;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var token))
        {
            throw new Win32Exception();
        }

        try
        {
            if (!CreateEnvironmentBlock(out var block, token, inherit: false))
            {
                throw new Win32Exception();
            }

            try
            {
                return Parse(ReadBlock(block));
            }
            finally
            {
                DestroyEnvironmentBlock(block);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>The block as one string, up to its closing pair of nulls.</summary>
    private static string ReadBlock(IntPtr block)
    {
        var length = 0;
        while (Marshal.ReadInt16(block, length * 2) != 0 || Marshal.ReadInt16(block, (length + 1) * 2) != 0)
        {
            length++;
        }

        return Marshal.PtrToStringUni(block, length + 1);
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
