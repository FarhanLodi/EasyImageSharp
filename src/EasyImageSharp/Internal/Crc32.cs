using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyImageSharp;

/// <summary>
/// CRC-32 (ISO 3309 / PNG). Eight bytes are folded per iteration using the "slicing by eight" table set:
/// the classic byte table is extended with seven more, each the previous one advanced by another byte
/// position, so a whole 64-bit word contributes with eight table reads and no per-bit work.
/// </summary>
internal static class Crc32
{
    private const int Slices = 8;

    /// <summary>Slice <c>s</c> occupies <c>Table[s * 256 .. s * 256 + 256)</c>.</summary>
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[Slices * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        for (int slice = 1; slice < Slices; slice++)
        {
            for (int n = 0; n < 256; n++)
            {
                uint previous = table[((slice - 1) * 256) + n];
                table[(slice * 256) + n] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }

    /// <summary>Updates a running CRC with the given data. Seed with 0 and use the final value directly.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = Table;
        uint c = crc ^ 0xFFFFFFFFu;
        int i = 0;

        // The eight-at-a-time path reads 32-bit words, so it is only correct on a little-endian layout.
        if (BitConverter.IsLittleEndian && !SimdConfig.ForceScalarFallback && data.Length >= Slices)
        {
            ref byte source = ref MemoryMarshal.GetReference(data);
            for (; i <= data.Length - Slices; i += Slices)
            {
                uint low = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, (uint)i)) ^ c;
                uint high = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, (uint)(i + 4)));
                c = table[(7 * 256) + (low & 0xFF)]
                    ^ table[(6 * 256) + ((low >> 8) & 0xFF)]
                    ^ table[(5 * 256) + ((low >> 16) & 0xFF)]
                    ^ table[(4 * 256) + (low >> 24)]
                    ^ table[(3 * 256) + (high & 0xFF)]
                    ^ table[(2 * 256) + ((high >> 8) & 0xFF)]
                    ^ table[(1 * 256) + ((high >> 16) & 0xFF)]
                    ^ table[high >> 24];
            }
        }

        for (; i < data.Length; i++)
        {
            c = table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);
}
