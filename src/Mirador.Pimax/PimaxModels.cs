using Mirador.Core.Modules;

namespace Mirador.Pimax;

/// <summary>A Pimax headset of the P3C platform and what Mirador does for it (Mirador.md §4.11 item 53).</summary>
/// <param name="Board">The USB id of its board, by which it is told apart.</param>
/// <param name="Name">Its name.</param>
/// <param name="EyeSteps">
/// Whether Mirador's eye steps run for it: the eye geometry upload, calibration backups and "Check my calibration". Only
/// the Dream Air: the geometry Mirador uploads is the Dream Air's (the Crystal Super's Tobii tracker has its own).
/// </param>
/// <param name="Lighthouse">Whether it tracks with SteamVR's Lighthouse driver (the Crystals are mostly inside-out).</param>
/// <param name="Tested">Whether Mirador has run it.</param>
public sealed record PimaxModel(UsbId Board, string Name, bool EyeSteps, bool Lighthouse, bool Tested);

/// <summary>
/// The Pimax headsets Mirador knows: "P3C" is Pimax's platform, not one headset. Every one gets Pimax's service and the
/// programs that hold the headset stopped for a session; the other steps run only for a model that has them.
/// </summary>
public static class PimaxModels
{
    public static readonly PimaxModel CrystalLight = new(new UsbId(0x34A4, 0x0018), "Pimax Crystal Light", EyeSteps: false, Lighthouse: false, Tested: false);
    public static readonly PimaxModel CrystalSuper = new(new UsbId(0x34A4, 0x0040), "Pimax Crystal Super", EyeSteps: false, Lighthouse: false, Tested: false);

    /// <summary>Lighthouse tracking per Pimax's own headset database; not seen on a unit.</summary>
    public static readonly PimaxModel DreamAirSe = new(new UsbId(0x34A4, 0x0042), "Pimax Dream Air SE", EyeSteps: false, Lighthouse: true, Tested: false);

    public static readonly PimaxModel DreamAir = new(DreamAirIds.Board, "Pimax Dream Air", EyeSteps: true, Lighthouse: true, Tested: true);

    public static IReadOnlyList<PimaxModel> All { get; } = [CrystalLight, CrystalSuper, DreamAirSe, DreamAir];

    /// <summary>The model whose board is plugged in, the tested one first when there are two; or null.</summary>
    public static PimaxModel? Detect(IReadOnlySet<UsbId> devices) =>
        All.OrderByDescending(m => m.Tested).FirstOrDefault(m => devices.Contains(m.Board));

    /// <summary>
    /// The plugged-in model; with none, the Dream Air's steps (the tested headset: its checks then say the board is
    /// missing).
    /// </summary>
    public static PimaxModel DetectOrDreamAir(IReadOnlySet<UsbId> devices) => Detect(devices) ?? DreamAir;
}
