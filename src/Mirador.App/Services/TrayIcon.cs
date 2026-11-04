using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace Mirador.App.Services;

/// <summary>How the tray icon looks.</summary>
public enum TrayState
{
    Stopped,
    Running,

    /// <summary>The last Start failed, or a status line is an error.</summary>
    Problem,
}

/// <summary>
/// Mirador's icon in the notification area: its look follows Monado's state; its menu has Start or Stop, Open and Quit;
/// a double click opens the window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon icon;
    private readonly MenuFlyoutItem startStop;
    private readonly string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
    private TrayState? shown;

    /// <param name="open">Shows the window.</param>
    /// <param name="startOrStop">Starts Monado when stopped, stops it when running.</param>
    /// <param name="quit">Quits Mirador.</param>
    public TrayIcon(Action open, Func<Task> startOrStop, Func<Task> quit)
    {
        startStop = new MenuFlyoutItem { Text = "Start", Command = new AsyncRelayCommand(startOrStop) };
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
            var file = state switch
            {
                TrayState.Running => "TrayRunning.ico",
                TrayState.Problem => "TrayProblem.ico",
                _ => "TrayStopped.ico",
            };
            icon.UpdateIcon(new System.Drawing.Icon(Path.Combine(assets, file)));
            shown = state;
        }

        icon.ToolTipText = tooltip;
        startStop.Text = startStopText;
    }

    public void Dispose() => icon.Dispose();
}
