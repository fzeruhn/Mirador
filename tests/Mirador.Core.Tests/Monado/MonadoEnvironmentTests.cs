using System.Globalization;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Monado;

public class MonadoEnvironmentTests
{
    /// <summary>A small stand-in for the process environment; the real one is never read.</summary>
    private static Dictionary<string, string> Inherited() => new()
    {
        ["SystemRoot"] = @"C:\WINDOWS",
        ["Path"] = @"C:\WINDOWS\system32;C:\WINDOWS",
    };

    private static Profile Custom() => ProfileSet.Defaults().Profiles[1];

    private static Profile CustomWith(params EnvVar[] extra) => Custom() with { ExtraEnvironment = extra };

    [Fact]
    public void Default_settings_map_to_monado_variables()
    {
        var env = MonadoEnvironment.Build(Inherited(), new MonadoSettings(), Custom(), []);

        Assert.Equal("3000", env["LH_DISCOVER_WAIT_MS"]);
        Assert.Equal("100", env["XRT_COMPOSITOR_SCALE_PERCENTAGE"]);
        Assert.Equal("6", env["U_PACING_COMP_MIN_TIME_MS"]);
        Assert.False(env.ContainsKey("IPC_EXIT_ON_DISCONNECT"));
        Assert.Equal(@"C:\WINDOWS", env["SystemRoot"]); // inherited kept
        Assert.Equal(@"C:\WINDOWS\system32;C:\WINDOWS", env["Path"]);
    }

    [Fact]
    public void Settings_values_are_written_as_they_are()
    {
        var settings = new MonadoSettings(LighthouseWaitMs: 12000, RenderScalePercent: 135, CompositorMinTimeMs: 11);

        var env = MonadoEnvironment.Build(Inherited(), settings, Custom(), []);

        Assert.Equal("12000", env["LH_DISCOVER_WAIT_MS"]);
        Assert.Equal("135", env["XRT_COMPOSITOR_SCALE_PERCENTAGE"]);
        Assert.Equal("11", env["U_PACING_COMP_MIN_TIME_MS"]);
    }

