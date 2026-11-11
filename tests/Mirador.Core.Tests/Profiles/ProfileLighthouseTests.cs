using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

public class ProfileLighthouseTests
{
    private static Profile Make(string? moduleId = null, bool? lighthouse = null, params EnvVar[] env) =>
        new("p", "P", moduleId, BuildSource.Mirador, env, new Dictionary<string, string>(), lighthouse);

    [Fact]
    public void Dream_air_means_on() =>
        Assert.True(Make("dreamair").UsesBaseStations(moduleUsesSteamVrLighthouse: true));

    [Fact]
    public void Steamvr_lh_enable_means_on() =>
        Assert.True(Make(null, null, new EnvVar("STEAMVR_LH_ENABLE", "1")).UsesBaseStations(false));

    [Fact]
    public void Plain_custom_means_off() =>
        Assert.False(Make().UsesBaseStations(false));

    [Fact]
    public void Explicit_value_wins()
    {
        Assert.False(Make("dreamair", false).UsesBaseStations(true));
        Assert.True(Make(null, true).UsesBaseStations(false));
    }
}
