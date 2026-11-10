using Mirador.Core.Gpu;

namespace Mirador.Core.Tests;

public class GpuSchedulingTests
{
    [Fact]
    public void On_when_every_adapter_that_offers_it_has_it_on()
    {
        // Windows' basic display adapter never offers it: only the GPUs that do count.
        Assert.Equal(GpuSchedulingState.On, GpuScheduling.StateOf([new(Supported: true, Enabled: true), new(Supported: false, Enabled: false)]));
    }

    [Fact]
    public void Off_when_an_adapter_offers_it_but_has_it_off()
    {
        Assert.Equal(GpuSchedulingState.Off, GpuScheduling.StateOf([new(Supported: true, Enabled: false)]));
    }

    [Fact]
    public void Not_offered_when_no_adapter_offers_it()
    {
        Assert.Equal(GpuSchedulingState.NotOffered, GpuScheduling.StateOf([new(Supported: false, Enabled: false)]));
    }

    [Fact]
    public void Unknown_without_adapters()
    {
        Assert.Equal(GpuSchedulingState.Unknown, GpuScheduling.StateOf([]));
    }

    [Fact]
    public void Home_warns_while_it_is_off_or_not_offered_and_says_nothing_otherwise()
    {
        Assert.Equal(
            "Hardware-accelerated GPU scheduling is off. Monado as administrator needs it for the GPU's realtime priority, and Mirador is tested with it on. Turn it on in Windows' graphics settings, then restart Windows.",
            GpuScheduling.Notice(GpuSchedulingState.Off));
        Assert.Equal(
            "This GPU's driver doesn't offer hardware-accelerated GPU scheduling, which Monado as administrator needs for the GPU's realtime priority. A newer driver may have it.",
            GpuScheduling.Notice(GpuSchedulingState.NotOffered));
        Assert.Null(GpuScheduling.Notice(GpuSchedulingState.On));
        Assert.Null(GpuScheduling.Notice(GpuSchedulingState.Unknown));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void This_pcs_adapters_can_be_asked()
    {
        // Finn's PC: an RTX 5070 Ti with it on (Mirador.md, the tested setup; HwSchMode 2).
        Assert.NotEmpty(GpuScheduling.Query());
        Assert.Equal(GpuSchedulingState.On, GpuScheduling.Now());
    }
}
