using Microsoft.Win32;
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

    // The registry reader is tested through its lookup seam: these never open a real registry key.
    private sealed class FakeRegistry(object? value)
    {
        public List<(RegistryView View, string KeyPath, string ValueName)> Asked { get; } = [];

        public object? Read(RegistryView view, string keyPath, string valueName)
        {
            Asked.Add((view, keyPath, valueName));
            return value;
        }
    }

    [Fact]
    public void The_registry_reader_asks_for_the_64_bit_active_runtime_value()
    {
        var registry = new FakeRegistry(@"C:\Monado\openxr_monado.json");

        var value = new RegistryRuntimeReader(registry.Read).ActiveRuntime();

        Assert.Equal(@"C:\Monado\openxr_monado.json", value);
        Assert.Equal([(RegistryView.Registry64, @"SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime")], registry.Asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(new byte[] { 1, 2 })]
    public void The_registry_reader_gives_null_unless_the_value_is_text(object? stored)
    {
        Assert.Null(new RegistryRuntimeReader(new FakeRegistry(stored).Read).ActiveRuntime());
    }
}
