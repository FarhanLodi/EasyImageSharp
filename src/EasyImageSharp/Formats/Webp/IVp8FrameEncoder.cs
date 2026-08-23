namespace EasyImageSharp.Formats.Webp;

/// <summary>Encodes a single VP8 key frame into a raw 'VP8 ' chunk payload.</summary>
internal interface IVp8FrameEncoder
{
    /// <param name="y">Luma plane, <paramref name="width"/> x <paramref name="height"/>, row-major, stride = width.</param>
    /// <param name="u">Chroma-blue plane at half resolution, stride = (width + 1) / 2.</param>
    /// <param name="v">Chroma-red plane at half resolution, stride = (width + 1) / 2.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="quality">1..100; higher is better quality and a larger payload.</param>
    /// <param name="method">0..6 effort level; higher spends more time for a smaller payload.</param>
    /// <returns>The raw VP8 bitstream (the bytes that go inside the 'VP8 ' chunk, without the chunk header).</returns>
    byte[] EncodeKeyFrame(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int width, int height, int quality, int method);
}
