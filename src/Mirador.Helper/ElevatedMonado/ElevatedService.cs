using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>
/// One elevated <c>monado-service</c> the helper started: inside a kill-on-close job, its output pumped to the log
/// file, ended when the caller process exits, on <see cref="Stop"/>, or when the helper stops. The SteamVR driver hold
/// is released once Monado's pipe is up or after 60 s. Its Win32 path runs only on a PC.
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

    // Monado's own (elevated) token and environment, and the program that writes the exit key into its console:
    // Mirador.Helper.exe, started with them in the user's session (ConsoleExitKey). Null: a Stop only closes the job.
    private readonly SafeAccessTokenHandle? senderToken;
    private readonly byte[] senderEnvironment;
    private readonly string? exitKeySender;
    private readonly CancellationTokenSource stop = new();
    private IDisposable hold;
    private readonly object gate = new();
    private bool disposed;
    private bool stopping; // a Stop has asked Monado to end: a later one only closes the job

    private ElevatedService(
        SafeFileHandle job, SafeProcessHandle process, int processId, IDisposable hold, SafeAccessTokenHandle? senderToken,
        byte[] senderEnvironment, string? exitKeySender)
    {
        this.senderEnvironment = senderEnvironment;
        this.job = job;
        this.process = process;
        this.hold = hold;
        this.senderToken = senderToken;
        this.exitKeySender = exitKeySender;
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

    /// <summary>
    /// With <paramref name="clean"/>, asks Monado to end by itself first (the exit key in its console), so that it tears
    /// its devices down, and waits for that at most <see cref="Mirador.Ipc.ConsoleExitKey.StopLimit"/>. Then closes the job, which ends Monado
    /// and anything it started if they are still there. An unelevated Mirador can do neither itself.
    /// </summary>
    public void Stop(bool clean)
    {
        lock (gate)
        {
            if (clean && !disposed && !stopping && !HasEnded)
            {
                stopping = true;
                if (SendExitKey())
                {
                    WaitForSingleObject(process, (uint)Mirador.Ipc.ConsoleExitKey.StopLimit.TotalMilliseconds);
                }
            }
        }

        job.Dispose();
    }

    /// <summary>
    /// Under the gate: runs the exit-key sender as Monado's own token in its session. True when it wrote the key, or may
    /// have (still running at its limit); false when it surely didn't.
    /// </summary>
    private bool SendExitKey()
    {
        if (senderToken is null || exitKeySender is null)
        {
            return false;
        }

        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        var environment = GCHandle.Alloc(senderEnvironment, GCHandleType.Pinned);
        try
        {
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = desktop } };
            var commandLine = $"\"{exitKeySender}\" {Mirador.Ipc.ConsoleExitKey.Flag} {ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0".ToCharArray();
            if (!CreateProcessAsUserW(
                    senderToken, exitKeySender, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: false, CreateNoWindow | CreateUnicodeEnvironment,
                    environment.AddrOfPinnedObject(), Path.GetDirectoryName(exitKeySender), ref startup, out var info))
            {
                return false;
            }

            using var sender = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            using var thread = new SafeProcessHandle(info.hThread, ownsHandle: true);
            if (WaitForSingleObject(sender, (uint)Mirador.Ipc.ConsoleExitKey.SenderLimit.TotalMilliseconds) != WaitObject0)
            {
                TerminateProcess(sender, 1);
                return true;
            }

            return GetExitCodeProcess(sender, out var code) && code == 0;
        }
        catch (Exception e) when (e is Win32Exception or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            environment.Free();
            Marshal.FreeHGlobal(desktop);
        }
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
        senderToken?.Dispose();
        process.Dispose();
        stop.Dispose();
    }

    /// <param name="exitKeySender">
    /// The program that ends Monado cleanly at a Stop (<c>Mirador.Helper.exe</c>, with
    /// <see cref="Mirador.Ipc.ConsoleExitKey.Flag"/>), run with <paramref name="token"/>; null: a Stop only closes the job.
    /// </param>
    public static ElevatedService Launch(
        string serviceExe, string serverDir, IReadOnlyDictionary<string, string> env, SafeAccessTokenHandle token,
        int callerProcessId, string logFile, IDisposable hold, string pipeName, string? exitKeySender = null)
    {
        var job = Mirador.Ipc.ProcessJobs.CreateKillOnCloseJob();
        SafeFileHandle? readPipe = null;
        SafeFileHandle? writePipe = null;
        var block = EnvironmentBlock(env);
        var senderBlock = EnvironmentBlock(new Dictionary<string, string>(env) { ["DOTNET_EnableDiagnostics"] = "0" });
        var envHandle = GCHandle.Alloc(block, GCHandleType.Pinned);
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
            var service = new ElevatedService(job, process, info.dwProcessId, hold, exitKeySender is null ? null : Duplicate(token), senderBlock, exitKeySender);
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
                        Stop(clean: true); // the Mirador that asked has exited: end Monado, as the job does for an unelevated one
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

    /// <summary>
    /// A handle of our own to <paramref name="token"/>, which the caller disposes after the launch; null when Windows
    /// won't give one, and then a Stop only closes the job (Monado ends at once, without its teardown).
    /// </summary>
    private static SafeAccessTokenHandle? Duplicate(SafeAccessTokenHandle token)
    {
        var self = GetCurrentProcess();
        return DuplicateHandle(self, token.DangerousGetHandle(), self, out var copy, 0, false, DuplicateSameAccess)
            ? new SafeAccessTokenHandle(copy)
            : null;
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
        SafeAccessTokenHandle token, string? applicationName, char[]? commandLine, IntPtr processAttributes,
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

    private const uint DuplicateSameAccess = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    /// <summary>A hold that holds nothing, after the real one has been released.</summary>
    private sealed class NoHold : IDisposable
    {
        public static NoHold Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
