namespace Mirador.Core.Tests;

public class TrayClickTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(2);

    [Fact]
    public void Hides_when_in_front()
    {
        Assert.True(TrayClick.ShouldHide(visible: true, minimized: false, foreground: true, Long));
    }

    [Fact]
    public void Hides_when_deactivated_by_the_click()
    {
        // The click on the tray icon took the focus first, so the window is no longer in front.
        Assert.True(TrayClick.ShouldHide(visible: true, minimized: false, foreground: false, TimeSpan.FromSeconds(0.2)));
    }

    [Fact]
    public void Shows_when_deactivated_long_ago()
    {
        Assert.False(TrayClick.ShouldHide(visible: true, minimized: false, foreground: false, Long));
    }

    [Fact]
    public void Shows_when_minimized()
    {
        Assert.False(TrayClick.ShouldHide(visible: true, minimized: true, foreground: true, TimeSpan.Zero));
    }

    [Fact]
    public void Shows_when_hidden()
    {
        Assert.False(TrayClick.ShouldHide(visible: false, minimized: false, foreground: true, TimeSpan.Zero));
    }
}
