namespace Mirador.Helper.Tests;

public class PimaxEyeBlobTests
{
    [Fact]
    public void Blob_variant_from_pimax_command_line() => Assert.Equal("K11_RAWH", PimaxEyeBlob.Parse(
        "\"C:\\Program Files\\Pimax\\Runtime\\EyeTrackingServer\\platform_runtime\\platform_runtime_XR5EYECHIP_WIN10_x64.exe\" -x \"C:\\Program Files\\Pimax\\Runtime\\EyeTrackingServer\\platform_runtime\\config\\config_XR5PIMAXDA_K11_RAWH.bin\" \"C:\\log\""));

    [Theory]
    [InlineData("K10_RAWH")]
    [InlineData("K10_RAYH")]
    [InlineData("K11_RAWH")]
    [InlineData("K11_RAYH")]
    public void All_four_known_variants_are_returned(string variant) =>
        Assert.Equal(variant, PimaxEyeBlob.Parse($"runtime.exe -x config_XR5PIMAXDA_{variant}.bin log"));

    [Theory]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_K12_RAWH.bin log")]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_K11_RAWX.bin log")]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_k11_rawh.bin log")]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_K11_RAWH log")]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_.bin log")]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_K11_RAWH_EXTRA.bin log")]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_..\\..\\evil.bin log")]
    [InlineData("runtime.exe -x config_XR5PIMAXDA_K11_RAWH;calc.bin log")]
    [InlineData("garbage")]
    [InlineData("")]
    public void Unknown_blob_variants_are_not_returned(string commandLine) =>
        Assert.Null(PimaxEyeBlob.Parse(commandLine));

    [Fact]
    public void The_first_known_variant_wins_when_the_line_names_several() =>
        Assert.Equal("K10_RAYH", PimaxEyeBlob.Parse(
            "runtime.exe config_XR5PIMAXDA_K12_RAWH.bin config_XR5PIMAXDA_K10_RAYH.bin config_XR5PIMAXDA_K11_RAWH.bin"));
}
