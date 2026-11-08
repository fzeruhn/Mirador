using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Mirador.Core.BaseStations;
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
/// Mirador's icon in the notification area: its look follows Monado's state; its menu has Start or Stop, the base
/// stations' power, Open and Quit; a left click shows the window or hides it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon icon;

    private readonly MenuFlyoutItem startStop;
    private readonly MenuFlyoutItem stationsOn;
    private readonly MenuFlyoutItem stationsOff;

    // Kept for as long as the tray icon lives: the tray uses an icon's handle again when Explorer restarts.
    private readonly Dictionary<TrayState, System.Drawing.Icon> icons;
    private TrayState? shown;

    /// <param name="open">Shows the window.</param>
    /// <param name="toggle">A left click: hides the window when it is in front, else shows it.</param>
    /// <param name="startOrStop">Starts Monado when stopped, stops it when running.</param>
    /// <param name="switchStations">Switches the selected base stations: <see cref="StationCommand.On"/> to power on, <see cref="StationCommand.Sleep"/> to power off.</param>
    /// <param name="quit">Quits Mirador.</param>
    public TrayIcon(Action open, Action toggle, Func<Task> startOrStop, Func<StationCommand, Task> switchStations, Func<Task> quit)
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
        stationsOn = new MenuFlyoutItem { Text = "Power on base stations", Command = new AsyncRelayCommand(() => switchStations(StationCommand.On)) };
        stationsOff = new MenuFlyoutItem { Text = "Power off base stations", Command = new AsyncRelayCommand(() => switchStations(StationCommand.Sleep)) };
        var menu = new MenuFlyout();
        menu.Items.Add(startStop);
        menu.Items.Add(stationsOn);
        menu.Items.Add(stationsOff);
        menu.Items.Add(new MenuFlyoutItem { Text = "Open", Command = new RelayCommand(open) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = new AsyncRelayCommand(quit) });

        icon = new TaskbarIcon
        {
            ToolTipText = "Mirador",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(toggle),
            NoLeftClickDelay = true,
        };
        Show(TrayState.Stopped, "Mirador: Monado is stopped", "Start");
        icon.ForceCreate(enablesEfficiencyMode: false);

        // The keyboard's select on the icon (Space or Enter) does what the left click does. TaskbarIcon's own
        // TrayKeyboardSelect event is compiled out of the package (WinRT events are off), so this uses the message
        // window's public keyboard event. Only KeySelect: Select also arrives with a mouse click, which the
        // LeftClickCommand already handles.
        icon.TrayIcon.MessageWindow.SubscribeToKeyboardEventReceived((_, e) =>
        {
            if (e.KeyboardEvent == H.NotifyIcon.Core.KeyboardEvent.KeySelect)
            {
                toggle();
            }
        });
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

    /// <summary>Enables the base stations' two items, or greys them. Call on the UI thread.</summary>
    public void ShowStations(bool enabled)
    {
        stationsOn.IsEnabled = enabled;
        stationsOff.IsEnabled = enabled;
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
