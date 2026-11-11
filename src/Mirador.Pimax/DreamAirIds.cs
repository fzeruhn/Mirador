using Mirador.Core.Modules;

namespace Mirador.Pimax;

/// <summary>The USB ids of the Dream Air's parts.</summary>
public static class DreamAirIds
{
    /// <summary>The headset board, which drives the panels.</summary>
    public static readonly UsbId Board = new(0x34A4, 0x0044);

    /// <summary>Tobii's eye tracker.</summary>
    public static readonly UsbId EyeTracker = new(0x2104, 0x0561);

    /// <summary>The Lighthouse receiver Monado tracks the headset through, with SteamVR's driver.</summary>
    public static readonly UsbId Receiver = new(0x28DE, 0x2300);
}
