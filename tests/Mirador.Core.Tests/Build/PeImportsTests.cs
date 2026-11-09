using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

public class PeImportsTests
{
    [Fact]
    public void Reads_the_imported_dlls_in_order()
    {
        Assert.Equal(["KERNEL32.dll", "vulkan-1.dll", "SDL2.dll"], PeImports.Parse(TestPe.Build("KERNEL32.dll", "vulkan-1.dll", "SDL2.dll")));
    }

    [Fact]
    public void A_file_without_imports_has_none()
    {
        Assert.Empty(PeImports.Parse(TestPe.Build()));
    }

    [Fact]
    public void A_real_dll_is_read_too()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Windows' own kernel32.dll imports ntdll.dll.
        Assert.Contains("ntdll.dll", PeImports.Read(Path.Combine(Environment.SystemDirectory, "kernel32.dll")), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x3C)]
    public void A_file_that_is_no_pe_is_refused(int length)
    {
        Assert.Throws<InvalidDataException>(() => PeImports.Parse(new byte[length]));
    }

    [Fact]
    public void A_cut_off_file_is_refused_not_overrun()
    {
        var pe = TestPe.Build("a.dll", "b.dll");
        for (var cut = 0x40; cut < pe.Length; cut += 7)
        {
            var exception = Record.Exception(() => PeImports.Parse(pe.AsSpan(0, cut)));
            Assert.True(exception is null or InvalidDataException, $"cut at {cut}: {exception}");
        }
    }

    [Fact]
    public void An_import_address_outside_every_section_is_refused()
    {
        var pe = TestPe.Build("a.dll");
        // The first descriptor's name RVA, far beyond the one section.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x200 + 12), 0x7FFF0000);
        Assert.Throws<InvalidDataException>(() => PeImports.Parse(pe));
    }
}
