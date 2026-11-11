using System.Buffers.Binary;
using System.Text;

namespace Mirador.Core.Tests.Build;

/// <summary>Writes the smallest PE32+ file with an import table naming <c>imports</c>: enough for <see cref="Core.Build.PeImports"/>.</summary>
internal static class TestPe
{
    private const int SectionRva = 0x1000;
    private const int SectionFileOffset = 0x200;

    public static byte[] Build(params string[] imports)
    {
        // The section: the import descriptors (one per DLL and the all-zero end), then the names.
        var descriptors = (imports.Length + 1) * 20;
        var names = new List<byte>();
        var nameRvas = new List<int>();
        foreach (var dll in imports)
        {
            nameRvas.Add(SectionRva + descriptors + names.Count);
            names.AddRange(Encoding.ASCII.GetBytes(dll));
            names.Add(0);
        }

        var section = new byte[Align(descriptors + names.Count, 0x200)];
        for (var i = 0; i < imports.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(i * 20 + 12), nameRvas[i]);
        }

        names.ToArray().CopyTo(section, descriptors);

        var pe = new byte[SectionFileOffset + section.Length];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        const int peAt = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), peAt);
        "PE\0\0"u8.CopyTo(pe.AsSpan(peAt));
        var coff = peAt + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 2), 1);
        const int optionalSize = 112 + 16 * 8;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(coff + 16), optionalSize);
        var optional = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(optional), 0x20B);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(optional + 108), 16);
        if (imports.Length > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(optional + 112 + 8), SectionRva);
            BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(optional + 112 + 12), descriptors);
        }

        var header = optional + optionalSize;
        ".idata\0\0"u8.CopyTo(pe.AsSpan(header));
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(header + 8), section.Length);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(header + 12), SectionRva);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(header + 16), section.Length);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(header + 20), SectionFileOffset);
        section.CopyTo(pe, SectionFileOffset);
        return pe;
    }

    private static int Align(int n, int a) => (n + a - 1) / a * a;
}
