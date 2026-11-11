using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

public class ProfileSetTests
{
    [Fact]
    public void Defaults_are_dream_air_and_custom()
    {
        var s = ProfileSet.Defaults();
        Assert.Equal(["dreamair", "custom"], s.Profiles.Select(p => p.Id));
        Assert.Equal("Dream Air", s.Profiles[0].Name);
        Assert.Equal("pimax", s.Profiles[0].ModuleId); // the module's id now, so nothing migrates (or saves) at first run
        Assert.Null(s.Profiles[1].ModuleId);
        Assert.Equal("dreamair", s.SelectedId);
        Assert.Same(s.Profiles[0], s.Selected);
        Assert.All(s.Profiles, p => Assert.Equal(SourceKind.Mirador, p.Monado.Kind));
        Assert.All(s.Profiles, p => Assert.Empty(p.ExtraEnvironment));
        Assert.All(s.Profiles, p => Assert.Empty(p.ModuleOptions));
    }

    [Fact]
    public void Duplicate_gets_new_id_and_name()
    {
        var original = ProfileSet.Defaults().Profiles[0] with
        {
            Monado = new BuildSource(SourceKind.Folder, @"D:\monado"),
            ExtraEnvironment = [new EnvVar("XRT_DEBUG_GUI", "1")],
            ModuleOptions = new Dictionary<string, string> { ["layerFolder"] = @"D:\layer" },
        };
        var set = ProfileSet.Defaults().Replace(original);

        var result = set.Duplicate("dreamair", "Dev");

        Assert.Equal(3, result.Profiles.Count);
        var copy = result.Profiles[^1];
        Assert.NotEqual("dreamair", copy.Id);
        Assert.Matches("^[0-9a-f]{32}$", copy.Id);
        Assert.Equal("Dev", copy.Name);
        Assert.Equal("pimax", copy.ModuleId);
        Assert.Equal(original.Monado, copy.Monado);
        Assert.Equal(original.ExtraEnvironment, copy.ExtraEnvironment);
        Assert.Equal(original.ModuleOptions, copy.ModuleOptions);
        Assert.Equal("dreamair", result.SelectedId);
        Assert.Equal(2, set.Profiles.Count); // the original set is untouched
    }

    [Fact]
    public void Duplicate_does_not_share_its_collections_with_the_original()
    {
        var options = new Dictionary<string, string> { ["layerFolder"] = @"D:\layer" };
        var set = ProfileSet.Defaults().Replace(ProfileSet.Defaults().Profiles[0] with { ModuleOptions = options });

        var copy = set.Duplicate("dreamair", "Dev").Profiles[^1];
        options["layerFolder"] = @"D:\other";

        Assert.Equal(@"D:\layer", copy.ModuleOptions["layerFolder"]);
    }

    [Fact]
    public void Names_must_be_unique_and_not_empty()
    {
        var s = ProfileSet.Defaults();

        var taken = Assert.Throws<MiradorException>(() => s.Rename("custom", "dream air"));
        Assert.Equal("A profile called dream air already exists.", taken.Message);

        var empty = Assert.Throws<MiradorException>(() => s.Rename("custom", " "));
        Assert.Equal("A profile needs a name.", empty.Message);

        Assert.Throws<MiradorException>(() => s.Duplicate("custom", "  DREAM AIR "));
        Assert.Throws<MiradorException>(() => s.Duplicate("custom", ""));
        Assert.Throws<MiradorException>(() => s.Add(s.Profiles[1] with { Id = "other", Name = "Custom" }));
    }

    [Fact]
    public void Names_are_trimmed()
    {
        var s = ProfileSet.Defaults().Rename("custom", "  Workshop  ");
        Assert.Equal("Workshop", s.Profiles[1].Name);

        var d = s.Duplicate("custom", " Copy ");
        Assert.Equal("Copy", d.Profiles[^1].Name);
    }

