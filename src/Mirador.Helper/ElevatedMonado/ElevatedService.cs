using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>
/// One elevated <c>monado-service</c> the helper started: inside a kill-on-close job, its output pumped to the log
/// file, ended when the caller process exits, on <see cref="Stop"/>, or when the helper stops. The SteamVR driver hold
/// is released once Monado's pipe is up or after 60 s. Its Win32 path is verified on the PC in Task 51.
/// </summary>
internal sealed class ElevatedService : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint Synchronize = 0x00100000;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint ResumeFailed = 0xFFFFFFFF;

    /// <summary><c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>: the only handles the new process inherits.</summary>
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x00020002);

    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HoldPoll = TimeSpan.FromMilliseconds(250);

    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle process;
    private readonly CancellationTokenSource stop = new();
    private IDisposable hold;
    private readonly object gate = new();
    private bool disposed;

    private ElevatedService(SafeFileHandle job, SafeProcessHandle process, int processId, IDisposable hold)
    {
        this.job = job;
        this.process = process;
        this.hold = hold;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public bool HasEnded => WaitForSingleObject(process, 0) == WaitObject0;

    /// <summary>The exit code once Monado has ended, else null.</summary>
    public int? ExitCodeIfEnded()
    {
        if (WaitForSingleObject(process, 0) != WaitObject0)
        {
            return null;
        }

        return GetExitCodeProcess(process, out var code) ? unchecked((int)code) : 0;
    }

    public void Stop()
    {
        // Closing the job ends Monado and anything it started; an unelevated Mirador can't do that itself.
        job.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        stop.Cancel();
        try
        {
            job.Dispose();
        }
        catch (Exception)
        {
        }

        hold.Dispose();
        process.Dispose();
        stop.Dispose();
    }

    public static ElevatedService Launch(
        string serviceExe, string serverDir, IReadOnlyDictionary<string, string> env, SafeAccessTokenHandle token,
        int callerProcessId, string logFile, IDisposable hold, string pipeName)
    {
        var job = Mirador.Ipc.ProcessJobs.CreateKillOnCloseJob();
        SafeFileHandle? readPipe = null;
        SafeFileHandle? writePipe = null;
        var envHandle = GCHandle.Alloc(EnvironmentBlock(env), GCHandleType.Pinned);
        try
        {
            CreatePipePair(out readPipe, out writePipe);

            // Created suspended, so it runs no code before it is in the job (below). Only the pipe's write end is handed
            // over: PROC_THREAD_ATTRIBUTE_HANDLE_LIST limits what Monado inherits to that one handle, whatever else the
            // helper may hold that is inheritable.
            var info = CreateSuspendedWithOnlyHandle(token, serviceExe, serverDir, envHandle.AddrOfPinnedObject(), writePipe);
            var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            using var thread = new SafeProcessHandle(info.hThread, ownsHandle: true);

            if (Mirador.Ipc.ProcessJobs.AssignToJob(job, process) is var jobError and not 0)
            {
                // Still suspended: it has run nothing. End it before it ever runs outside the job, and refuse.
                TerminateProcess(process, 1);
                process.Dispose();
                throw new Win32Exception(jobError, "Windows wouldn't put Monado in the group that ends it when Mirador closes");
            }

            if (ResumeThread(info.hThread) == ResumeFailed)
            {
                // In the job but never started: end it (the job also would, when it closes below).
                var error = Marshal.GetLastPInvokeError();
                TerminateProcess(process, 1);
                process.Dispose();
                throw new Win32Exception(error, "Windows wouldn't start Monado");
            }

            // Hand the read end to the pump; the write end is closed here so the pump sees end-of-file when Monado exits.
            var service = new ElevatedService(job, process, info.dwProcessId, hold);
            writePipe.Dispose();
            writePipe = null;
            var read = readPipe;
            readPipe = null;
            service.StartPump(read, logFile);
            service.WatchCaller(callerProcessId);
            service.ReleaseHoldWhenUp(pipeName);
            return service;
        }
        catch
        {
            job.Dispose();
            hold.Dispose();
            throw;
        }
        finally
        {
            readPipe?.Dispose();
            writePipe?.Dispose();
            envHandle.Free();
        }
    }

    private void StartPump(SafeFileHandle read, string logFile) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await using var input = new FileStream(read, FileAccess.Read);
                await using var log = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                var buffer = new byte[8192];
                int n;
                while ((n = await input.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
                {
                    await log.WriteAsync(buffer.AsMemory(0, n), stop.Token).ConfigureAwait(false);
                    await log.FlushAsync(stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Monado ended, the log couldn't be written, or we are stopping: nothing more to pump.
            }
        });

    private void WatchCaller(int callerProcessId) =>
        _ = Task.Run(async () =>
        {
            using var caller = OpenProcess(Synchronize, false, (uint)callerProcessId);
            if (caller.IsInvalid)
            {
                return; // the caller is already gone, or can't be opened; its own exit will not reach us
            }

            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (WaitForSingleObject(caller, 0) == WaitObject0)
                    {
                        Stop(); // the Mirador that asked has exited: end Monado, as the job does for an unelevated one
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500), stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

    private void ReleaseHoldWhenUp(string pipeName) =>
        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + HoldLimit;
            try
            {
                while (DateTime.UtcNow < deadline && !stop.IsCancellationRequested)
                {
                    if (Mirador.Ipc.ProcessJobs.PipeExists(pipeName) || WaitForSingleObject(process, 0) == WaitObject0)
                    {
                        break;
                    }

                    await Task.Delay(HoldPoll, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (gate)
                {
                    hold.Dispose();
                    hold = NoHold.Instance;
                }
            }
        });

    /// <summary>
    /// <c>CreateProcessAsUser</c> of the service, suspended, with no window, its output on <paramref name="output"/> and
    /// nothing else inherited: a <c>STARTUPINFOEX</c> whose <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> holds just that
    /// handle (<c>EXTENDED_STARTUPINFO_PRESENT</c>). The caller must put it in the job before resuming it.
    /// </summary>
    private static ProcessInformation CreateSuspendedWithOnlyHandle(
        SafeAccessTokenHandle token, string serviceExe, string serverDir, IntPtr environment, SafeFileHandle output)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // asks for the size; fails by design
        var attributes = Marshal.AllocHGlobal((nint)size);
        var handles = Marshal.AllocHGlobal(IntPtr.Size);
        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        var initialized = false;
        var added = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            initialized = true;
            output.DangerousAddRef(ref added);
            Marshal.WriteIntPtr(handles, output.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handles, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    lpDesktop = desktop,
                    dwFlags = StartfUseStdHandles,
                    hStdOutput = output.DangerousGetHandle(),
                    hStdError = output.DangerousGetHandle(),
                    hStdInput = IntPtr.Zero,
                },
                lpAttributeList = attributes,
            };

            // inheritHandles must be true for the handle list to apply; the list keeps it to the one handle.
            if (!CreateProcessAsUserW(
                    token, serviceExe, null, IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                    CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | CreateNewProcessGroup | ExtendedStartupInfoPresent,
                    environment, serverDir, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return info;
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }

            if (added)
            {
                output.DangerousRelease();
            }

            Marshal.FreeHGlobal(desktop);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static byte[] EnvironmentBlock(IReadOnlyDictionary<string, string> env)
    {
        // CREATE_UNICODE_ENVIRONMENT wants the block sorted case-insensitively, NAME=VALUE\0 … \0\0.
        var text = new StringBuilder();
        foreach (var (name, value) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(name).Append('=').Append(value).Append('\0');
        }

        text.Append('\0');
        return Encoding.Unicode.GetBytes(text.ToString());
    }

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write)
    {
        var security = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true,
        };

        if (!CreatePipe(out read, out write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // Only the write end is inherited by Monado; the read end stays with the helper.
        SetHandleInformation(read, HandleFlagInherit, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    /// <summary><c>STARTUPINFOW</c>, blittable: its strings are pointers the caller allocates and frees.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    /// <summary><c>STARTUPINFOEXW</c>: the startup info and the attribute list (the handle list).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token, string? applicationName, string? commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    /// <summary>A hold that holds nothing, after the real one has been released.</summary>
    private sealed class NoHold : IDisposable
    {
        public static NoHold Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
