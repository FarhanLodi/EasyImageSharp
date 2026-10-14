using EasyImageSharp.PixelFormats;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Xunit;

namespace EasyImageSharp.SkiaSharp.Tests;

public class SkiaInteropTests
{
    [Fact]
    public void FloatBitmapRoundtrip_RetainsPrecisionAndIndependentOwnership()
    {
        using var source = new Image<RgbaVector>(2, 1);
        source[0, 0] = new RgbaVector(1.25f, -0.125f, 0.123456f, 0.75f);
        source[1, 0] = new RgbaVector(0.001f, 0.002f, 0.003f, 1);
        using SKBitmap bitmap = source.ToSkiaBitmap();
        using Image<RgbaVector> copied = bitmap.ToEasyImage<RgbaVector>();
        Assert.Equal(source[0, 0].R, copied[0, 0].R, 5);
        Assert.Equal(source[0, 0].G, copied[0, 0].G, 5);
        Assert.Equal(source[0, 0].B, copied[0, 0].B, 5);
        Assert.Equal(source[0, 0].A, copied[0, 0].A, 5);
        copied[0, 0] = new RgbaVector(0, 0, 0);
        using Image<RgbaVector> secondCopy = bitmap.ToEasyImage<RgbaVector>();
        Assert.Equal(1.25f, secondCopy[0, 0].R, 5);
        Assert.Equal(1.25f, source[0, 0].R, 5);
    }

    [Fact]
    public unsafe void NativePaddedRows_AreCopiedUsingActualStride()
    {
        var info = new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap();
        Assert.True(bitmap.TryAllocPixels(info, 16));
        // Native allocation need not include padding after the final row.
        Span<byte> bytes = new((void*)bitmap.GetPixels(), 24);
        bytes.Fill(199);
        byte[] row0 = [255, 0, 0, 255, 0, 255, 0, 255];
        byte[] row1 = [0, 0, 255, 255, 255, 255, 255, 255];
        row0.CopyTo(bytes);
        row1.CopyTo(bytes[16..]);
        using Image<Rgba32> result = bitmap.ToEasyImage<Rgba32>();
        Assert.Equal(new Rgba32(255, 0, 0), result[0, 0]);
        Assert.Equal(new Rgba32(0, 255, 0), result[1, 0]);
        Assert.Equal(new Rgba32(0, 0, 255), result[0, 1]);
        Assert.Equal(new Rgba32(255, 255, 255), result[1, 1]);
    }

    [Fact]
    public void GradientPathDrawing_ChangesSelectedFrameOnly()
    {
        using var image = new Image<Rgba32>(30, 20, Rgba32.Transparent);
        using var second = new Image<Rgba32>(30, 20, Rgba32.Transparent);
        image.Frames.AddFrame(second.Frames.RootFrame);
        using Image<Rgba32> result = image.DrawWithSkia(canvas =>
        {
            using var builder = new SKPathBuilder();
            builder.AddRect(new SKRect(2, 2, 28, 18));
            using var path = builder.Detach();
            using var shader = SKShader.CreateLinearGradient(new(2, 0), new(28, 0), [SKColors.Red, SKColors.Blue], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            canvas.DrawPath(path, paint);
        }, frameIndex: 1);
        Assert.Equal(0, result[10, 10].A);
        Assert.True(result.Frames[1][4, 10].R > result.Frames[1][4, 10].B);
        Assert.True(result.Frames[1][25, 10].B > result.Frames[1][25, 10].R);
        Assert.Equal(0, image.Frames[1][10, 10].A);
        Assert.Equal(0, result.Frames[1][0, 0].A);
    }

    [Fact]
    public void CallbackException_DoesNotMutateSource()
    {
        using var image = new Image<Rgba32>(3, 2, new Rgba32(1, 2, 3));
        Assert.Throws<InvalidOperationException>(() => image.DrawWithSkia(canvas =>
        {
            canvas.Clear(SKColors.Red);
            throw new InvalidOperationException("test");
        }));
        Assert.Equal(new Rgba32(1, 2, 3), image[0, 0]);
    }

    [Fact]
    public void NativeColorConversion_MatchesSrgbLinearReference()
    {
        using var image = new Image<RgbaVector>(1, 1, new RgbaVector(0.5f, 0.5f, 0.5f, 0.75f));
        using SKColorSpace encoded = SKColorSpace.CreateSrgb();
        using SKColorSpace linear = SKColorSpace.CreateSrgbLinear();
        using Image<RgbaVector> result = image.ConvertColorSpace(encoded, linear);
        // Skia's native transfer implementation is approximate; allow less than 0.03 of an 8-bit step.
        Assert.InRange(result[0, 0].R, 0.21394f, 0.21414f);
        Assert.Equal(0.75f, result[0, 0].A, 5);
        using Image<RgbaVector> restored = result.ConvertColorSpace(linear, encoded);
        Assert.InRange(restored[0, 0].R, 0.4999f, 0.5001f);
    }

    [Fact]
    public void ArabicRun_IsShapedAndRenderedWithBundledFont()
    {
        using SKTypeface typeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSansArabic.ttf"));
        Assert.NotNull(typeface);
        using var font = new SKFont(typeface, 36);
        using var shaper = new SKShaper(typeface);
        SKShaper.Result shaped = shaper.Shape("لا", font);
        Assert.NotEmpty(shaped.Codepoints);
        Assert.DoesNotContain(0u, shaped.Codepoints);
        Assert.True(shaped.Width > 0);
        // This font renders lam-alef with contextual glyph forms (not necessarily one glyph).
        ushort[] nominal = font.GetGlyphs("لا");
        Assert.Contains(shaped.Codepoints, glyph => !nominal.Contains((ushort)glyph));
        using var image = new Image<Rgba32>(120, 80, Rgba32.Transparent);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using Image<Rgba32> rendered = image.DrawShapedText("لا", new(20, 55), font, paint);
        int visible = 0;
        for (int y = 0; y < rendered.Height; y++)
        for (int x = 0; x < rendered.Width; x++)
            if (rendered[x, y].A > 0) visible++;
        Assert.True(visible > 20);
        Assert.Equal(0, image[20, 40].A);
    }
}