    [Fact]
    public void A_profile_can_change_the_case_of_its_own_name()
    {
        var s = ProfileSet.Defaults().Rename("custom", "CUSTOM");
        Assert.Equal("CUSTOM", s.Profiles[1].Name);
    }

    [Fact]
    public void Rename_keeps_the_profile_in_place_and_selected()
    {
        var s = ProfileSet.Defaults().Rename("dreamair", "Headset");
        Assert.Equal(["dreamair", "custom"], s.Profiles.Select(p => p.Id));
        Assert.Equal("Headset", s.Selected.Name);
    }

    [Fact]
    public void Add_appends_a_profile()
    {
        var p = new Profile(Profile.NewId(), "Lab", null, BuildSource.Mirador, [], new Dictionary<string, string>());
        var s = ProfileSet.Defaults().Add(p);
        Assert.Equal(3, s.Profiles.Count);
        Assert.Equal(p, s.Profiles[^1]);
        Assert.Equal("dreamair", s.SelectedId);
    }

    [Fact]
    public void Add_refuses_an_id_that_is_already_used()
    {
        var s = ProfileSet.Defaults();
        Assert.Throws<ArgumentException>(() => s.Add(s.Profiles[1] with { Name = "Other" }));
    }

    [Fact]
    public void Replace_swaps_the_profile_with_the_same_id_in_place()
    {
        var s = ProfileSet.Defaults();
        var changed = s.Profiles[0] with { Monado = new BuildSource(SourceKind.Folder, @"D:\monado") };
        var r = s.Replace(changed);
        Assert.Equal(["dreamair", "custom"], r.Profiles.Select(p => p.Id));
        Assert.Equal(changed, r.Profiles[0]);
        Assert.Equal(s.Profiles[1], r.Profiles[1]);
    }

    [Fact]
    public void Replace_checks_the_name_too()
    {
        var s = ProfileSet.Defaults();
        Assert.Throws<MiradorException>(() => s.Replace(s.Profiles[1] with { Name = "Dream Air" }));
    }

    [Fact]
    public void Select_changes_the_selected_profile()
    {
        var s = ProfileSet.Defaults().Select("custom");
        Assert.Equal("custom", s.SelectedId);
        Assert.Equal("Custom", s.Selected.Name);
    }

    [Fact]
    public void An_unknown_profile_is_reported_in_plain_words()
    {
        var s = ProfileSet.Defaults();
        Assert.Equal("That profile no longer exists.", Assert.Throws<MiradorException>(() => s.Select("nope")).Message);
        Assert.Throws<MiradorException>(() => s.Duplicate("nope", "X"));
        Assert.Throws<MiradorException>(() => s.Rename("nope", "X"));
        Assert.Throws<MiradorException>(() => s.Delete("nope"));
        Assert.Throws<MiradorException>(() => s.Replace(s.Profiles[0] with { Id = "nope" }));
    }

    [Fact]
    public void Deleting_the_last_profile_is_refused()
    {
        var s = ProfileSet.Defaults().Delete("custom");
        var e = Assert.Throws<MiradorException>(() => s.Delete("dreamair"));
        Assert.Equal("Mirador needs at least one profile.", e.Message);
    }

    [Fact]
    public void Deleting_the_selected_profile_selects_the_first()
    {
        var s = ProfileSet.Defaults().Duplicate("dreamair", "Dev");
        var devId = s.Profiles[^1].Id;
        s = s.Select(devId).Delete(devId);
        Assert.Equal("dreamair", s.SelectedId);

        s = ProfileSet.Defaults().Delete("dreamair");
        Assert.Equal(["custom"], s.Profiles.Select(p => p.Id));
        Assert.Equal("custom", s.SelectedId);
    }

    [Fact]
    public void Deleting_another_profile_keeps_the_selection()
    {
        var s = ProfileSet.Defaults().Select("custom").Delete("dreamair");
        Assert.Equal("custom", s.SelectedId);
    }
}
