using System.Reflection;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Streaming;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The row-streaming resize must produce bit for bit what the in-memory resize produces, for every resampler,
/// pixel format, premultiplication and companding setting, with either way of holding the vertical window.
/// </summary>
public class ScanlineResizerTests
{
    private static readonly (int SourceWidth, int SourceHeight, int Width, int Height)[] Geometries =
    [
        (61, 37, 23, 17),  // Reduce both axes.
        (23, 17, 61, 40),  // Enlarge both axes.
        (61, 37, 61, 20),  // Height only: the horizontal kernel is the identity window but still applied.
        (61, 37, 20, 37),  // Width only.
        (50, 40, 1, 1),    // Everything into one pixel.
        (3, 2, 50, 1),     // Enlarge one axis, reduce the other.
        (200, 3, 7, 30),
    ];

    public static IEnumerable<object[]> Resamplers()
        => typeof(KnownResamplers).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => new object[] { property.Name });

    [Theory]
    [MemberData(nameof(Resamplers))]
    public void EveryResampler_MatchesTheInMemoryResizeExactly(string resamplerName)
    {
        IResampler sampler = (IResampler)typeof(KnownResamplers).GetProperty(resamplerName)!.GetValue(null)!;
        foreach ((int sw, int sh, int w, int h) in Geometries)
        {
            foreach (bool? ring in new bool?[] { null, true, false })
            {
                AssertMatches<Rgba32>(sw, sh, w, h, sampler, premultiply: true, compand: false, ring);
                AssertMatches<Rgba32>(sw, sh, w, h, sampler, premultiply: false, compand: true, ring);
                AssertMatches<Rgba32>(sw, sh, w, h, sampler, premultiply: true, compand: true, ring);
                AssertMatches<Rgb24>(sw, sh, w, h, sampler, premultiply: true, compand: false, ring);
                AssertMatches<L8>(sw, sh, w, h, sampler, premultiply: true, compand: false, ring);
                AssertMatches<L8>(sw, sh, w, h, sampler, premultiply: true, compand: true, ring);
                AssertMatches<Rgba64>(sw, sh, w, h, sampler, premultiply: true, compand: false, ring);
                AssertMatches<Rgba64>(sw, sh, w, h, sampler, premultiply: false, compand: true, ring);
                AssertMatches<Bgra32>(sw, sh, w, h, sampler, premultiply: false, compand: false, ring);
            }
        }
    }

    [Fact]
    public void UnusualKernels_WithGapsBetweenWindowsOrVeryWideWindows_MatchExactly()
    {
        foreach (IResampler sampler in new IResampler[] { new NarrowResampler(), new WideResampler() })
        {
            foreach ((int sw, int sh, int w, int h) in Geometries)
            {
                foreach (bool? ring in new bool?[] { true, false })
                {
                    AssertMatches<Rgba32>(sw, sh, w, h, sampler, premultiply: true, compand: false, ring);
                    AssertMatches<L8>(sw, sh, w, h, sampler, premultiply: false, compand: false, ring);
                }
            }
        }
    }

    [Fact]
    public void TheStrategy_FollowsWhicheverHoldsFewerRows()
    {
        // Reducing 400 rows to 10 with Lanczos3: a window spans about 241 source rows, but only about 7 outputs are open at once.
        Assert.Equal("Accumulate", Run<Rgba32>(5, 400, 5, 10, KnownResamplers.Lanczos3).Strategy);

        // Enlarging 10 rows to 400: a window spans 6 source rows, but each source row feeds about 240 outputs.
        Assert.Equal("Ring", Run<Rgba32>(5, 10, 5, 400, KnownResamplers.Lanczos3).Strategy);
        Assert.Equal("Nearest", Run<Rgba32>(5, 10, 7, 400, KnownResamplers.NearestNeighbor).Strategy);
        Assert.Equal("PassThrough", Run<Rgba32>(5, 10, 5, 10, KnownResamplers.Lanczos3).Strategy);

        var vertical = ResizeKernelMap.Build(400, 10, KnownResamplers.Lanczos3);
        (int ringRows, int accumulatorRows) = ScanlineResizer<Rgba32>.WindowRows(vertical, 400);
        Assert.True(accumulatorRows < 10 && ringRows > 200, $"ring {ringRows}, accumulators {accumulatorRows}");
    }

    [Fact]
    public void OutputRows_LeaveAsSoonAsTheirWindowIsComplete()
    {
        // Bicubic reduction by 4: output row y needs source rows up to about 4y + 5, so after 40 of 100 source rows
        // the first several output rows must already have been passed on.
        var sink = new CollectingSink<Rgba32>();
        var resizer = new ScanlineResizer<Rgba32>(sink, 10, 25, KnownResamplers.Bicubic, true, false, long.MaxValue);
        resizer.Begin(40, 100, new ImageMetadata());
        var row = new Rgba32[40];
        for (int y = 0; y < 40; y++)
        {
            resizer.WriteRow(row);
        }

        Assert.InRange(sink.RowsWritten, 7, 10);
        for (int y = 40; y < 100; y++)
        {
            resizer.WriteRow(row);
        }

        resizer.Complete();
        Assert.True(sink.Completed);
        Assert.Equal(25, sink.RowsWritten);
    }

    [Fact]
    public void AWindowLargerThanTheBudget_IsRejectedBeforeItIsAllocated()
    {
        // The kernel maps of an extreme enlargement are already too large...
        var maps = new ScanlineResizer<Rgba32>(new CollectingSink<Rgba32>(), 100_000, 1000, KnownResamplers.Lanczos8, true, false, 1_000_000);
        Assert.Throws<ImageSizeLimitExceededException>(() => maps.Begin(10, 10, new ImageMetadata()));

        // ...and here the maps fit but a ring of enlarged rows does not.
        var ring = new ScanlineResizer<Rgba32>(new CollectingSink<Rgba32>(), 20_000, 20_000, KnownResamplers.Bicubic, true, false, 2_000_000);
        Assert.Throws<ImageSizeLimitExceededException>(() => ring.Begin(1000, 1000, new ImageMetadata()));

        // Reducing a tall source to one row needs one accumulator, not a window as tall as the source.
        var reduce = new ScanlineResizer<Rgba32>(new CollectingSink<Rgba32>(), 1000, 1, KnownResamplers.Lanczos8, true, false, 1_000_000);
        reduce.Begin(2000, 100_000, new ImageMetadata());
        Assert.Equal("Accumulate", reduce.Strategy);
        reduce.Dispose();

        // An invalid radius is the in-memory resize's ArgumentException, not a limit.
        var invalid = new ScanlineResizer<Rgba32>(new CollectingSink<Rgba32>(), 10, 10, new BrokenResampler(), true, false, 1_000_000);
        Assert.Throws<ArgumentException>(() => invalid.Begin(20, 20, new ImageMetadata()));
    }

    [Fact]
    public void CallsOutOfOrder_AreRejected_AndDisposeReachesTheNextStage()
    {
        var sink = new CollectingSink<Rgba32>();
        var resizer = new ScanlineResizer<Rgba32>(sink, 3, 3, KnownResamplers.Bicubic, true, false, long.MaxValue);
        Assert.Throws<InvalidOperationException>(() => resizer.WriteRow(new Rgba32[6]));
        resizer.Begin(6, 6, new ImageMetadata());
        Assert.Throws<ArgumentException>(() => resizer.WriteRow(new Rgba32[5]));
        resizer.WriteRow(new Rgba32[6]);
        Assert.Throws<InvalidOperationException>(() => resizer.Complete());
        resizer.Dispose();
        Assert.True(sink.Disposed);
        Assert.Throws<ObjectDisposedException>(() => resizer.WriteRow(new Rgba32[6]));
    }

    private static ScanlineResizer<TPixel> Run<TPixel>(int sw, int sh, int w, int h, IResampler sampler)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using Image<TPixel> source = StreamingTestSupport.Noise<TPixel>(sw, sh, seed: 1);
        var resizer = new ScanlineResizer<TPixel>(new CollectingSink<TPixel>(), w, h, sampler, true, false, long.MaxValue);
        resizer.Begin(sw, sh, new ImageMetadata());
        for (int y = 0; y < sh; y++)
        {
            resizer.WriteRow(source.Frames.RootFrame.GetRowSpan(y));
        }

        resizer.Complete();
        return resizer;
    }

    private static void AssertMatches<TPixel>(
        int sourceWidth, int sourceHeight, int width, int height, IResampler sampler, bool premultiply, bool compand, bool? ring)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using Image<TPixel> source = StreamingTestSupport.Noise<TPixel>(sourceWidth, sourceHeight, seed: sourceWidth + (7 * height));
        using Image<TPixel> expected = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Sampler = sampler,
            PremultiplyAlpha = premultiply,
            Compand = compand,
        }));

        var sink = new CollectingSink<TPixel>();
        var resizer = new ScanlineResizer<TPixel>(sink, width, height, sampler, premultiply, compand, long.MaxValue) { ForceRing = ring };
        var metadata = new ImageMetadata();
        resizer.Begin(sourceWidth, sourceHeight, metadata);
        Assert.Same(metadata, sink.Metadata);
        Assert.Equal((width, height), (sink.Width, sink.Height));
        for (int y = 0; y < sourceHeight; y++)
        {
            resizer.WriteRow(source.Frames.RootFrame.GetRowSpan(y));
        }

        resizer.Complete();
        Assert.True(sink.Completed);
        TPixel[] actual = sink.Pixels();
        TPixel[] wanted = StreamingTestSupport.Pixels(expected);
        if (!wanted.AsSpan().SequenceEqual(actual))
        {
            int index = Enumerable.Range(0, wanted.Length).First(i => !wanted[i].Equals(actual[i]));
            Assert.Fail(
                $"{typeof(TPixel).Name} {sampler.GetType().Name} {sourceWidth}x{sourceHeight}->{width}x{height} premultiply={premultiply} "
                + $"compand={compand} strategy={resizer.Strategy}: first difference at ({index % width}, {index / width}): "
                + $"expected {wanted[index]}, got {actual[index]}.");
        }
    }

    /// <summary>A kernel narrower than a pixel, so enlarging leaves source rows no window reads.</summary>
    private sealed class NarrowResampler : IResampler
    {
        public float Radius => 0.2f;

        public float GetValue(float x) => MathF.Abs(x) < 0.2f ? 1f - (MathF.Abs(x) * 4f) : 0f;
    }

    /// <summary>A kernel with a radius no resize accepts.</summary>
    private sealed class BrokenResampler : IResampler
    {
        public float Radius => float.NaN;

        public float GetValue(float x) => 1f;
    }

    /// <summary>A very wide smooth kernel, so every window covers most of the source.</summary>
    private sealed class WideResampler : IResampler
    {
        public float Radius => 9f;

        public float GetValue(float x) => MathF.Abs(x) < 9f ? MathF.Cos(MathF.Abs(x) / 9f * MathF.PI / 2f) : 0f;
    }
}
