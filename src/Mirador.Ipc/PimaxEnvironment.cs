namespace Mirador.Ipc;

/// <summary>
/// The Pimax module's id in a helper request (Mirador.md §4.11 item 53): the helper writes the Dream Air's table
/// (<see cref="DreamAirEnvironment"/>) for it, the only Pimax model with an elevated table yet; the module asks for it
/// only while a Dream Air is plugged in. <see cref="DreamAirEnvironment.ModuleId"/>, the module's id before, is still
/// accepted for one release.
/// </summary>
public static class PimaxEnvironment
{
    public const string ModuleId = "pimax";
}
