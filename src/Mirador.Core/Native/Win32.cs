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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);

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
