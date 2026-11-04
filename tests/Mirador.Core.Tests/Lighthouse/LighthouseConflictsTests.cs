using Mirador.Core.Lighthouse;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Lighthouse;

public class LighthouseConflictsTests
{
    private static ProcInfo Proc(string name, int id = 1) => new(name, id);

    private static Profile ProfileWith(params EnvVar[] extra) =>
        new("p", "Test", null, BuildSource.Bundled, extra, new Dictionary<string, string>());

    [Fact]
    public void Finds_the_steamvr_processes_sorted_and_without_case()
    {
        var found = LighthouseConflicts.Find([Proc("vrserver"), Proc("VRMONITOR"), Proc("explorer")]);

        Assert.Equal(["vrmonitor", "vrserver"], found);
    }

    [Fact]
    public void Nothing_is_found_when_none_of_them_runs()
    {
        Assert.Empty(LighthouseConflicts.Find([Proc("explorer"), Proc("monado-service")]));
        Assert.Empty(LighthouseConflicts.Find([]));
    }

    [Fact]
    public void A_process_running_twice_is_listed_once()
    {
        var found = LighthouseConflicts.Find([Proc("vrserver", 10), Proc("VrServer", 11), Proc("vrserver", 12)]);

        Assert.Equal(["vrserver"], found);
    }

    [Fact]
    public void Every_process_that_holds_the_receiver_is_found_under_its_own_name()
    {
        var found = LighthouseConflicts.Find(
            [Proc("vrserver"), Proc("vrcompositor"), Proc("VRMonitor"), Proc("ovrlighthousemanager")]);

        Assert.Equal(["OVRLighthouseManager", "vrcompositor", "vrmonitor", "vrserver"], found);
    }

    [Fact]
    public void A_name_that_only_contains_one_of_them_is_not_a_match()
    {
        Assert.Empty(LighthouseConflicts.Find([Proc("vrserver_helper"), Proc("myvrserver"), Proc("vrserver.exe")]));
    }

    [Fact]
    public void The_message_names_what_to_close()
    {
        var message = LighthouseConflicts.Message(["vrserver", "vrmonitor"]);

        Assert.Equal("Close vrserver, vrmonitor first: they use the Lighthouse receiver.", message);
    }

    [Fact]
    public void The_message_for_a_single_program_says_it_uses_the_receiver()
    {
        var message = LighthouseConflicts.Message(["OVRLighthouseManager"]);

        Assert.Equal("Close OVRLighthouseManager first: it uses the Lighthouse receiver.", message);
    }

    [Fact]
    public void A_message_needs_something_to_name()
    {
        Assert.Throws<ArgumentException>(() => LighthouseConflicts.Message([]));
    }

    [Fact]
    public void The_message_follows_what_find_returns()
    {
        var found = LighthouseConflicts.Find([Proc("vrserver"), Proc("vrmonitor")]);

        Assert.Equal("Close vrmonitor, vrserver first: they use the Lighthouse receiver.", LighthouseConflicts.Message(found));
    }

    [Fact]
    public void A_module_that_uses_lighthouse_is_enough()
    {
        Assert.True(LighthouseConflicts.UsesLighthouse(ProfileWith(), moduleUsesLighthouse: true));
    }

    [Fact]
    public void Neither_the_module_nor_the_environment_means_no()
    {
        Assert.False(LighthouseConflicts.UsesLighthouse(ProfileWith(), moduleUsesLighthouse: false));
        Assert.False(LighthouseConflicts.UsesLighthouse(ProfileWith(new EnvVar("OTHER", "1")), moduleUsesLighthouse: false));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("On")]
    [InlineData("yes")]
    [InlineData("Yes")]
    [InlineData(" 1 ")]
    public void These_values_of_steamvr_lh_enable_turn_lighthouse_on(string value)
    {
        var profile = ProfileWith(new EnvVar("STEAMVR_LH_ENABLE", value));

        Assert.True(LighthouseConflicts.UsesLighthouse(profile, moduleUsesLighthouse: false));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("enabled")]
    public void Other_values_of_steamvr_lh_enable_do_not(string value)
    {
        var profile = ProfileWith(new EnvVar("STEAMVR_LH_ENABLE", value));

        Assert.False(LighthouseConflicts.UsesLighthouse(profile, moduleUsesLighthouse: false));
    }

    [Fact]
    public void The_last_steamvr_lh_enable_entry_wins()
    {
        var off = ProfileWith(new EnvVar("STEAMVR_LH_ENABLE", "1"), new EnvVar("STEAMVR_LH_ENABLE", "0"));
        var on = ProfileWith(new EnvVar("STEAMVR_LH_ENABLE", "0"), new EnvVar("STEAMVR_LH_ENABLE", "1"));

        Assert.False(LighthouseConflicts.UsesLighthouse(off, moduleUsesLighthouse: false));
        Assert.True(LighthouseConflicts.UsesLighthouse(on, moduleUsesLighthouse: false));
    }

    [Fact]
    public void The_variable_name_is_matched_the_way_windows_does_without_case()
    {
        var profile = ProfileWith(new EnvVar("steamvr_lh_enable", "1"));

        Assert.True(LighthouseConflicts.UsesLighthouse(profile, moduleUsesLighthouse: false));
    }

    [Fact]
    public void A_variable_that_only_starts_with_the_name_does_not_count()
    {
        var profile = ProfileWith(new EnvVar("STEAMVR_LH_ENABLE_FOO", "1"), new EnvVar("MY_STEAMVR_LH_ENABLE", "1"));

        Assert.False(LighthouseConflicts.UsesLighthouse(profile, moduleUsesLighthouse: false));
    }

    [Fact]
    public void The_module_wins_over_a_profile_that_turns_it_off()
    {
        var profile = ProfileWith(new EnvVar("STEAMVR_LH_ENABLE", "0"));

        Assert.True(LighthouseConflicts.UsesLighthouse(profile, moduleUsesLighthouse: true));
    }
}
