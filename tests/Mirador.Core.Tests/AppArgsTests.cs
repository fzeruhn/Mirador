namespace Mirador.Core.Tests;

public class AppArgsTests
{
    [Fact]
    public void No_arguments()
    {
        Assert.Equal(new AppArgs(false, false, null), AppArgs.Parse([]));
    }

    [Fact]
    public void Start_and_background()
    {
        Assert.Equal(new AppArgs(true, true, null), AppArgs.Parse(["--background", "--start"]));
    }

    [Fact]
    public void Set_runtime_takes_the_next_argument_as_it_is()
    {
        Assert.Equal(
            new AppArgs(false, false, @"C:\Users\Zoë\My Builds\monado\build\openxr_monado-dev.json"),
            AppArgs.Parse(["--set-runtime", @"C:\Users\Zoë\My Builds\monado\build\openxr_monado-dev.json"]));
    }

    [Fact]
    public void Set_runtime_without_a_manifest_is_empty_not_missing()
    {
        Assert.Equal(new AppArgs(false, false, ""), AppArgs.Parse(["--set-runtime"]));
    }

    [Fact]
    public void A_manifest_that_looks_like_a_flag_is_still_the_manifest()
    {
        Assert.Equal("--start", AppArgs.Parse(["--set-runtime", "--start"]).SetRuntime);
        Assert.False(AppArgs.Parse(["--set-runtime", "--start"]).Start);
    }

    [Fact]
    public void Case_doesnt_matter_and_unknown_arguments_are_ignored()
    {
        Assert.Equal(new AppArgs(true, true, null), AppArgs.Parse(["--START", "-Embedding", "--Background", "x"]));
    }
}
