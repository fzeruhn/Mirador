using Mirador.Core.Modules;

namespace Mirador.Pimax.Tests;

public class PimaxModelsTests
{
    [Fact]
    public void The_four_p3c_headsets_are_known_by_their_usb_ids()
    {
        Assert.Equal(
            [(0x34A4, 0x0018, "Pimax Crystal Light"), (0x34A4, 0x0040, "Pimax Crystal Super"), (0x34A4, 0x0042, "Pimax Dream Air SE"), (0x34A4, 0x0044, "Pimax Dream Air")],
            PimaxModels.All.Select(m => ((int)m.Board.Vid, (int)m.Board.Pid, m.Name)));
    }

    [Fact]
    public void Only_the_dream_air_gets_mirador_s_eye_steps_and_is_tested()
    {
        Assert.Equal([PimaxModels.DreamAir], PimaxModels.All.Where(m => m.EyeSteps));
        Assert.Equal([PimaxModels.DreamAir], PimaxModels.All.Where(m => m.Tested));
    }

    [Fact]
    public void The_dream_airs_use_steamvrs_lighthouse_driver_the_crystals_are_inside_out()
    {
        Assert.Equal(["Pimax Dream Air SE", "Pimax Dream Air"], PimaxModels.All.Where(m => m.Lighthouse).Select(m => m.Name));
    }

    [Fact]
    public void The_plugged_in_model_is_found_by_its_board()
    {
        Assert.Equal(PimaxModels.CrystalSuper, PimaxModels.Detect(new HashSet<UsbId> { new(0x34A4, 0x0040), new(0x046D, 0xC52B) }));
        Assert.Equal(PimaxModels.DreamAir, PimaxModels.Detect(new HashSet<UsbId> { DreamAirIds.Board }));
        Assert.Null(PimaxModels.Detect(new HashSet<UsbId> { new(0x046D, 0xC52B) }));
    }

    [Fact]
    public void With_two_plugged_in_the_tested_dream_air_is_the_one()
    {
        Assert.Equal(PimaxModels.DreamAir, PimaxModels.Detect(new HashSet<UsbId> { PimaxModels.CrystalLight.Board, DreamAirIds.Board }));
    }

    [Fact]
    public void With_no_pimax_plugged_in_the_steps_are_the_dream_airs()
    {
        Assert.Equal(PimaxModels.DreamAir, PimaxModels.DetectOrDreamAir(new HashSet<UsbId>()));
    }
}
