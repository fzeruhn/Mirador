using System.Buffers.Binary;
using System.Text;

namespace Mirador.Core.Build;

/// <summary>
/// Reads which DLLs a Windows program or DLL imports, from its PE import table, without loading it, so that
/// every DLL each needs is found. Delay-loaded imports are left out: a missing one fails only when it is used.
/// </summary>
public static class PeImports
{
    private const int ImportDirectory = 1;
    private const int MaxImports = 4096;

    /// <summary>The names of the DLLs <paramref name="path"/> imports, as written in it.</summary>
    /// <exception cref="InvalidDataException">The file is not a PE file, or its import table is broken.</exception>
    public static IReadOnlyList<string> Read(string path) => Parse(File.ReadAllBytes(path));

    /// <inheritdoc cref="Read"/>
    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> pe)
    {
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z')
        {
            throw new InvalidDataException("Not a Windows program or DLL (no MZ header).");
        }

        var peOffset = Int32At(pe, 0x3C);
        if (peOffset < 0 || peOffset > pe.Length - 24 || !pe.Slice(peOffset, 4).SequenceEqual("PE\0\0"u8))
        {
            throw new InvalidDataException("Not a Windows program or DLL (no PE header).");
        }

        var coff = peOffset + 4;
        var sectionCount = UInt16At(pe, coff + 2);
        var optionalSize = UInt16At(pe, coff + 16);
        var optional = coff + 20;
        if (optional + optionalSize > pe.Length || optionalSize < 2)
        {
            throw new InvalidDataException("The PE optional header is cut off.");
        }

        var magic = UInt16At(pe, optional);
        var directories = magic switch
        {
            0x20B => optional + 112,
            0x10B => optional + 96,
            _ => throw new InvalidDataException($"Unknown PE optional header (magic 0x{magic:X})."),
        };
        if (directories + (ImportDirectory + 1) * 8 > optional + optionalSize)
        {
            return [];
        }

        var directoryCount = Int32At(pe, directories - 4);
        if (directoryCount <= ImportDirectory)
        {
            return [];
        }

        var importRva = UInt32At(pe, directories + ImportDirectory * 8);
        if (importRva == 0)
        {
            return [];
        }

        var sections = optional + optionalSize;
        if (sections + sectionCount * 40 > pe.Length)
        {
            throw new InvalidDataException("The PE section table is cut off.");
        }

        var names = new List<string>();
        var descriptor = Offset(pe, sections, sectionCount, importRva);
        for (var i = 0; i < MaxImports; i++, descriptor += 20)
        {
            if (descriptor + 20 > pe.Length)
            {
                throw new InvalidDataException("The import table is cut off.");
            }

            var entry = pe.Slice(descriptor, 20);
            if (entry.IndexOfAnyExcept((byte)0) < 0)
            {
                return names;
            }

            var nameAt = Offset(pe, sections, sectionCount, BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
            var length = pe[nameAt..].IndexOf((byte)0);
            if (length <= 0 || length > 260)
            {
                throw new InvalidDataException("An imported DLL's name is broken.");
            }

            names.Add(Encoding.ASCII.GetString(pe.Slice(nameAt, length)));
        }

        throw new InvalidDataException("The import table doesn't end.");
    }

    /// <summary>The file offset of <paramref name="rva"/>, from the section that holds it.</summary>
    private static int Offset(ReadOnlySpan<byte> pe, int sections, int count, uint rva)
    {
        for (var i = 0; i < count; i++)
        {
            var s = sections + i * 40;
            var virtualSize = UInt32At(pe, s + 8);
            var virtualAddress = UInt32At(pe, s + 12);
            var rawSize = UInt32At(pe, s + 16);
            var rawPointer = UInt32At(pe, s + 20);
            var size = Math.Max(virtualSize, rawSize);
            if (rva >= virtualAddress && rva - virtualAddress < size)
            {
                var offset = (long)rawPointer + (rva - virtualAddress);
                if (offset >= pe.Length)
                {
                    break;
                }

                return (int)offset;
            }
        }

        throw new InvalidDataException($"An address in the import table (0x{rva:X}) is in no section.");
    }

    private static int Int32At(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b[at..]);

    private static uint UInt32At(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    private static ushort UInt16At(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
}
