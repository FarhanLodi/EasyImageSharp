namespace EasyImageSharp.Formats.Webp;

/// <summary>Supplies the VP8 (lossy) key-frame encoder to the WebP writer.</summary>
internal static class Vp8FrameEncoderFactory
{
    /// <summary>Returns the lossy key-frame encoder, or <see langword="null"/> when this build has none.</summary>
    internal static IVp8FrameEncoder? Create() => new Vp8Encoder();
}
