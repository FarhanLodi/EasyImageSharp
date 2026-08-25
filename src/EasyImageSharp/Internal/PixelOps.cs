using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp;

/// <summary>
/// Bulk pixel conversion helpers routed through <see cref="Rgba32"/>.
/// <para>
/// The five built-in formats are byte layouts of the same channels, so every conversion between them is one
/// of three kernels: a byte shuffle (<see cref="Rgba32"/>/<see cref="Bgra32"/>/<see cref="Rgb24"/>/
/// <see cref="Bgr24"/> in any direction, filling opaque alpha when the destination has an alpha channel the
/// source lacks), a broadcast (<see cref="L8"/> to anything) or a BT.709 luminance (anything to
/// <see cref="L8"/>). All three have a 128-bit vector implementation selected at run time; the scalar path
/// through <c>IPixel</c> stays as the reference implementation and serves every other pixel type.
/// </para>
/// <para>
/// The luminance kernel evaluates the very same single-precision expression as
/// <see cref="L8.FromRgba32(Rgba32)"/> in the same order, so the vector and scalar paths are byte-identical.
/// </para>
/// </summary>
internal static class PixelOps
{
    /// <summary>Smallest run of pixels worth entering a vector kernel for.</summary>
    private const int MinVectorCount = 32;

    public static void Convert<TSrc, TDest>(ReadOnlySpan<TSrc> source, Span<TDest> destination)
        where TSrc : unmanaged, IPixel<TSrc>
        where TDest : unmanaged, IPixel<TDest>
    {
        if (typeof(TSrc) == typeof(TDest))
        {
            MemoryMarshal.Cast<TSrc, TDest>(source).CopyTo(destination);
            return;
        }

        // Both JIT-time constants for value types, so only one of the two loops is ever compiled in.
        if (IsHighPrecision<TSrc>() && IsHighPrecision<TDest>())
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = TDest.FromScaledVector4(source[i].ToScaledVector4());
            }

