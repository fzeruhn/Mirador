using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The only DLL loaded by name is kernel32; look for it in System32 and nowhere else.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Mirador.Core.Native;

/// <summary>The few Win32 calls Mirador.Core needs. Nothing outside this file talks to the operating system directly.</summary>
internal static class Win32
{
    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>: the buffer was too small for the answer.</summary>
    private const int ErrorInsufficientBuffer = 122;

    /// <summary><c>PROCESS_QUERY_LIMITED_INFORMATION</c>: the least access that reads a process's program file.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Path buffer sizes to try, in characters: the usual, then the longest a Windows path can be.</summary>
    private static readonly int[] PathCapacities = [1024, 32768];

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint mode, out uint oldMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>
    /// What tells a file from every other on this computer, whatever path leads to it (case, short names, links): its
    /// volume's serial number and its index on that volume. Null when the file can't be opened.
    /// </summary>
    public static (uint Volume, ulong Index)? FileIdentity(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(file, out var information)
                ? (information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary><c>SEM_FAILCRITICALERRORS</c>: Windows hands the error back instead of showing its message box.</summary>
    private const uint SemFailCriticalErrors = 0x0001;

    /// <summary>
    /// Runs <paramref name="action"/> with Windows' error message boxes off for this thread. Starting a file that isn't
    /// a program otherwise shows "Unsupported 16-Bit Application" from inside the call, which then waits until someone
    /// clicks OK; with them off the start fails at once with the error.
    /// </summary>
    public static T WithoutErrorBoxes<T>(Func<T> action)
    {
        var quiet = SetThreadErrorMode(SemFailCriticalErrors, out var old);
        try
        {
            return action();
        }
        finally
        {
            if (quiet)
            {
                SetThreadErrorMode(old, out _);
            }
        }
    }

    /// <summary>
    /// The full path of the program file process <paramref name="id"/> runs, or null when Windows won't say: no such
    /// process, or not one this user may ask about.
    /// </summary>
    public static string? ProcessImagePath(int id)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, unchecked((uint)id));
        if (process.IsInvalid)
        {
            return null;
        }

        foreach (var capacity in PathCapacities)
        {
            var buffer = new char[capacity];
            var size = (uint)capacity;
            if (QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return new string(buffer, 0, (int)size);
            }

            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>; each <c>FILETIME</c> as its two halves, so the layout matches.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

}
