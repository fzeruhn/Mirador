using System.Runtime.InteropServices;

namespace Mirador.App.Services;

/// <summary>
/// Keeps a window from being made smaller than a size meant at 100 % scale, on any display. Windows asks the window
/// for its smallest size (<c>WM_GETMINMAXINFO</c>) whenever it is resized, and the answer is scaled to the display the
/// window is on at that moment. (The presenter's own minimum size is in pixels and doesn't follow the window from one
/// display's scale to another's.)
/// </summary>
internal sealed class MinimumSize
{
    private const uint WmGetMinMaxInfo = 0x0024;

    private readonly int width;
    private readonly int height;

    // Windows calls this for as long as the window lives: the field keeps the delegate from being collected.
    private readonly SubclassProc proc;

    /// <param name="window">The window's handle.</param>
    /// <param name="width">The smallest width at 100 % scale, the frame included.</param>
    /// <param name="height">The smallest height at 100 % scale, the frame included.</param>
    public MinimumSize(IntPtr window, int width, int height)
    {
        this.width = width;
        this.height = height;
        proc = OnMessage;

        // Should it fail, the window just has no minimum: not worth stopping Mirador for.
        SetWindowSubclass(window, proc, 1, 0);
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message != WmGetMinMaxInfo)
        {
            return result;
        }

        var scale = GetDpiForWindow(window) / 96.0;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MinTrackSize.X = (int)Math.Ceiling(width * scale);
        info.MinTrackSize.Y = (int)Math.Ceiling(height * scale);
        Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
        return IntPtr.Zero;
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
