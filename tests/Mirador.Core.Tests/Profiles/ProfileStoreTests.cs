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
        Assert.Equal(["dreamair", null, null], loaded.Profiles.Select(p => p.ModuleId));

        var first = loaded.Profiles[0];
        Assert.Equal(SourceKind.Folder, first.Monado.Kind);
        Assert.Equal(@"D:\Monado Build\bin", first.Monado.Folder);
        Assert.Equal([new EnvVar("XRT_DEBUG_GUI", "1"), new EnvVar("XRT_LOG", "debug")], first.ExtraEnvironment);
        Assert.Equal(@"D:\layer folder", first.ModuleOptions["layerFolder"]);
        Assert.Single(first.ModuleOptions);

        Assert.Equal(BuildSource.Bundled, loaded.Profiles[1].Monado);
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
    [InlineData("""{"profiles":[{"id":"a","name":"A","monado":{"kind":"Bundled"}},{"id":"a","name":"B","monado":{"kind":"Bundled"}}],"selectedId":"a"}""")]
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
    public void A_selected_id_that_does_not_exist_selects_the_first_profile()
    {
        using var dir = new TempDir();
        var path = dir.File("profiles.json");
        File.WriteAllText(path, """
            {"profiles":[{"id":"a","name":"A","monado":{"kind":"Bundled"}},{"id":"b","name":"B","monado":{"kind":"Bundled"}}],"selectedId":"gone"}
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

    private static void AssertAreTheDefaults(ProfileSet loaded)
    {
        var defaults = ProfileSet.Defaults();
        Assert.Equal(defaults.Profiles.Select(p => p.Id), loaded.Profiles.Select(p => p.Id));
        Assert.Equal(defaults.Profiles.Select(p => p.Name), loaded.Profiles.Select(p => p.Name));
        Assert.Equal(defaults.Profiles.Select(p => p.ModuleId), loaded.Profiles.Select(p => p.ModuleId));
        Assert.Equal(defaults.SelectedId, loaded.SelectedId);
        Assert.All(loaded.Profiles, p => Assert.Equal(BuildSource.Bundled, p.Monado));
    }
}
