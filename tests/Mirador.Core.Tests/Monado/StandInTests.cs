using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class StandInTests
{
    [Fact]
    public void A_program_that_says_something_else_is_not_the_stand_in()
    {
        Assert.False(StandIn.IsStandIn(Environment.ProcessPath!)); // the test host
        Assert.False(StandIn.IsStandIn(Path.Combine(Environment.SystemDirectory, "ping.exe")));
    }

    [Fact]
    public void A_file_that_is_missing_or_no_program_is_not_the_stand_in()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("monado-service.exe"), "not a program");

        Assert.False(StandIn.IsStandIn(dir.File("missing.exe")));
        Assert.False(StandIn.IsStandIn(dir.File("monado-service.exe")));
        Assert.False(StandIn.IsStandIn(""));
    }
}
