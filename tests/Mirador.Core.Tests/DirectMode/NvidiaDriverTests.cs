using Mirador.Core.DirectMode;
namespace Mirador.Core.Tests.DirectMode;

public class NvidiaDriverTests
{
    [Theory]
    [InlineData("32.0.16.2101", "621.01")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.3623", "536.23")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData(" 32.0.16.2101 ", "621.01")]
    public void Windows_version_gives_nvidias(string windows, string nvidia)
    {
        Assert.Equal(nvidia, NvidiaDriver.FromWindowsVersion(windows));
    }

    [Fact]
    public void A_last_part_written_without_its_leading_zero_still_counts_as_four_digits()
    {
        Assert.Equal("600.03", NvidiaDriver.FromWindowsVersion("32.0.16.3"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("32.0.16")]
    [InlineData("32.0.16.2101.1")]
    [InlineData("32.0.16.21011")]
    [InlineData("32.0.x.2101")]
    [InlineData("32.0.-1.2101")]
    [InlineData("32.0..2101")]
    public void Anything_else_gives_null(string windows)
    {
        Assert.Null(NvidiaDriver.FromWindowsVersion(windows));
    }

    [Fact]
    public void This_pcs_nvidia_driver_is_none_or_one_mirador_can_read()
    {
        // Asks Windows (WMI). On a PC without an NVIDIA GPU there is nothing to read.
        var windows = NvidiaDriver.CurrentWindowsVersion();

        if (windows is not null)
        {
            Assert.NotNull(NvidiaDriver.FromWindowsVersion(windows));
        }
    }
}
