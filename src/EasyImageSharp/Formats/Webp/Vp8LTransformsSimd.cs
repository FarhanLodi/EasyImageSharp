using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Vector versions of the VP8L (lossless) pixel transforms: add / subtract green, the inverse and forward
/// cross-colour transforms, and the inverse predictor for the modes whose prediction does not depend on the pixel to
/// the left (black, top, top-right, top-left and the two top-row averages). Those modes read only the row above, so
/// a whole run of a tile can be reconstructed with vector loads; the remaining modes are inherently serial along the
/// row and use the scalar code of <see cref="Vp8LDecoder"/>.
/// <para>
/// Each channel is processed exactly as in the scalar code: additions and subtractions modulo 256 are byte-lane
/// arithmetic (no carry or borrow can cross a channel), the cross-colour deltas are evaluated in 32-bit lanes with
/// the same sign extensions and arithmetic shifts, and the floor average is the scalar bit identity. The output is
/// therefore bit-identical.
/// </para>
/// </summary>
internal static class Vp8LTransformsSimd
{
    /// <summary>True when the kernels may run.</summary>
    public static bool IsEnabled => SimdConfig.Vector128Enabled && BitConverter.IsLittleEndian;

    // ----- Green -----

    /// <summary>Inverse subtract-green: adds green to red and blue, modulo 256.</summary>
    public static void AddGreenToBlueAndRed(Span<uint> pixels) => ApplyGreen(pixels, add: true);

    /// <summary>Forward subtract-green: subtracts green from red and blue, modulo 256.</summary>
    public static void SubtractGreen(Span<uint> pixels) => ApplyGreen(pixels, add: false);

    private static void ApplyGreen(Span<uint> pixels, bool add)
    {
        int count = pixels.Length;
        ref uint data = ref MemoryMarshal.GetReference(pixels);
        int i = 0;
        if (SimdConfig.Vector256Enabled)
        {
            Vector256<uint> mask = Vector256.Create(0xffu);
            for (; i <= count - Vector256<uint>.Count; i += Vector256<uint>.Count)
            {
                Vector256<uint> argb = Vector256.LoadUnsafe(ref data, (nuint)i);
                Vector256<uint> green = Vector256.ShiftRightLogical(argb, 8) & mask;
                Vector256<byte> delta = (green | Vector256.ShiftLeft(green, 16)).AsByte();
                (add ? argb.AsByte() + delta : argb.AsByte() - delta).AsUInt32().StoreUnsafe(ref data, (nuint)i);
            }
        }

        Vector128<uint> mask128 = Vector128.Create(0xffu);
        for (; i <= count - Vector128<uint>.Count; i += Vector128<uint>.Count)
        {
            Vector128<uint> argb = Vector128.LoadUnsafe(ref data, (nuint)i);
            Vector128<uint> green = Vector128.ShiftRightLogical(argb, 8) & mask128;
            Vector128<byte> delta = (green | Vector128.ShiftLeft(green, 16)).AsByte();
            (add ? argb.AsByte() + delta : argb.AsByte() - delta).AsUInt32().StoreUnsafe(ref data, (nuint)i);
        }

        for (; i < count; i++)
        {
            uint argb = Unsafe.Add(ref data, i);
            uint green = (argb >> 8) & 0xff;
            uint red = add ? ((argb >> 16) + green) & 0xff : ((argb >> 16) - green) & 0xff;
            uint blue = add ? (argb + green) & 0xff : (argb - green) & 0xff;
            Unsafe.Add(ref data, i) = (argb & 0xff00ff00u) | (red << 16) | blue;
        }
    }

    // ----- Cross colour -----

    /// <summary>The inverse cross-colour transform of the whole image, one tile run at a time.</summary>
    public static void CrossColorInverse(uint[] pixels, int width, int height, int bits, uint[] multipliers)
        => CrossColor(pixels, width, height, bits, multipliers, inverse: true);

    /// <summary>The forward cross-colour transform of the whole image (the encoder side).</summary>
    public static void CrossColorForward(uint[] pixels, int width, int height, int bits, uint[] multipliers)
        => CrossColor(pixels, width, height, bits, multipliers, inverse: false);

    private static void CrossColor(uint[] pixels, int width, int height, int bits, uint[] multipliers, bool inverse)
    {
        int tileWidth = 1 << bits;
        int tilesPerRow = (width + tileWidth - 1) >> bits;
        for (int y = 0; y < height; y++)
        {
            int tileRow = (y >> bits) * tilesPerRow;
            Span<uint> row = pixels.AsSpan(y * width, width);
            for (int x0 = 0, tile = 0; x0 < width; x0 += tileWidth, tile++)
            {
                uint m = multipliers[tileRow + tile];
                int greenToRed = (sbyte)(m & 0xff);
                int greenToBlue = (sbyte)((m >> 8) & 0xff);
                int redToBlue = (sbyte)((m >> 16) & 0xff);
                CrossColorRun(row.Slice(x0, Math.Min(tileWidth, width - x0)), greenToRed, greenToBlue, redToBlue, inverse);
            }
        }
    }

