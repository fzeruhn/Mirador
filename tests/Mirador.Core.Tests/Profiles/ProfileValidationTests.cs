using Mirador.Core.Monado;
using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

public class ProfileValidationTests
{
    private static readonly MonadoLocator Locator = new(Path.Combine(Path.GetTempPath(), "no-mirador-here"));

    private static Profile Custom(params EnvVar[] extra) =>
        new("p1", "Custom", null, BuildSource.Bundled, extra, new Dictionary<string, string>());

    private static IReadOnlyList<string> Problems(Profile p, string? moduleProblem = null) =>
        ProfileValidation.Problems(p, Locator, _ => moduleProblem);

    [Fact]
    public void A_good_profile_has_none()
    {
        Assert.Empty(Problems(Custom(new EnvVar("XRT_LOG", "debug"), new EnvVar("EMPTY_REMOVES", ""))));
    }

    [Fact]
    public void A_bundled_build_isnt_looked_for_here()
    {
        // Mirador's own Monado is checked when it starts; a dev build has none.
        Assert.Empty(Problems(Custom()));
    }

    [Fact]
    public void A_folder_that_isnt_there_says_so()
    {
        using var dir = new TempDir();
        var gone = Path.Combine(dir.Path, "monado", "build");

        var problems = Problems(Custom() with { Monado = new BuildSource(SourceKind.Folder, gone) });

        Assert.Equal([$"The Monado build folder {gone} doesn't exist. Pick another folder in the profile."], problems);
    }

    [Fact]
    public void A_folder_build_without_a_folder_says_so()
    {
        Assert.Equal(
            ["No Monado build folder is set. Pick a Monado build folder in the profile."],
            Problems(Custom() with { Monado = new BuildSource(SourceKind.Folder) }));
    }

    [Fact]
    public void A_name_with_an_equals_sign()
    {
        Assert.Equal(["A variable's name can't contain \"=\": A=B."], Problems(Custom(new EnvVar("A=B", "1"))));
    }

    [Fact]
    public void An_empty_name()
    {
        Assert.Equal(["Every extra variable needs a name."], Problems(Custom(new EnvVar("  ", "1"))));
    }

    [Fact]
    public void Duplicate_names_ignoring_case()
    {
        Assert.Equal(["xrt_log is set twice. Keep one."], Problems(Custom(new EnvVar("XRT_LOG", "debug"), new EnvVar("xrt_log", "info"))));
    }

    [Fact]
    public void A_module_problem_is_passed_through()
    {
        Assert.Equal(
            ["The Dream Air layer folder D:\\layer doesn't exist."],
            Problems(Custom() with { ModuleId = "dreamair" }, "The Dream Air layer folder D:\\layer doesn't exist."));
    }

    [Fact]
    public void A_profile_needs_a_name()
    {
        Assert.Equal(["A profile needs a name."], Problems(Custom() with { Name = " " }));
    }
}
