namespace Mirador.Core.Tests;

public class MiradorExceptionTests
{
    [Fact]
    public void Carries_the_message_for_the_user()
    {
        var e = new MiradorException("The helper service is not running. Reinstall Mirador.");
        Assert.Equal("The helper service is not running. Reinstall Mirador.", e.Message);
    }
}
