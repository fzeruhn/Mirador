namespace Mirador.Core.Tests;

public class SingleInstanceTests
{
    private static string UniqueName() => @"Local\Mirador-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_second_mirador_finds_the_first()
    {
        var name = UniqueName();
        using var first = SingleInstance.TryAcquire(name);

        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));
    }

    [Fact]
    public void The_first_letting_go_frees_the_name()
    {
        var name = UniqueName();
        SingleInstance.TryAcquire(name)!.Dispose();

        using var again = SingleInstance.TryAcquire(name);

        Assert.NotNull(again);
    }
}
