using Mirador.Core.Runtime;

namespace Mirador.Core.Tests.Runtime;

public class OpenXrRuntimeTests
{
    private sealed class FakeReader(string? value) : IRuntimeRegistryReader
    {
        public string? ActiveRuntime() => value;
    }

    private static OpenXrRuntime With(string? active) => new(new FakeReader(active));

    [Fact]
    public void Active_is_what_the_registry_says()
    {
        Assert.Equal(@"C:\Monado\openxr_monado.json", With(@"C:\Monado\openxr_monado.json").Active());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Active_is_null_when_no_runtime_is_set(string? value)
    {
        var runtime = With(value);

        Assert.Null(runtime.Active());
        Assert.False(runtime.IsActive(@"C:\Monado\openxr_monado.json"));
    }

    [Fact]
    public void The_same_path_is_active()
    {
        Assert.True(With(@"C:\Monado\openxr_monado.json").IsActive(@"C:\Monado\openxr_monado.json"));
    }

    [Fact]
    public void Case_makes_no_difference()
    {
        Assert.True(With(@"c:\MONADO\OpenXR_Monado.JSON").IsActive(@"C:\monado\openxr_monado.json"));
    }

    [Fact]
    public void Forward_and_back_slashes_are_the_same()
    {
        Assert.True(With(@"C:/Monado/runtime/openxr_monado.json").IsActive(@"C:\Monado\runtime\openxr_monado.json"));
        Assert.True(With(@"C:\Monado\runtime\openxr_monado.json").IsActive("C:/Monado/runtime/openxr_monado.json"));
    }

    [Fact]
    public void Surrounding_quotes_and_spaces_are_ignored()
    {
        Assert.True(With("  \"C:\\Monado\\openxr_monado.json\"  ").IsActive(@"C:\Monado\openxr_monado.json"));
        Assert.True(With(@"C:\Monado\openxr_monado.json").IsActive("\" C:\\Monado\\openxr_monado.json \""));
    }

    [Fact]
    public void Dot_segments_and_doubled_separators_are_resolved()
    {
        Assert.True(With(@"C:\Monado\build\..\runtime\\openxr_monado.json").IsActive(@"C:\Monado\.\runtime\openxr_monado.json"));
    }

    [Fact]
    public void Another_runtime_is_not_active()
    {
        var runtime = With(@"C:\Program Files\SteamVR\steamxr_win64.json");

        Assert.False(runtime.IsActive(@"C:\Monado\openxr_monado.json"));
        Assert.Equal(@"C:\Program Files\SteamVR\steamxr_win64.json", runtime.Active());
    }

    [Fact]
    public void A_manifest_in_a_longer_or_shorter_folder_is_not_active()
    {
        Assert.False(With(@"C:\Monado\runtime\openxr_monado.json").IsActive(@"C:\Monado\openxr_monado.json"));
        Assert.False(With(@"C:\Monado\openxr_monado.json").IsActive(@"C:\Monado\openxr_monado.json.bak"));
    }

    [Theory]
    [InlineData("C:\\Monado\\open\0xr_monado.json")]
    [InlineData(@"openxr_monado.json")]
    [InlineData(@"\Monado\openxr_monado.json")]
    [InlineData(@"C:openxr_monado.json")]
    [InlineData("\"\"")]
    public void A_registry_value_that_is_not_a_full_path_is_not_active_and_does_not_throw(string value)
    {
        var runtime = With(value);

        Assert.False(runtime.IsActive(@"C:\Monado\openxr_monado.json"));
        Assert.Equal(value, runtime.Active());
    }

    [Fact]
    public void A_manifest_that_is_not_a_path_is_not_active_and_does_not_throw()
    {
        var runtime = With(@"C:\Monado\openxr_monado.json");

        Assert.False(runtime.IsActive(""));
        Assert.False(runtime.IsActive("C:\\Monado\\open\0xr_monado.json"));
    }

    [Fact]
    public void The_real_reader_reads_the_registry_without_failing()
    {
        // Reads HKLM (never writes). Only that it answers is checked: the value depends on the machine.
        var reader = new RegistryRuntimeReader();

        Assert.Null(Record.Exception(() => reader.ActiveRuntime()));
    }
}
