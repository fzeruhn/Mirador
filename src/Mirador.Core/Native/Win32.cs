using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The only DLL loaded by name is kernel32; look for it in System32 and nowhere else.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Mirador.Core.Native;

/// <summary>The few Win32 calls Mirador.Core needs. Nothing outside this file talks to the operating system directly.</summary>
internal static class Win32
{
    /// <summary><c>ERROR_SEM_TIMEOUT</c>: <c>WaitNamedPipeW</c> gives up because every instance of the pipe is busy.</summary>
    private const int ErrorSemTimeout = 121;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>: the buffer was too small for the answer.</summary>
    private const int ErrorInsufficientBuffer = 122;

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    /// <summary><c>PROCESS_QUERY_LIMITED_INFORMATION</c>: the least access that reads a process's program file.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary><c>CP_ACP</c>: the system's ANSI code page, the one Windows' narrow ("A") functions use.</summary>
    private const uint CodePageAnsi = 0;

    /// <summary>Path buffer sizes to try, in characters: the usual, then the longest a Windows path can be.</summary>
    private static readonly int[] PathCapacities = [1024, 32768];

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, nint name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeHandle process);

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMs);

    // CharSet.Unicode: under the default (Ansi) a char[] is marshalled as one byte per character.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string wide, int wideLength, [Out] byte[]? multi,
        int multiLength, nint defaultChar, nint usedDefaultChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] multi, int multiLength, [Out] char[]? wide, int wideLength);

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

    /// <summary>Creates a job object that ends every process in it when its last handle closes.</summary>
    /// <returns>The job's handle. Closing it (dispose) ends the processes.</returns>
    /// <exception cref="Win32Exception">Windows refused; the message is its own wording.</exception>
    public static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(0, 0);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        var info = new ExtendedLimitInformation();
        info.Basic.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    /// <summary>Puts <paramref name="process"/> into <paramref name="job"/>. Returns the Win32 error, or 0 when it worked.</summary>
    public static int AssignToJob(SafeFileHandle job, SafeHandle process) =>
        AssignProcessToJobObject(job, process) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>
    /// <paramref name="text"/> as a program that works through Windows' narrow ("A") functions sees it: converted to
    /// the ANSI code page, as <c>getenv</c> and <c>GetTempPath2A</c> hand it over, and back, as <c>CreateNamedPipeA</c>
    /// takes it. A character the code page lacks comes back as its best fit (<c>Ł</c> as <c>L</c> in code page 1252)
    /// or <c>?</c>. Returns <paramref name="text"/> unchanged when Windows can't convert it.
    /// </summary>
    public static string ThroughAnsiCodePage(string text)
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

    /// <summary>
    /// Whether a named pipe exists: a call that succeeds means an instance is waiting for a client, and one that
    /// times out with <see cref="ErrorSemTimeout"/> means every instance is busy. Any other failure (file not found
    /// above all) means no such pipe.
    /// </summary>
    /// <remarks>
    /// The name is normalized first. <c>CreateNamedPipe</c> and <c>CreateFile</c> turn <c>/</c> into <c>\</c> and collapse
    /// doubled backslashes, so Monado's <c>...\Temp\/monado_comp_ipc</c> is filed as <c>...\Temp\monado_comp_ipc</c>;
    /// <c>WaitNamedPipe</c> only converts the slash, so given the literal name it looks for a pipe that doesn't exist.
    /// </remarks>
    public static bool PipeExists(string name)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(name);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return false; // a name Windows can't make sense of is no pipe
        }

        return WaitNamedPipe(normalized, 1) || Marshal.GetLastPInvokeError() == ErrorSemTimeout;
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

    // The x64 layout of JOBOBJECT_EXTENDED_LIMIT_INFORMATION (144 bytes); the project builds for x64 only.

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