    [Fact]
    public void Numbers_are_formatted_with_the_invariant_culture()
    {
        // Swedish writes a negative sign as U+2212, which Monado cannot parse.
        var settings = new MonadoSettings(LighthouseWaitMs: -5, RenderScalePercent: -5, CompositorMinTimeMs: -5);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");

            var env = MonadoEnvironment.Build(Inherited(), settings, Custom(), []);

            Assert.Equal("-5", env["LH_DISCOVER_WAIT_MS"]);
            Assert.Equal("-5", env["XRT_COMPOSITOR_SCALE_PERCENTAGE"]);
            Assert.Equal("-5", env["U_PACING_COMP_MIN_TIME_MS"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Stop_when_last_app_closes_sets_exit_on_disconnect()
    {
        var env = MonadoEnvironment.Build(Inherited(), new MonadoSettings(StopWhenLastAppCloses: true), Custom(), []);

        Assert.Equal("1", env["IPC_EXIT_ON_DISCONNECT"]);
    }

    [Fact]
    public void An_inherited_exit_on_disconnect_passes_through_when_the_setting_is_off()
    {
        var inherited = Inherited();
        inherited["IPC_EXIT_ON_DISCONNECT"] = "1";

        var env = MonadoEnvironment.Build(inherited, new MonadoSettings(StopWhenLastAppCloses: false), Custom(), []);

        Assert.Equal("1", env["IPC_EXIT_ON_DISCONNECT"]);
    }

    [Fact]
    public void Settings_override_inherited_values()
    {
        var inherited = Inherited();
        inherited["LH_DISCOVER_WAIT_MS"] = "1";

        var env = MonadoEnvironment.Build(inherited, new MonadoSettings(), Custom(), []);

        Assert.Equal("3000", env["LH_DISCOVER_WAIT_MS"]);
    }

    [Fact]
    public void Module_values_override_settings()
    {
        EnvVar[] moduleFixed = [new("LH_DISCOVER_WAIT_MS", "9000"), new("XRT_DEBUG_GUI", "0")];

        var env = MonadoEnvironment.Build(Inherited(), new MonadoSettings(), Custom(), moduleFixed);

        Assert.Equal("9000", env["LH_DISCOVER_WAIT_MS"]);
        Assert.Equal("0", env["XRT_DEBUG_GUI"]);
        Assert.Equal("100", env["XRT_COMPOSITOR_SCALE_PERCENTAGE"]); // other settings untouched
    }

    [Fact]
    public void Extra_variables_win_over_everything()
    {
        EnvVar[] moduleFixed = [new("DREAMAIR_VK_LAYER", "1")];
        var profile = CustomWith(new EnvVar("dreamair_vk_layer", "0"));

        var env = MonadoEnvironment.Build(Inherited(), new MonadoSettings(), profile, moduleFixed);

        Assert.Equal("0", env["DREAMAIR_VK_LAYER"]);
        Assert.Single(env.Keys, k => string.Equals(k, "DREAMAIR_VK_LAYER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extra_variables_win_over_settings_and_inherited_values()
    {
        var profile = CustomWith(new EnvVar("LH_DISCOVER_WAIT_MS", "500"), new EnvVar("SystemRoot", @"D:\WINDOWS"));

        var env = MonadoEnvironment.Build(Inherited(), new MonadoSettings(), profile, []);

        Assert.Equal("500", env["LH_DISCOVER_WAIT_MS"]);
        Assert.Equal(@"D:\WINDOWS", env["SystemRoot"]);
    }

    [Fact]
    public void A_later_layer_keeps_its_own_spelling_of_the_name()
    {
        var inherited = Inherited();
        inherited["lh_discover_wait_ms"] = "1";
        EnvVar[] moduleFixed = [new("Xrt_Compositor_Scale_Percentage", "80")];
        var profile = CustomWith(new EnvVar("path", "X"));

        var env = MonadoEnvironment.Build(inherited, new MonadoSettings(), profile, moduleFixed);

        // A plain list, so these checks compare spellings exactly instead of using the dictionary's ignore-case lookup.
        var names = env.Keys.ToList();
        Assert.Contains("LH_DISCOVER_WAIT_MS", names); // settings over inherited
        Assert.DoesNotContain("lh_discover_wait_ms", names);
        Assert.Contains("Xrt_Compositor_Scale_Percentage", names); // module over settings
        Assert.DoesNotContain("XRT_COMPOSITOR_SCALE_PERCENTAGE", names);
        Assert.Contains("path", names); // extra over inherited
        Assert.DoesNotContain("Path", names);
        Assert.Equal("X", env["PATH"]);
    }

    [Fact]
    public void Names_are_matched_ignoring_case()
    {
        var env = MonadoEnvironment.Build(Inherited(), new MonadoSettings(), Custom(), []);

        Assert.Equal(@"C:\WINDOWS", env["SYSTEMROOT"]);
        Assert.Equal("3000", env["lh_discover_wait_ms"]);
    }

    [Fact]
    public void Extra_variables_with_empty_names_are_ignored()
    {
        var profile = CustomWith(new EnvVar("", "a"), new EnvVar("   ", "b"), new EnvVar("\t", "c"), new EnvVar("KEPT", "d"));

        var env = MonadoEnvironment.Build(Inherited(), new MonadoSettings(), profile, []);

        Assert.Equal("d", env["KEPT"]);
        Assert.All(env.Keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        Assert.Equal(2 + 3 + 1, env.Count); // inherited, the three settings, KEPT
    }

    [Fact]
    public void With_nothing_inherited_only_what_mirador_sets_is_listed()
    {
        var profile = CustomWith(new EnvVar("EXTRA", "1"));
        EnvVar[] moduleFixed = [new("FIXED", "2")];

        var env = MonadoEnvironment.Build(new Dictionary<string, string>(), new MonadoSettings(StopWhenLastAppCloses: true), profile, moduleFixed);

        Assert.Equal(
            ["EXTRA", "FIXED", "IPC_EXIT_ON_DISCONNECT", "LH_DISCOVER_WAIT_MS", "U_PACING_COMP_MIN_TIME_MS", "XRT_COMPOSITOR_SCALE_PERCENTAGE"],
            env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_inherited_dictionary_is_left_alone_and_the_result_is_a_new_one()
    {
        var inherited = Inherited();

        var env = MonadoEnvironment.Build(inherited, new MonadoSettings(), CustomWith(new EnvVar("EXTRA", "1")), []);

        Assert.Equal(2, inherited.Count);
        Assert.NotSame(inherited, env);
    }
}
