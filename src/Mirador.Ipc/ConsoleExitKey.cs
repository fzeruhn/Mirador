using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mirador.Ipc;

/// <summary>
/// Ends <c>monado-service</c> cleanly. On Windows it has no other way to be asked: its main loop ends when a key is
/// waiting in its console's input, and it then tears down its devices (the Lighthouse driver's standby on exit, for
/// one). A kill skips all that. Mirador and the helper start Monado with a console of its own, without a window, so a
/// key can be written there: by a short-lived process that attaches to that console (<see cref="Send"/>), never by
/// Mirador or the helper itself, since a process attached to a console ends with it.
/// </summary>
public static class ConsoleExitKey
{
    /// <summary>The argument that makes Mirador.exe or Mirador.Helper.exe write the key and exit: <c>--send-exit-key &lt;pid&gt;</c>.</summary>
    public const string Flag = "--send-exit-key";

    /// <summary>How long a sender may take before it is given up.</summary>
    public static readonly TimeSpan SenderLimit = TimeSpan.FromSeconds(3);

    /// <summary>How long Monado has to end by itself after the key before it is killed (it takes well under a second).</summary>
    public static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(3);

    /// <summary>
    /// For the sender process only (it gives up its own console): attaches to the console of <paramref name="processId"/>
    /// and writes one key press into its input. Null when it was written, else why not.
    /// </summary>
    public static string? Send(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId))
        {
            return $"Couldn't attach to the console of process {processId} (error {Marshal.GetLastPInvokeError()}).";
        }

        try
        {
            using var input = CreateFileW("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (input.IsInvalid)
            {
                return $"Couldn't open the console's input (error {Marshal.GetLastPInvokeError()}).";
            }

            var records = new InputRecord[2];
            for (var i = 0; i < records.Length; i++)
            {
                records[i].EventType = KeyEvent;
                records[i].Key.KeyDown = i == 0 ? 1 : 0;
                records[i].Key.RepeatCount = 1;
                records[i].Key.VirtualKeyCode = 0x51; // Q
                records[i].Key.UnicodeChar = 'q';
            }

            return WriteConsoleInputW(input, records, (uint)records.Length, out var written) && written == records.Length
                ? null
                : $"Couldn't write to the console's input (error {Marshal.GetLastPInvokeError()}).";
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>
    /// Runs <paramref name="senderExe"/> <c>--send-exit-key</c> for <paramref name="processId"/>, without a window, and
    /// waits for it at most <see cref="SenderLimit"/>. True when it wrote the key, or may have (it was still running at
    /// the limit: a slow start), so that the caller waits for the process to end before killing it; false when it surely
    /// didn't (it couldn't start, or said it couldn't write).
    /// </summary>
    public static bool RunSender(string senderExe, int processId)
    {
        try
        {
            var info = new ProcessStartInfo(senderExe) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Flag);
            info.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var sender = Process.Start(info);
            if (sender is null)
            {
                return false;
            }

            if (!sender.WaitForExit(SenderLimit))
            {
                sender.Kill();
                return true;
            }

            return sender.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>The process id in <c>--send-exit-key &lt;pid&gt;</c>, or null when <paramref name="args"/> isn't that.</summary>
    public static int? Requested(string[] args) =>
        args is [Flag, var pid] && int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    private const ushort KeyEvent = 1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KeyEventRecord Key;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleInputW(Microsoft.Win32.SafeHandles.SafeFileHandle input, InputRecord[] records, uint count, out uint written);
}