    private static void CrossColorRun(Span<uint> pixels, int greenToRed, int greenToBlue, int redToBlue, bool inverse)
    {
        int count = pixels.Length;
        ref uint data = ref MemoryMarshal.GetReference(pixels);
        int i = 0;
        if (SimdConfig.Vector256Enabled && count >= Vector256<uint>.Count)
        {
            Vector256<int> g2r = Vector256.Create(greenToRed);
            Vector256<int> g2b = Vector256.Create(greenToBlue);
            Vector256<int> r2b = Vector256.Create(redToBlue);
            Vector256<int> low = Vector256.Create(0xff);
            for (; i <= count - Vector256<uint>.Count; i += Vector256<uint>.Count)
            {
                Vector256<int> argb = Vector256.LoadUnsafe(ref data, (nuint)i).AsInt32();
                Vector256<int> green = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(argb, 16), 24);
                Vector256<int> red = Vector256.ShiftRightLogical(argb, 16) & low;
                Vector256<int> blue = argb & low;
                Vector256<int> newRed;
                Vector256<int> newBlue;
                if (inverse)
                {
                    newRed = (red + Vector256.ShiftRightArithmetic(g2r * green, 5)) & low;
                    Vector256<int> signedRed = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(newRed, 24), 24);
                    newBlue = (blue + Vector256.ShiftRightArithmetic(g2b * green, 5) + Vector256.ShiftRightArithmetic(r2b * signedRed, 5)) & low;
                }
                else
                {
                    Vector256<int> signedRed = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(argb, 8), 24);
                    newRed = (red - Vector256.ShiftRightArithmetic(g2r * green, 5)) & low;
                    newBlue = (blue - Vector256.ShiftRightArithmetic(g2b * green, 5) - Vector256.ShiftRightArithmetic(r2b * signedRed, 5)) & low;
                }

