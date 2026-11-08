namespace Mirador.Core;

/// <summary>What a left click on the tray icon does to the window: hide it, or bring it to the front.</summary>
public static class TrayClick
{
    /// <summary>
    /// How soon after the window lost the focus a click on the icon still counts as having been in front: the click
    /// itself takes the focus before Mirador hears of it.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    /// <summary>True when the window is shown, not minimized, and in front (or was until the click deactivated it).</summary>
    public static bool ShouldHide(bool visible, bool minimized, bool foreground, TimeSpan sinceDeactivated) =>
        visible && !minimized && (foreground || sinceDeactivated < Grace);
}
