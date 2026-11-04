using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class RuntimeManifestTests
{
    [Fact]
    public void An_absolute_library_path_is_returned_as_it_is()
    {
        using var dir = new TempDir();
        var dll = dir.File(Path.Combine("elsewhere", "openxr_monado.dll"));
        File.WriteAllText(dir.File("openxr_monado-dev.json"), TestManifest.Json(dll));

        Assert.Equal(dll, RuntimeManifest.ReadLibraryPath(dir.File("openxr_monado-dev.json")));
    }

    [Theory]
    [InlineData("./openxr_monado.dll")]
    [InlineData("openxr_monado.dll")]
    public void A_relative_library_path_resolves_against_the_manifest_folder(string libraryPath)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("openxr_monado.json"), TestManifest.Json(libraryPath));

        Assert.Equal(dir.File("openxr_monado.dll"), RuntimeManifest.ReadLibraryPath(dir.File("openxr_monado.json")));
    }

    [Fact]
    public void A_relative_library_path_may_go_up_a_folder()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("runtime"));
        File.WriteAllText(dir.File(@"runtime\openxr_monado.json"), TestManifest.Json(@"..\lib\openxr_monado.dll"));

        Assert.Equal(dir.File(@"lib\openxr_monado.dll"), RuntimeManifest.ReadLibraryPath(dir.File(@"runtime\openxr_monado.json")));
    }

    [Fact]
    public void Reading_a_manifest_that_is_not_usable_throws_the_words_validate_gives()
    {
        using var dir = new TempDir();
        var manifest = dir.File("openxr_monado.json");
        File.WriteAllText(manifest, "{ nope");

        var ex = Assert.Throws<MiradorException>(() => RuntimeManifest.ReadLibraryPath(manifest));

        Assert.Equal(RuntimeManifest.Validate(manifest), ex.Message);
    }

    [Fact]
    public void Validate_accepts_a_manifest_whose_library_exists()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("openxr_monado.dll"), "");
        File.WriteAllText(dir.File("openxr_monado.json"), TestManifest.Json("./openxr_monado.dll"));

        Assert.Null(RuntimeManifest.Validate(dir.File("openxr_monado.json")));
    }

    [Fact]
    public void Validate_reports_bad_json_and_missing_library()
    {
        using var dir = new TempDir();
        var manifest = dir.File("openxr_monado.json");

        File.WriteAllText(manifest, "{ this is not json");
        var notJson = RuntimeManifest.Validate(manifest);
        Assert.NotNull(notJson);
        Assert.Contains(manifest, notJson);
        Assert.Contains("JSON", notJson);

        File.WriteAllText(manifest, TestManifest.Json("./openxr_monado.dll"));
        var missingDll = RuntimeManifest.Validate(manifest);
        Assert.NotNull(missingDll);
        Assert.Contains(manifest, missingDll);
        Assert.Contains(dir.File("openxr_monado.dll"), missingDll);
    }

    [Theory]
    [InlineData("""{"file_format_version": "1.0.0"}""")]
    [InlineData("""{"runtime": {"name": "Monado"}}""")]
    [InlineData("""{"runtime": {"library_path": ""}}""")]
    [InlineData("""{"runtime": {"library_path": "  "}}""")]
    [InlineData("""{"runtime": {"library_path": 7}}""")]
    [InlineData("""{"runtime": "Monado"}""")]
    [InlineData("""["runtime"]""")]
    [InlineData("null")]
    public void Validate_reports_a_manifest_that_does_not_say_where_the_library_is(string content)
    {
        using var dir = new TempDir();
        var manifest = dir.File("openxr_monado.json");
        File.WriteAllText(manifest, content);

        var problem = RuntimeManifest.Validate(manifest);

        Assert.NotNull(problem);
        Assert.Contains(manifest, problem);
        Assert.Contains("library_path", problem);
    }

    [Fact]
    public void A_library_path_that_is_not_a_valid_path_is_a_problem_not_an_exception()
    {
        using var dir = new TempDir();
        var manifest = dir.File("openxr_monado.json");
        File.WriteAllText(manifest, TestManifest.Json("a\0b"));

        var problem = RuntimeManifest.Validate(manifest);

        Assert.NotNull(problem);
        Assert.Contains(manifest, problem);
        Assert.Contains("library_path", problem);
        var ex = Assert.Throws<MiradorException>(() => RuntimeManifest.ReadLibraryPath(manifest));
        Assert.Equal(problem, ex.Message);
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_is_a_problem_not_an_exception()
    {
        using var dir = new TempDir();
        var manifest = dir.File("openxr_monado.json");
        File.WriteAllText(manifest, TestManifest.Json("./openxr_monado.dll"));
        using var lock_ = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None);

        var problem = RuntimeManifest.Validate(manifest);

        Assert.NotNull(problem);
        Assert.Contains(manifest, problem);
        Assert.Contains("can't be read", problem);
        var ex = Assert.Throws<MiradorException>(() => RuntimeManifest.ReadLibraryPath(manifest));
        Assert.Equal(problem, ex.Message);
    }

    [Fact]
    public void Validate_reports_a_missing_manifest()
    {
        using var dir = new TempDir();
        var manifest = dir.File("openxr_monado.json");

        var problem = RuntimeManifest.Validate(manifest);

        Assert.NotNull(problem);
        Assert.Contains(manifest, problem);
        Assert.Throws<MiradorException>(() => RuntimeManifest.ReadLibraryPath(manifest));
    }
}