                ((argb & Vector256.Create(unchecked((int)0xff00ff00u))) | Vector256.ShiftLeft(newRed, 16) | newBlue).AsUInt32().StoreUnsafe(ref data, (nuint)i);
            }
        }

        if (count - i >= Vector128<uint>.Count)
        {
            Vector128<int> g2r = Vector128.Create(greenToRed);
            Vector128<int> g2b = Vector128.Create(greenToBlue);
            Vector128<int> r2b = Vector128.Create(redToBlue);
            Vector128<int> low = Vector128.Create(0xff);
            for (; i <= count - Vector128<uint>.Count; i += Vector128<uint>.Count)
            {
                Vector128<int> argb = Vector128.LoadUnsafe(ref data, (nuint)i).AsInt32();
                Vector128<int> green = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(argb, 16), 24);
                Vector128<int> red = Vector128.ShiftRightLogical(argb, 16) & low;
                Vector128<int> blue = argb & low;
                Vector128<int> newRed;
                Vector128<int> newBlue;
                if (inverse)
                {
                    newRed = (red + Vector128.ShiftRightArithmetic(g2r * green, 5)) & low;
                    Vector128<int> signedRed = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(newRed, 24), 24);
                    newBlue = (blue + Vector128.ShiftRightArithmetic(g2b * green, 5) + Vector128.ShiftRightArithmetic(r2b * signedRed, 5)) & low;
                }
                else
                {
                    Vector128<int> signedRed = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(argb, 8), 24);
                    newRed = (red - Vector128.ShiftRightArithmetic(g2r * green, 5)) & low;
                    newBlue = (blue - Vector128.ShiftRightArithmetic(g2b * green, 5) - Vector128.ShiftRightArithmetic(r2b * signedRed, 5)) & low;
                }

                ((argb & Vector128.Create(unchecked((int)0xff00ff00u))) | Vector128.ShiftLeft(newRed, 16) | newBlue).AsUInt32().StoreUnsafe(ref data, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            uint argb = Unsafe.Add(ref data, i);
            int green = (sbyte)(argb >> 8);
            int newRed;
            int newBlue;
            if (inverse)
            {
                newRed = ((int)((argb >> 16) & 0xff) + ((greenToRed * green) >> 5)) & 0xff;
                newBlue = ((int)(argb & 0xff) + ((greenToBlue * green) >> 5) + ((redToBlue * (sbyte)newRed) >> 5)) & 0xff;
            }
            else
            {
                int red = (sbyte)(argb >> 16);
                newRed = ((int)((argb >> 16) & 0xff) - ((greenToRed * green) >> 5)) & 0xff;
                newBlue = ((int)(argb & 0xff) - ((greenToBlue * green) >> 5) - ((redToBlue * red) >> 5)) & 0xff;
            }

            Unsafe.Add(ref data, i) = (argb & 0xff00ff00u) | ((uint)newRed << 16) | (uint)newBlue;
        }
    }

    // ----- Predictor (decoder) -----

    /// <summary>True for the predictor modes that read only the row above, which <see cref="PredictRun"/> handles.</summary>
    public static bool IsTopRowMode(int mode) => mode is 0 or 2 or 3 or 4 or 8 or 9;

    /// <summary>
    /// Reconstructs <paramref name="count"/> pixels starting at <paramref name="start"/> (never the first pixel of a
    /// row, and never on the first row) with one of the <see cref="IsTopRowMode"/> predictors. The top-right neighbour
    /// of a row's last pixel is the first pixel of the current row, which is already final and is exactly where a
    /// contiguous load of the row above continues.
    /// </summary>
    public static void PredictRun(uint[] pixels, int start, int count, int width, int mode)
    {
        ref uint current = ref MemoryMarshal.GetReference(pixels.AsSpan(start, count));
        ref uint above = ref MemoryMarshal.GetReference(pixels.AsSpan(start - width - 1, count + 2));
        int i = 0;
        if (SimdConfig.Vector256Enabled)
        {
            for (; i <= count - Vector256<uint>.Count; i += Vector256<uint>.Count)
            {
                Vector256<uint> prediction = mode switch
                {
                    0 => Vector256.Create(0xff000000u),
                    2 => Vector256.LoadUnsafe(ref above, (nuint)(i + 1)),
                    3 => Vector256.LoadUnsafe(ref above, (nuint)(i + 2)),
                    4 => Vector256.LoadUnsafe(ref above, (nuint)i),
                    8 => Average2(Vector256.LoadUnsafe(ref above, (nuint)i), Vector256.LoadUnsafe(ref above, (nuint)(i + 1))),
                    _ => Average2(Vector256.LoadUnsafe(ref above, (nuint)(i + 1)), Vector256.LoadUnsafe(ref above, (nuint)(i + 2))),
                };
                (Vector256.LoadUnsafe(ref current, (nuint)i).AsByte() + prediction.AsByte()).AsUInt32().StoreUnsafe(ref current, (nuint)i);
            }
        }

        for (; i <= count - Vector128<uint>.Count; i += Vector128<uint>.Count)
        {
            Vector128<uint> prediction = mode switch
            {
                0 => Vector128.Create(0xff000000u),
                2 => Vector128.LoadUnsafe(ref above, (nuint)(i + 1)),
                3 => Vector128.LoadUnsafe(ref above, (nuint)(i + 2)),
                4 => Vector128.LoadUnsafe(ref above, (nuint)i),
                8 => Average2(Vector128.LoadUnsafe(ref above, (nuint)i), Vector128.LoadUnsafe(ref above, (nuint)(i + 1))),
                _ => Average2(Vector128.LoadUnsafe(ref above, (nuint)(i + 1)), Vector128.LoadUnsafe(ref above, (nuint)(i + 2))),
            };
            (Vector128.LoadUnsafe(ref current, (nuint)i).AsByte() + prediction.AsByte()).AsUInt32().StoreUnsafe(ref current, (nuint)i);
        }

        for (; i < count; i++)
        {
            uint topLeft = Unsafe.Add(ref above, i);
            uint top = Unsafe.Add(ref above, i + 1);
            uint topRight = Unsafe.Add(ref above, i + 2);
            uint prediction = mode switch
            {
                0 => 0xff000000u,
                2 => top,
                3 => topRight,
                4 => topLeft,
                8 => Average2(topLeft, top),
                _ => Average2(top, topRight),
            };
            Unsafe.Add(ref current, i) = AddPixels(Unsafe.Add(ref current, i), prediction);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddPixels(uint a, uint b)
    {
        uint alphaGreen = (a & 0xff00ff00u) + (b & 0xff00ff00u);
        uint redBlue = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
        return (alphaGreen & 0xff00ff00u) | (redBlue & 0x00ff00ffu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Average2(Vector256<uint> a, Vector256<uint> b)
        => Vector256.ShiftRightLogical((a ^ b) & Vector256.Create(0xfefefefeu), 1) + (a & b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Average2(Vector128<uint> a, Vector128<uint> b)
        => Vector128.ShiftRightLogical((a ^ b) & Vector128.Create(0xfefefefeu), 1) + (a & b);
}
