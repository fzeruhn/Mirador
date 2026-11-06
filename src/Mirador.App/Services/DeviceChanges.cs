using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Mirador.App.Services;

/// <summary>
/// Says when devices were plugged in or out. Windows tells every top-level window (<c>WM_DEVICECHANGE</c>,
/// <c>DBT_DEVNODES_CHANGED</c>), several times for one device, so <see cref="Changed"/> comes once things have been
/// quiet for a second. On the window's thread.
/// </summary>
internal sealed class DeviceChanges
{
    private const uint WmDeviceChange = 0x0219;
    private const nint DbtDevNodesChanged = 0x0007;

    private readonly DispatcherQueueTimer quiet;

    // Windows calls this for as long as the window lives: the field keeps the delegate from being collected.
    private readonly SubclassProc proc;

    /// <param name="window">The window's handle; its thread must have a <see cref="DispatcherQueue"/>.</param>
    public DeviceChanges(IntPtr window)
    {
        quiet = DispatcherQueue.GetForCurrentThread().CreateTimer();
        quiet.Interval = TimeSpan.FromSeconds(1);
        quiet.IsRepeating = false;
        quiet.Tick += (_, _) => Changed?.Invoke();
        proc = OnMessage;

        // Should it fail, the checks still run when Home is shown: not worth stopping Mirador for.
        SetWindowSubclass(window, proc, 2, 0);
    }

    /// <summary>Devices changed, a second ago.</summary>
    public event Action? Changed;

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WmDeviceChange && wParam == DbtDevNodesChanged)
        {
            quiet.Stop();
            quiet.Start();
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
