using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>Constants shared by the JPEG decoder and encoder.</summary>
internal static class JpegTables
{
    /// <summary>Maps zigzag scan position to natural (row-major) block index.</summary>
    public static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    /// <summary>DCT basis: CosTable[(u * 8) + x] = 0.5 * c(u) * cos((2x + 1) * u * PI / 16).</summary>
    public static readonly float[] CosTable = CreateCosTable();

    private static float[] CreateCosTable()
    {
        var table = new float[64];
        for (int u = 0; u < 8; u++)
        {
            double cu = u == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
            for (int x = 0; x < 8; x++)
            {
                table[(u * 8) + x] = (float)(0.5 * cu * Math.Cos((((2 * x) + 1) * u * Math.PI) / 16.0));
            }
        }

        return table;
    }

    /// <summary>ITU-T T.81 Annex K luminance quantization table (natural order).</summary>
    public static readonly byte[] StdLuminanceQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    };

    /// <summary>ITU-T T.81 Annex K chrominance quantization table (natural order).</summary>
    public static readonly byte[] StdChrominanceQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    /// <summary>Standard DC luminance Huffman spec: code counts for lengths 1-16.</summary>
    public static readonly byte[] StdDcBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };

    public static readonly byte[] StdDcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    /// <summary>Standard AC luminance Huffman spec: code counts for lengths 1-16.</summary>
    public static readonly byte[] StdAcBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D };

    public static readonly byte[] StdAcValues =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
        0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16,
        0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
        0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4,
        0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA,
        0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    };

    // ----- Encoder-side tables (ITU-T T.81 Annex K.3.3, the "typical" chrominance Huffman specifications) -----

    /// <summary>Standard DC chrominance Huffman spec: code counts for lengths 1-16 (T.81 table K.4).</summary>
    public static readonly byte[] StdDcChrominanceBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };

    public static readonly byte[] StdDcChrominanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    /// <summary>Standard AC chrominance Huffman spec: code counts for lengths 1-16 (T.81 table K.6).</summary>
    public static readonly byte[] StdAcChrominanceBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };

    public static readonly byte[] StdAcChrominanceValues =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34,
        0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4,
        0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2,
        0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9,
        0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    };

    /// <summary>
    /// The value every sample of a block takes when only its DC coefficient is non-zero.
    /// </summary>
    /// <remarks>
    /// With a single non-zero coefficient the separable transform collapses: the row pass leaves
    /// <c>CosTable[x] * dc</c> in row 0 and zeros elsewhere, and the column pass then multiplies that by
    /// <c>CosTable[y]</c>, which is the same constant for every x and y. Evaluating it directly gives the
    /// same float as running the full transform, and skips 1024 multiply-adds.
    /// </remarks>
    public static float DcOnlyValue(float dc) => CosTable[0] * (CosTable[0] * dc);

    /// <summary>Performs an in-place 8x8 inverse DCT: block holds coefficients in natural order.</summary>
    /// <remarks>
    /// Both passes accumulate over the same index in the same ascending order as the straightforward triple
    /// loop; only the iteration is turned inside out so the eight outputs of a row share one broadcast
    /// coefficient. Float multiplication is commutative and the additions keep their order, so the vector
    /// and scalar paths produce identical results.
    /// </remarks>
    public static void InverseDct(Span<float> block, Span<float> temp)
    {
        if (SimdConfig.Vector256Enabled)
        {
            InverseDct256(block, temp);
            return;
        }

        if (SimdConfig.Vector128Enabled)
        {
            InverseDct128(block, temp);
            return;
        }

        InverseDctScalar(block, temp);
    }

    private static void InverseDct256(Span<float> block, Span<float> temp)
    {
        ref float t = ref MemoryMarshal.GetArrayDataReference(CosTable);
        ref float b = ref MemoryMarshal.GetReference(block);
        ref float g = ref MemoryMarshal.GetReference(temp);

        // Row pass: g(v, .) = sum over u of F(v, u) * T[u, .].
        for (int v = 0; v < 8; v++)
        {
            int rowOffset = v * 8;
            Vector256<float> sum = Vector256<float>.Zero;
            for (int u = 0; u < 8; u++)
            {
                sum += Vector256.Create(Unsafe.Add(ref b, rowOffset + u)) * Vector256.LoadUnsafe(ref t, (nuint)(u * 8));
            }

            sum.StoreUnsafe(ref g, (nuint)rowOffset);
        }

        // Column pass: f(., y) = sum over v of T[v, y] * g(v, .).
        for (int y = 0; y < 8; y++)
        {
            Vector256<float> sum = Vector256<float>.Zero;
            for (int v = 0; v < 8; v++)
            {
                sum += Vector256.Create(Unsafe.Add(ref t, (v * 8) + y)) * Vector256.LoadUnsafe(ref g, (nuint)(v * 8));
            }

            sum.StoreUnsafe(ref b, (nuint)(y * 8));
        }
    }

    private static void InverseDct128(Span<float> block, Span<float> temp)
    {
        ref float t = ref MemoryMarshal.GetArrayDataReference(CosTable);
        ref float b = ref MemoryMarshal.GetReference(block);
        ref float g = ref MemoryMarshal.GetReference(temp);

        for (int v = 0; v < 8; v++)
        {
            int rowOffset = v * 8;
            Vector128<float> low = Vector128<float>.Zero;
            Vector128<float> high = Vector128<float>.Zero;
            for (int u = 0; u < 8; u++)
            {
                Vector128<float> coefficient = Vector128.Create(Unsafe.Add(ref b, rowOffset + u));
                low += coefficient * Vector128.LoadUnsafe(ref t, (nuint)(u * 8));
                high += coefficient * Vector128.LoadUnsafe(ref t, (nuint)((u * 8) + 4));
            }

            low.StoreUnsafe(ref g, (nuint)rowOffset);
            high.StoreUnsafe(ref g, (nuint)(rowOffset + 4));
        }

        for (int y = 0; y < 8; y++)
        {
            Vector128<float> low = Vector128<float>.Zero;
            Vector128<float> high = Vector128<float>.Zero;
            for (int v = 0; v < 8; v++)
            {
                Vector128<float> basis = Vector128.Create(Unsafe.Add(ref t, (v * 8) + y));
                low += basis * Vector128.LoadUnsafe(ref g, (nuint)(v * 8));
                high += basis * Vector128.LoadUnsafe(ref g, (nuint)((v * 8) + 4));
            }

            low.StoreUnsafe(ref b, (nuint)(y * 8));
            high.StoreUnsafe(ref b, (nuint)((y * 8) + 4));
        }
    }

    private static void InverseDctScalar(Span<float> block, Span<float> temp)
    {
        float[] t = CosTable;

        // Row pass: g(v, x) = sum over u of T[u, x] * F(v, u).
        for (int v = 0; v < 8; v++)
        {
            int rowOffset = v * 8;
            for (int x = 0; x < 8; x++)
            {
                float s = 0;
                for (int u = 0; u < 8; u++)
                {
                    s += t[(u * 8) + x] * block[rowOffset + u];
                }

                temp[rowOffset + x] = s;
            }
        }

        // Column pass: f(x, y) = sum over v of T[v, y] * g(v, x).
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                float s = 0;
                for (int v = 0; v < 8; v++)
                {
                    s += t[(v * 8) + y] * temp[(v * 8) + x];
                }

                block[(y * 8) + x] = s;
            }
        }
    }
}
