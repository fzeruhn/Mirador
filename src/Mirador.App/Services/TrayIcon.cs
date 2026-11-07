using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace Mirador.App.Services;

/// <summary>How the tray icon looks.</summary>
public enum TrayState
{
    Stopped,
    Running,

    /// <summary>Monado isn't running: the last Start failed, it stopped by itself, or a status line is an error.</summary>
    Problem,

    /// <summary>Monado runs, but a status line is an error (it lost the headset, say).</summary>
    RunningProblem,
}

/// <summary>
/// Mirador's icon in the notification area: its look follows Monado's state; its menu has Start or Stop, Open and Quit;
/// a double click opens the window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon icon;
    private readonly MenuFlyoutItem startStop;

    // Kept for as long as the tray icon lives: the tray uses an icon's handle again when Explorer restarts.
    private readonly Dictionary<TrayState, System.Drawing.Icon> icons;
    private TrayState? shown;

    /// <param name="open">Shows the window.</param>
    /// <param name="startOrStop">Starts Monado when stopped, stops it when running.</param>
    /// <param name="quit">Quits Mirador.</param>
    public TrayIcon(Action open, Func<Task> startOrStop, Func<Task> quit)
    {
        icons = new Dictionary<TrayState, System.Drawing.Icon>
        {
            [TrayState.Stopped] = Load("TrayStopped.ico"),
            [TrayState.Running] = Load("TrayRunning.ico"),
            [TrayState.Problem] = Load("TrayProblem.ico"),
            [TrayState.RunningProblem] = Load("TrayRunningProblem.ico"),
        };

        // Stop must work while a Start from the tray still runs.
        startStop = new MenuFlyoutItem { Text = "Start", Command = new AsyncRelayCommand(startOrStop, AsyncRelayCommandOptions.AllowConcurrentExecutions) };
        var menu = new MenuFlyout();
        menu.Items.Add(startStop);
        menu.Items.Add(new MenuFlyoutItem { Text = "Open", Command = new RelayCommand(open) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = new AsyncRelayCommand(quit) });

        icon = new TaskbarIcon
        {
            ToolTipText = "Mirador",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            DoubleClickCommand = new RelayCommand(open),
        };
        Show(TrayState.Stopped, "Mirador: Monado is stopped", "Start");
        icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>Changes the icon, its tooltip and the menu's Start/Stop item. Call on the UI thread.</summary>
    public void Show(TrayState state, string tooltip, string startStopText)
    {
        if (shown != state)
        {
            icon.UpdateIcon(icons[state]);
            shown = state;
        }

        icon.ToolTipText = tooltip;
        startStop.Text = startStopText;
    }

    public void Dispose()
    {
        icon.Dispose();
        foreach (var i in icons.Values)
        {
            i.Dispose();
        }
    }

    /// <summary>The icon file's image at the size Windows draws tray icons on this display, so it isn't scaled blurry.</summary>
    private static System.Drawing.Icon Load(string file)
    {
        const int SmallIconWidth = 49; // SM_CXSMICON
        var size = GetSystemMetricsForDpi(SmallIconWidth, GetDpiForSystem());
        return new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", file), size > 0 ? size : 16, size > 0 ? size : 16);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
