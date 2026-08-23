using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Dithering;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Processing;

/// <summary>Quantization and dithering operations.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext Quantize(IQuantizer quantizer)
    {
        ArgumentNullException.ThrowIfNull(quantizer);
        return this.PerFrame(frame =>
        {
            IQuantizer<TPixel> worker = quantizer.CreatePixelSpecificQuantizer<TPixel>();
            var bounds = new Rectangle(0, 0, frame.Width, frame.Height);
            if (worker is QuantizerBase<TPixel> builtIn)
            {
                builtIn.QuantizeFrameInPlace(frame, bounds);
            }
            else
            {
                IndexedImageFrame<TPixel> indexed = worker.QuantizeFrame(frame, bounds);
                WritePaletteColors(frame, indexed);
            }

            return frame;
        });
    }

    public IImageProcessingContext Dither(IDither dither, float ditherScale, ReadOnlyMemory<Color> palette)
    {
        ArgumentNullException.ThrowIfNull(dither);
        if (palette.Length is 0 or > 256)
        {
            throw new ArgumentException("A palette must contain between 1 and 256 colours.", nameof(palette));
        }

        if (!(ditherScale >= 0f && ditherScale <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(ditherScale), ditherScale, "The dither scale must be between 0 and 1.");
        }

        var colors = new Rgba32[palette.Length];
        ReadOnlySpan<Color> source = palette.Span;
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = source[i].ToRgba32();
        }

        var map = new PaletteIndexMap(colors, ColorMatchingMode.Exact, new QuantizerOptions().AlphaCutoff);
        return this.PerFrame(frame =>
        {
            dither.Apply(frame, new Rectangle(0, 0, frame.Width, frame.Height), map, ditherScale, Memory<byte>.Empty, replacePixels: true);
            return frame;
        });
    }

    public IImageProcessingContext BinaryDither(IDither dither, Color upperColor, Color lowerColor)
    {
        ArgumentNullException.ThrowIfNull(dither);
        TPixel upper = TPixel.FromRgba32(upperColor.ToRgba32());
        TPixel lower = TPixel.FromRgba32(lowerColor.ToRgba32());
        var map = new BinaryLuminanceMap();
        return this.PerFrame(frame =>
        {
            // Decide black/white by luminance first, then paint the requested colours by index so the dither's
            // error is measured against the ideal thresholds rather than against arbitrary display colours.
            var indices = new byte[frame.Width * frame.Height];
            dither.Apply(frame, new Rectangle(0, 0, frame.Width, frame.Height), map, 1f, indices, replacePixels: false);
            Span<TPixel> pixels = frame.PixelSpan;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = indices[i] == 0 ? lower : upper;
            }

            return frame;
        });
    }

    private static void WritePaletteColors(ImageFrame<TPixel> frame, IndexedImageFrame<TPixel> indexed)
    {
        ReadOnlySpan<TPixel> palette = indexed.Palette.Span;
        for (int y = 0; y < frame.Height; y++)
        {
            Span<TPixel> pixels = frame.GetRowSpan(y);
            ReadOnlySpan<byte> indices = indexed.GetRowSpan(y);
            for (int x = 0; x < pixels.Length; x++)
            {
                pixels[x] = palette[indices[x]];
            }
        }
    }

    /// <summary>A two-entry "palette" (black, white) resolved by BT.709 luminance instead of colour distance.</summary>
    private sealed class BinaryLuminanceMap : IPaletteMap
    {
        private static readonly Rgba32[] BlackAndWhite = { Rgba32.Black, Rgba32.White };

        public ReadOnlySpan<Rgba32> Palette => BlackAndWhite;

        public int GetPaletteIndex(Rgba32 color, out Rgba32 match)
        {
            int index = PixelOps.Luminance8(color) >= 128 ? 1 : 0;
            match = BlackAndWhite[index];
            return index;
        }
    }
}