            return;
        }

        int count = source.Length;
        if (count == 0)
        {
            return;
        }

        destination = destination[..count];
        int done = count >= MinVectorCount && SimdConfig.Vector128Enabled ? ConvertVectorized(source, destination, count) : 0;
        for (int i = done; i < count; i++)
        {
            destination[i] = TDest.FromRgba32(source[i].ToRgba32());
        }
    }

    public static void FromRgba32<TDest>(ReadOnlySpan<Rgba32> source, Span<TDest> destination)
        where TDest : unmanaged, IPixel<TDest>
        => Convert(source, destination);

    public static void ToRgba32<TSrc>(ReadOnlySpan<TSrc> source, Span<Rgba32> destination)
        where TSrc : unmanaged, IPixel<TSrc>
        => Convert(source, destination);

    /// <summary>
    /// Writes the BT.709 luminance of every pixel of <paramref name="source"/> into <paramref name="destination"/>.
    /// Equivalent to converting to <see cref="L8"/>, and to <see cref="Luminance8"/> per pixel.
    /// </summary>
    public static void ToLuminance<TSrc>(ReadOnlySpan<TSrc> source, Span<byte> destination)
        where TSrc : unmanaged, IPixel<TSrc>
        => Convert(source, MemoryMarshal.Cast<byte, L8>(destination));

    /// <summary>Expands pixels to normalised RGBA components, keeping the full precision of any format.</summary>
    public static void ToScaledVector4<TSrc>(ReadOnlySpan<TSrc> source, Span<Vector4> destination)
        where TSrc : unmanaged, IPixel<TSrc>
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = source[i].ToScaledVector4();
        }
    }

    /// <summary>Packs normalised RGBA components back into a pixel format.</summary>
    public static void FromScaledVector4<TDest>(ReadOnlySpan<Vector4> source, Span<TDest> destination)
        where TDest : unmanaged, IPixel<TDest>
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = TDest.FromScaledVector4(source[i]);
        }
    }

    /// <summary>
    /// True for the formats that carry more than 8 bits per component and therefore cannot round trip
    /// through <see cref="Rgba32"/>. The comparisons fold away at JIT time for a given pixel type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsHighPrecision<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => typeof(TPixel) == typeof(Rgb48)
            || typeof(TPixel) == typeof(Rgba64)
            || typeof(TPixel) == typeof(L16)
            || typeof(TPixel) == typeof(La32)
            || typeof(TPixel) == typeof(RgbaVector);

    /// <summary>Computes the BT.709 luminance of a pixel in the 0-255 range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Luminance8(Rgba32 color)
    {
        float l = (color.R * 0.2126f) + (color.G * 0.7152f) + (color.B * 0.0722f);
        return (byte)Math.Clamp((int)(l + 0.5f), 0, 255);
    }

    /// <summary>
    /// Replaces the colour channels of every pixel with its BT.709 luminance, leaving alpha untouched.
    /// Interleaved RGB(A) formats take the luminance kernel followed by a broadcast; other formats take
    /// the scalar path.
    /// </summary>
    public static void Grayscale<TPixel>(Span<TPixel> pixels)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int count = pixels.Length;
        if (count == 0)
        {
            return;
        }

        if (count >= MinVectorCount && SimdConfig.Vector128Enabled && TryGetLayout<TPixel>(out ChannelLayout layout))
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                Span<byte> luminance = rented.AsSpan(0, count);
                ref byte pixelBytes = ref Unsafe.As<TPixel, byte>(ref MemoryMarshal.GetReference(pixels));
                ref byte luminanceBytes = ref MemoryMarshal.GetReference(luminance);

                int measured = ToL8(ref pixelBytes, ref luminanceBytes, count, layout);
                for (int i = measured; i < count; i++)
                {
                    luminance[i] = Luminance8(pixels[i].ToRgba32());
                }

                int written = layout.HasAlpha
                    ? BlendLuminanceKeepAlpha(ref luminanceBytes, ref pixelBytes, count, layout)
                    : FromL8(ref luminanceBytes, ref pixelBytes, count, layout);
                for (int i = written; i < count; i++)
                {
                    byte l = luminance[i];
                    pixels[i] = TPixel.FromRgba32(new Rgba32(l, l, l, pixels[i].ToRgba32().A));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            Rgba32 source = pixels[i].ToRgba32();
            byte l = Luminance8(source);
            pixels[i] = TPixel.FromRgba32(new Rgba32(l, l, l, source.A));
        }
    }

    /// <summary>
    /// Widens every channel of every pixel to a single-precision value in 0..255, four floats per pixel in
    /// R, G, B, A order. <paramref name="destination"/> must hold four floats per source pixel.
    /// </summary>
    public static void WidenToSingle(ReadOnlySpan<Rgba32> source, Span<float> destination)
    {
        int count = source.Length;
        int i = 0;
        if (SimdConfig.Vector128Enabled && count >= 8)
        {
            ref byte src = ref Unsafe.As<Rgba32, byte>(ref MemoryMarshal.GetReference(source));
            ref float dst = ref MemoryMarshal.GetReference(destination);
            for (; i <= count - 4; i += 4)
            {
                (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(Vector128.LoadUnsafe(ref src, (nuint)(i * 4)));
                (Vector128<uint> q0, Vector128<uint> q1) = Vector128.Widen(low);
                (Vector128<uint> q2, Vector128<uint> q3) = Vector128.Widen(high);
                nuint offset = (nuint)(i * 4);
                Vector128.ConvertToSingle(q0.AsInt32()).StoreUnsafe(ref dst, offset);
                Vector128.ConvertToSingle(q1.AsInt32()).StoreUnsafe(ref dst, offset + 4);
                Vector128.ConvertToSingle(q2.AsInt32()).StoreUnsafe(ref dst, offset + 8);
                Vector128.ConvertToSingle(q3.AsInt32()).StoreUnsafe(ref dst, offset + 12);
            }
        }

        for (; i < count; i++)
        {
            Rgba32 p = source[i];
            int o = i * 4;
            destination[o] = p.R;
            destination[o + 1] = p.G;
            destination[o + 2] = p.B;
            destination[o + 3] = p.A;
        }
    }

    /// <summary>
    /// Widens one channel of every pixel to a single-precision value in 0..255.
    /// <paramref name="channel"/> is the byte offset of the channel inside <see cref="Rgba32"/>
    /// (0 = R, 1 = G, 2 = B, 3 = A).
    /// </summary>
    public static void ExtractChannel(ReadOnlySpan<Rgba32> source, int channel, Span<float> destination)
    {
        int count = source.Length;
        int i = 0;
        int shift = channel * 8;

        if (SimdConfig.Vector256Enabled && count >= Vector256<float>.Count)
        {
            ref uint src = ref Unsafe.As<Rgba32, uint>(ref MemoryMarshal.GetReference(source));
            ref float dst = ref MemoryMarshal.GetReference(destination);
            Vector256<uint> mask = Vector256.Create(0x000000FFu);
            for (; i <= count - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<uint> pixels = Vector256.LoadUnsafe(ref src, (nuint)i);
                Vector256<int> values = (Vector256.ShiftRightLogical(pixels, shift) & mask).AsInt32();
                Vector256.ConvertToSingle(values).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            Rgba32 p = source[i];
            destination[i] = channel switch
            {
                0 => p.R,
                1 => p.G,
                2 => p.B,
                _ => p.A,
            };
        }
    }

    /// <summary>
    /// Writes <c>((value / 255) - mean) / standardDeviation</c> for every element. The expression matches
    /// the scalar formulation operation for operation, so the vector path rounds identically.
    /// </summary>
    public static void Normalize(ReadOnlySpan<float> source, Span<float> destination, float mean, float standardDeviation)
    {
        int count = source.Length;
        int i = 0;

        if (SimdConfig.Vector256Enabled && count >= Vector256<float>.Count)
        {
            ref float src = ref MemoryMarshal.GetReference(source);
            ref float dst = ref MemoryMarshal.GetReference(destination);
            Vector256<float> scale = Vector256.Create(255f);
            Vector256<float> offset = Vector256.Create(mean);
            Vector256<float> deviation = Vector256.Create(standardDeviation);
            for (; i <= count - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                (((Vector256.LoadUnsafe(ref src, (nuint)i) / scale) - offset) / deviation).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            destination[i] = ((source[i] / 255f) - mean) / standardDeviation;
        }
    }

    /// <summary>Byte stride and alpha presence of the interleaved RGB(A) formats.</summary>
    public static bool TryGetChannelStride<TPixel>(out int bytesPerPixel, out bool hasAlpha)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (TryGetLayout<TPixel>(out ChannelLayout layout))
        {
            bytesPerPixel = layout.BytesPerPixel;
            hasAlpha = layout.HasAlpha;
            return true;
        }

        bytesPerPixel = 0;
        hasAlpha = false;
        return false;
    }

    // ----- Vector dispatch -----

    /// <summary>Runs the best available vector kernel and returns the number of pixels it converted.</summary>
    private static int ConvertVectorized<TSrc, TDest>(ReadOnlySpan<TSrc> source, Span<TDest> destination, int count)
        where TSrc : unmanaged, IPixel<TSrc>
        where TDest : unmanaged, IPixel<TDest>
    {
        ref byte src = ref Unsafe.As<TSrc, byte>(ref MemoryMarshal.GetReference(source));
        ref byte dst = ref Unsafe.As<TDest, byte>(ref MemoryMarshal.GetReference(destination));
        bool sourceIsL8 = typeof(TSrc) == typeof(L8);
        bool destinationIsL8 = typeof(TDest) == typeof(L8);

        if (destinationIsL8)
        {
            return TryGetLayout<TSrc>(out ChannelLayout s) ? ToL8(ref src, ref dst, count, s) : 0;
        }

        if (!TryGetLayout<TDest>(out ChannelLayout d))
        {
            return 0;
        }

        return sourceIsL8
            ? FromL8(ref src, ref dst, count, d)
            : TryGetLayout<TSrc>(out ChannelLayout s2) ? ShuffleChannels(ref src, ref dst, count, s2, d) : 0;
    }

    /// <summary>Byte offsets of the channels inside one pixel of an interleaved 8-bit RGB(A) format.</summary>
    private readonly record struct ChannelLayout(int BytesPerPixel, int R, int G, int B, int A)
    {
        public bool HasAlpha => this.A >= 0;
    }

    private static bool TryGetLayout<TPixel>(out ChannelLayout layout)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            layout = new ChannelLayout(4, 0, 1, 2, 3);
            return true;
        }

        if (typeof(TPixel) == typeof(Bgra32))
        {
            layout = new ChannelLayout(4, 2, 1, 0, 3);
            return true;
        }

        if (typeof(TPixel) == typeof(Rgb24))
        {
            layout = new ChannelLayout(3, 0, 1, 2, -1);
            return true;
        }

        if (typeof(TPixel) == typeof(Bgr24))
        {
            layout = new ChannelLayout(3, 2, 1, 0, -1);
            return true;
        }

        layout = default;
        return false;
    }

    // ----- Kernels -----

    /// <summary>
    /// Rearranges four pixels per iteration with a single byte shuffle, filling opaque alpha when the
    /// destination carries an alpha channel that the source does not.
    /// </summary>
    private static int ShuffleChannels(ref byte src, ref byte dst, int count, ChannelLayout s, ChannelLayout d)
    {
        Span<byte> indices = stackalloc byte[16];
        Span<byte> fill = stackalloc byte[16];
        indices.Fill(SkipIndex);
        fill.Clear();
        for (int p = 0; p < 4; p++)
        {
            int o = p * d.BytesPerPixel;
            int i = p * s.BytesPerPixel;
            indices[o + d.R] = (byte)(i + s.R);
            indices[o + d.G] = (byte)(i + s.G);
            indices[o + d.B] = (byte)(i + s.B);
            if (d.HasAlpha)
            {
                if (s.HasAlpha)
                {
                    indices[o + d.A] = (byte)(i + s.A);
                }
                else
                {
                    fill[o + d.A] = byte.MaxValue;
                }
            }
        }

        Vector128<byte> mask = Vector128.Create<byte>(indices);
        Vector128<byte> alpha = Vector128.Create<byte>(fill);
        bool addAlpha = d.HasAlpha && !s.HasAlpha;

        // A 16-byte load/store covers four pixels plus slack; keep both inside their buffers.
        int limit = count - (s.BytesPerPixel == 3 || d.BytesPerPixel == 3 ? 6 : 4);
        int x = 0;
        for (; x <= limit; x += 4)
        {
            Vector128<byte> value = Vector128.Shuffle(Vector128.LoadUnsafe(ref src, (nuint)(x * s.BytesPerPixel)), mask);
            if (addAlpha)
            {
                value |= alpha;
            }

            value.StoreUnsafe(ref dst, (nuint)(x * d.BytesPerPixel));
        }

        return x;
    }

    /// <summary>Broadcasts sixteen luminance samples per iteration into an interleaved RGB(A) destination.</summary>
    private static int FromL8(ref byte src, ref byte dst, int count, ChannelLayout d)
    {
        Span<byte> indices = stackalloc byte[16];
        Span<byte> fill = stackalloc byte[16];
        Vector128<byte> mask0 = BuildBroadcastMask(indices, fill, 0, d);
        Vector128<byte> mask1 = BuildBroadcastMask(indices, fill, 1, d);
        Vector128<byte> mask2 = BuildBroadcastMask(indices, fill, 2, d);
        Vector128<byte> mask3 = BuildBroadcastMask(indices, fill, 3, d);
        Vector128<byte> alpha = Vector128.Create<byte>(fill);
        bool addAlpha = d.HasAlpha;
        int bpp = d.BytesPerPixel;

        // Each iteration reads sixteen samples and writes four 16-byte blocks; the last block starts at
        // pixel x + 12, so it needs 16 bytes of room from there.
        int limit = count - (bpp == 3 ? 18 : 16);
        int x = 0;
        for (; x <= limit; x += 16)
        {
            Vector128<byte> value = Vector128.LoadUnsafe(ref src, (nuint)x);
            Store(ref dst, (nuint)(x * bpp), Vector128.Shuffle(value, mask0), alpha, addAlpha);
            Store(ref dst, (nuint)((x + 4) * bpp), Vector128.Shuffle(value, mask1), alpha, addAlpha);
            Store(ref dst, (nuint)((x + 8) * bpp), Vector128.Shuffle(value, mask2), alpha, addAlpha);
            Store(ref dst, (nuint)((x + 12) * bpp), Vector128.Shuffle(value, mask3), alpha, addAlpha);
        }

        return x;

        static void Store(ref byte destination, nuint offset, Vector128<byte> value, Vector128<byte> alpha, bool addAlpha)
        {
            if (addAlpha)
            {
                value |= alpha;
            }

            value.StoreUnsafe(ref destination, offset);
        }
    }

    /// <summary>Shuffle mask spreading source samples <c>4 * group .. 4 * group + 3</c> over four destination pixels.</summary>
    private static Vector128<byte> BuildBroadcastMask(Span<byte> indices, Span<byte> fill, int group, ChannelLayout d)
    {
        indices.Fill(SkipIndex);
        if (group == 0)
        {
            fill.Clear();
        }

        for (int p = 0; p < 4; p++)
        {
            int o = p * d.BytesPerPixel;
            byte sample = (byte)((group * 4) + p);
            indices[o + d.R] = sample;
            indices[o + d.G] = sample;
            indices[o + d.B] = sample;
            if (d.HasAlpha)
            {
                fill[o + d.A] = byte.MaxValue;
            }
        }

        return Vector128.Create<byte>(indices);
    }

    /// <summary>
    /// Writes sixteen luminance samples per iteration into the colour channels of an existing four-byte
    /// destination, preserving its alpha bytes.
    /// </summary>
    private static int BlendLuminanceKeepAlpha(ref byte luminance, ref byte dst, int count, ChannelLayout d)
    {
        Span<byte> indices = stackalloc byte[16];
        Span<byte> keep = stackalloc byte[16];
        keep.Clear();
        Vector128<byte> mask0 = BuildLuminanceMask(indices, keep, 0, d);
        Vector128<byte> mask1 = BuildLuminanceMask(indices, keep, 1, d);
        Vector128<byte> mask2 = BuildLuminanceMask(indices, keep, 2, d);
        Vector128<byte> mask3 = BuildLuminanceMask(indices, keep, 3, d);
        Vector128<byte> alphaKeep = Vector128.Create<byte>(keep);

        int limit = count - 16;
        int x = 0;
        for (; x <= limit; x += 16)
        {
            Vector128<byte> samples = Vector128.LoadUnsafe(ref luminance, (nuint)x);
            Blend(ref dst, (nuint)(x * 4), Vector128.Shuffle(samples, mask0), alphaKeep);
            Blend(ref dst, (nuint)((x + 4) * 4), Vector128.Shuffle(samples, mask1), alphaKeep);
            Blend(ref dst, (nuint)((x + 8) * 4), Vector128.Shuffle(samples, mask2), alphaKeep);
            Blend(ref dst, (nuint)((x + 12) * 4), Vector128.Shuffle(samples, mask3), alphaKeep);
        }

        return x;

        static void Blend(ref byte destination, nuint offset, Vector128<byte> colors, Vector128<byte> alphaKeep)
            => (colors | (Vector128.LoadUnsafe(ref destination, offset) & alphaKeep)).StoreUnsafe(ref destination, offset);
    }

    /// <summary>Shuffle mask spreading four luminance samples over the colour channels, zeroing alpha.</summary>
    private static Vector128<byte> BuildLuminanceMask(Span<byte> indices, Span<byte> keep, int group, ChannelLayout d)
    {
        indices.Fill(SkipIndex);
        for (int p = 0; p < 4; p++)
        {
            int o = p * 4;
            byte sample = (byte)((group * 4) + p);
            indices[o + d.R] = sample;
            indices[o + d.G] = sample;
            indices[o + d.B] = sample;
            keep[o + d.A] = byte.MaxValue;
        }

        return Vector128.Create<byte>(indices);
    }

    /// <summary>
    /// BT.709 luminance of sixteen pixels per iteration. The arithmetic mirrors
    /// <see cref="L8.FromRgba32(Rgba32)"/> lane by lane, including the order of the additions and the
    /// truncation of <c>value + 0.5</c>, so the result is identical to the scalar path.
    /// </summary>
    private static int ToL8(ref byte src, ref byte dst, int count, ChannelLayout s)
    {
        // Gather each group of four pixels into a canonical R,G,B,0 layout so one code path serves every source.
        Span<byte> indices = stackalloc byte[16];
        indices.Fill(SkipIndex);
        for (int p = 0; p < 4; p++)
        {
            int i = p * s.BytesPerPixel;
            indices[(p * 4) + 0] = (byte)(i + s.R);
            indices[(p * 4) + 1] = (byte)(i + s.G);
            indices[(p * 4) + 2] = (byte)(i + s.B);
        }

        Vector128<byte> mask = Vector128.Create<byte>(indices);
        int bpp = s.BytesPerPixel;
        int limit = count - (bpp == 3 ? 18 : 16);
        int x = 0;
        for (; x <= limit; x += 16)
        {
            Vector128<int> l0 = LuminanceQuad(ref src, (nuint)(x * bpp), mask);
            Vector128<int> l1 = LuminanceQuad(ref src, (nuint)((x + 4) * bpp), mask);
            Vector128<int> l2 = LuminanceQuad(ref src, (nuint)((x + 8) * bpp), mask);
            Vector128<int> l3 = LuminanceQuad(ref src, (nuint)((x + 12) * bpp), mask);
            Vector128<ushort> low = Vector128.Narrow(l0, l1).AsUInt16();
            Vector128<ushort> high = Vector128.Narrow(l2, l3).AsUInt16();
            Vector128.Narrow(low, high).StoreUnsafe(ref dst, (nuint)x);
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> LuminanceQuad(ref byte src, nuint offset, Vector128<byte> mask)
    {
        Vector128<uint> rgb = Vector128.Shuffle(Vector128.LoadUnsafe(ref src, offset), mask).AsUInt32();
        Vector128<uint> byteMask = Vector128.Create(0x000000FFu);
        Vector128<float> r = Vector128.ConvertToSingle((rgb & byteMask).AsInt32());
        Vector128<float> g = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(rgb, 8) & byteMask).AsInt32());
        Vector128<float> b = Vector128.ConvertToSingle((Vector128.ShiftRightLogical(rgb, 16) & byteMask).AsInt32());
        Vector128<float> l = (r * Vector128.Create(0.2126f)) + (g * Vector128.Create(0.7152f));
        l += b * Vector128.Create(0.0722f);
        l += Vector128.Create(0.5f);
        Vector128<int> value = Vector128.ConvertToInt32(l);
        return Vector128.Max(Vector128.Min(value, Vector128.Create(255)), Vector128<int>.Zero);
    }

    /// <summary>Shuffle index whose high bit is set, which every platform resolves to a zero byte.</summary>
    private const byte SkipIndex = 0xFF;
}
