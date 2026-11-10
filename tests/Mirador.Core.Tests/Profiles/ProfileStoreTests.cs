using System.Text.Json;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

public class ProfileStoreTests
{
    [Fact]
    public void Store_round_trips_everything()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.File("profiles.json"));
        var set = ProfileSet.Defaults()
            .Replace(ProfileSet.Defaults().Profiles[0] with
            {
                Monado = new BuildSource(SourceKind.Folder, @"D:\Monado Build\bin"),
                ExtraEnvironment = [new EnvVar("XRT_DEBUG_GUI", "1"), new EnvVar("XRT_LOG", "debug")],
                ModuleOptions = new Dictionary<string, string> { ["layerFolder"] = @"D:\layer folder" },
            })
            .Duplicate("custom", "Workshop")
            .Select("custom");

        store.Save(set);
        var loaded = store.Load();

        Assert.Equal("custom", loaded.SelectedId);
        Assert.Equal(set.Profiles.Select(p => p.Id), loaded.Profiles.Select(p => p.Id));
        Assert.Equal(["Dream Air", "Custom", "Workshop"], loaded.Profiles.Select(p => p.Name));
        Assert.Equal(["pimax", null, null], loaded.Profiles.Select(p => p.ModuleId));

        var first = loaded.Profiles[0];
        Assert.Equal(SourceKind.Folder, first.Monado.Kind);
        Assert.Equal(@"D:\Monado Build\bin", first.Monado.Folder);
        Assert.Equal([new EnvVar("XRT_DEBUG_GUI", "1"), new EnvVar("XRT_LOG", "debug")], first.ExtraEnvironment);
        Assert.Equal(@"D:\layer folder", first.ModuleOptions["layerFolder"]);
        Assert.Single(first.ModuleOptions);

        Assert.Equal(BuildSource.Mirador, loaded.Profiles[1].Monado);
        Assert.Empty(loaded.Profiles[1].ExtraEnvironment);
        Assert.Empty(loaded.Profiles[1].ModuleOptions);
    }

    [Fact]
    public void Missing_file_loads_defaults()
    {
        using var dir = new TempDir();
        var loaded = new ProfileStore(dir.File("profiles.json")).Load();
        AssertAreTheDefaults(loaded);
        Assert.False(File.Exists(dir.File("profiles.json")));
    }

    [Fact]
    public void Corrupt_file_is_kept_as_bad_and_defaults_load()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        File.WriteAllText(path, "{not json");

        var loaded = new ProfileStore(path).Load();

        AssertAreTheDefaults(loaded);
        Assert.False(File.Exists(path));
        Assert.Equal("{not json", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void A_second_corrupt_file_replaces_the_older_bad_copy()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        File.WriteAllText(path + ".bad", "older");
        File.WriteAllText(path, "newer {");

        new ProfileStore(path).Load();

        Assert.Equal("newer {", File.ReadAllText(path + ".bad"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"profiles":[],"selectedId":"x"}""")]
    [InlineData("""{"profiles":[null],"selectedId":"x"}""")]
    [InlineData("""{"profiles":[{"id":"a","name":"A"}],"selectedId":"a"}""")]
    [InlineData("""{"profiles":[{"id":"a","name":"A","monado":{"kind":"Sideways"}}],"selectedId":"a"}""")]
    public void A_file_that_is_not_a_usable_profile_list_is_kept_as_bad(string text)
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        File.WriteAllText(path, text);

        AssertAreTheDefaults(new ProfileStore(path).Load());

        Assert.False(File.Exists(path));
        Assert.Equal(text, File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void One_unusable_profile_is_left_out_and_the_others_kept_with_a_copy_of_the_file_as_bad()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        const string text = """
            {"profiles":[
              {"id":"a","name":"Mine","monado":{"kind":"Folder","folder":"D:\\monado\\build"}},
              {"id":"b","name":"","monado":{"kind":"Mirador"}},
              {"id":"c","name":"Other","monado":{"kind":"Mirador"}},
              {"id":"a","name":"Same id","monado":{"kind":"Mirador"}}
            ],"selectedId":"c"}
            """;
        File.WriteAllText(path, text);

        var set = new ProfileStore(path).Load();

        Assert.Equal(["a", "c"], set.Profiles.Select(p => p.Id));
        Assert.Equal(@"D:\monado\build", set.Profiles[0].Monado.Folder);
        Assert.Equal("c", set.SelectedId);
        Assert.Equal(text, File.ReadAllText(path)); // still there until the next save
        Assert.Equal(text, File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void Variables_without_a_name_and_options_without_a_value_are_left_out()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        File.WriteAllText(path, """
            {"profiles":[{"id":"a","name":"A","monado":{"kind":"Mirador"},
              "extraEnvironment":[null,{"value":"1"},{"name":"KEPT","value":"2"},{"name":"NO_VALUE"}],
              "moduleOptions":{"layerFolder":null,"other":"x"}}],"selectedId":"a"}
            """);

        var profile = Assert.Single(new ProfileStore(path).Load().Profiles);

        Assert.Equal([new EnvVar("KEPT", "2"), new EnvVar("NO_VALUE", "")], profile.ExtraEnvironment);
        Assert.Equal(new Dictionary<string, string> { ["other"] = "x" }, profile.ModuleOptions);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void A_file_with_nothing_left_out_leaves_no_bad_copy()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        new ProfileStore(path).Save(ProfileSet.Defaults());

        new ProfileStore(path).Load();

        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public void A_selected_id_that_does_not_exist_selects_the_first_profile()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        File.WriteAllText(path, """
            {"profiles":[{"id":"a","name":"A","monado":{"kind":"Mirador"}},{"id":"b","name":"B","monado":{"kind":"Mirador"}}],"selectedId":"gone"}
            """);

        var loaded = new ProfileStore(path).Load();

        Assert.Equal(["a", "b"], loaded.Profiles.Select(p => p.Id));
        Assert.Equal("a", loaded.SelectedId);
        Assert.Empty(loaded.Profiles[0].ExtraEnvironment);
        Assert.Empty(loaded.Profiles[0].ModuleOptions);
        Assert.Null(loaded.Profiles[0].ModuleId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_creates_the_folder_replaces_the_file_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "not", "yet", "profiles.json");
        var store = new ProfileStore(path);

        store.Save(ProfileSet.Defaults());
        store.Save(ProfileSet.Defaults().Select("custom"));

        Assert.Equal("custom", store.Load().SelectedId);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void The_file_holds_only_the_profiles_and_the_selected_id()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");

        new ProfileStore(path).Save(ProfileSet.Defaults());

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(["profiles", "selectedId"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Names_are_written_as_they_read()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        var defaults = ProfileSet.Defaults();
        var set = defaults with { Profiles = [defaults.Profiles[0] with { Name = "Zoë's builds" }, .. defaults.Profiles.Skip(1)] };

        new ProfileStore(path).Save(set);

        Assert.Contains("\"name\": \"Zoë's builds\"", File.ReadAllText(path));
        Assert.Equal("Zoë's builds", new ProfileStore(path).Load().Profiles[0].Name);
    }

    [Fact]
    public void Profile_without_lighthouse_reads_null()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        File.WriteAllText(path, """
            {"profiles":[{"id":"a","name":"A","monado":{"kind":"Mirador"}}],"selectedId":"a"}
            """);

        Assert.Null(new ProfileStore(path).Load().Profiles[0].Lighthouse);
    }

    [Fact]
    public void Lighthouse_round_trips()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        var defaults = ProfileSet.Defaults();
        var set = defaults with { Profiles = [defaults.Profiles[0] with { Lighthouse = false }, defaults.Profiles[1] with { Lighthouse = true }] };

        new ProfileStore(path).Save(set);
        var loaded = new ProfileStore(path).Load();

        Assert.False(loaded.Profiles[0].Lighthouse);
        Assert.True(loaded.Profiles[1].Lighthouse);
    }

    private static void AssertAreTheDefaults(ProfileSet loaded)
    {
        var defaults = ProfileSet.Defaults();
        Assert.Equal(defaults.Profiles.Select(p => p.Id), loaded.Profiles.Select(p => p.Id));
        Assert.Equal(defaults.Profiles.Select(p => p.Name), loaded.Profiles.Select(p => p.Name));
        Assert.Equal(defaults.Profiles.Select(p => p.ModuleId), loaded.Profiles.Select(p => p.ModuleId));
        Assert.Equal(defaults.SelectedId, loaded.SelectedId);
        Assert.All(loaded.Profiles, p => Assert.Equal(BuildSource.Mirador, p.Monado));
    }
}
