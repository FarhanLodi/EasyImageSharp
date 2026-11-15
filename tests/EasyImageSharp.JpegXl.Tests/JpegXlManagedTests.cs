using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using Xunit;

namespace EasyImageSharp.JpegXl.Tests;

/// <summary>Tests that need no native library: detection, struct layouts and argument validation.</summary>
public class JpegXlManagedTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0x0A }, true)]
    [InlineData(new byte[] { 0xFF, 0x0A, 0xFA, 0x7F }, true)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A }, true)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87 }, false)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, false)]
    [InlineData(new byte[] { 0xFF }, false)]
    [InlineData(new byte[0], false)]
    public void Signature_DetectsCodestreamAndContainer(byte[] data, bool expected)
    {
        Assert.Equal(expected, JpegXlFormat.IsJpegXl(data));
        Assert.Equal(expected, JpegXlFormat.Instance.Matches(data));
    }

    [Fact]
    public void Provider_DescribesTheFormat()
    {
        ImageFormat format = JpegXlFormat.Instance;
        Assert.Same(format, JpegXlFormat.Instance);
        Assert.Equal("JPEGXL", format.Name);
        Assert.Equal("image/jxl", format.DefaultMimeType);
        Assert.Equal(["jxl"], format.FileExtensions);
        Assert.True(format.CanDecode);
        Assert.True(format.CanEncode);
    }

    /// <summary>
    /// Sizes and offsets computed by hand from the C declarations of libjxl v0.12.0 under the LP64/LLP64 rules
    /// (4-byte int/enum/float, 8-byte size_t/double/pointers, natural alignment), not from the C# layouts.
    /// </summary>
    [Fact]
    public void StructLayouts_MatchTheCAbi()
    {
        Assert.Equal(8, IntPtr.Size);
        (int basicInfo, int pixelFormat, int frameHeader, int extraChannel, int colorEncoding) = JpegXlFormat.NativeStructSizes;
        Assert.Equal(204, basicInfo);
        Assert.Equal(24, pixelFormat);
        Assert.Equal(56, frameHeader);
        Assert.Equal(44, extraChannel);
        Assert.Equal(104, colorEncoding);
        Assert.Equal(48, (int)Marshal.OffsetOf<JxlBasicInfo>(nameof(JxlBasicInfo.Orientation)));
        Assert.Equal(60, (int)Marshal.OffsetOf<JxlBasicInfo>(nameof(JxlBasicInfo.AlphaBits)));
        Assert.Equal(80, (int)Marshal.OffsetOf<JxlBasicInfo>(nameof(JxlBasicInfo.TpsNumerator)));
        Assert.Equal(104, (int)Marshal.OffsetOf<JxlBasicInfo>(nameof(JxlBasicInfo.Padding)));
        Assert.Equal(16, (int)Marshal.OffsetOf<JxlPixelFormat>(nameof(JxlPixelFormat.Align)));
        Assert.Equal(36, (int)Marshal.OffsetOf<JxlFrameHeader>(nameof(JxlFrameHeader.BlendMode)));
        Assert.Equal(24, (int)Marshal.OffsetOf<JxlColorEncoding>(nameof(JxlColorEncoding.Primaries)));
        Assert.Equal(80, (int)Marshal.OffsetOf<JxlColorEncoding>(nameof(JxlColorEncoding.TransferFunction)));
        Assert.Equal(88, (int)Marshal.OffsetOf<JxlColorEncoding>(nameof(JxlColorEncoding.Gamma)));
    }

    [Fact]
    public void EncoderOptions_RejectOutOfRangeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { Distance = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { Distance = 26 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { Effort = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { Effort = 11 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { BitsPerSample = 17 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { MaxThreads = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { FrameDuration = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegXlEncoder { LoopCount = -1 });
        var encoder = new JpegXlEncoder();
        Assert.Equal(1f, encoder.Distance);
        Assert.Equal(7, encoder.Effort);
        Assert.False(encoder.Lossless);
    }

    [Fact]
    public void NonJxlInput_IsUnknownFormatBeforeNativeWork()
    {
        var decoder = new JpegXlDecoder();
        Assert.Throws<UnknownImageFormatException>(() => decoder.Decode<PixelFormats.Rgba32>(new byte[] { 1, 2, 3, 4 }, DecoderOptions.Default));
        Assert.Throws<UnknownImageFormatException>(() => decoder.Identify(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, DecoderOptions.Default));
        Assert.Throws<ImageSizeLimitExceededException>(() => decoder.Identify(new byte[] { 0xFF, 0x0A, 0, 0 }, new DecoderOptions { MaxEncodedBytes = 2 }));
    }
}
