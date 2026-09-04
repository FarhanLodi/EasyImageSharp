using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace Thumbnailer;

/// <summary>
/// Raised when one of the sample's assertions does not hold. Carrying the failure as an exception keeps the
/// happy path free of result plumbing while still producing a single <c>FAIL: &lt;reason&gt;</c> line and a
/// non-zero exit code, which is all either CI job looks at.
/// </summary>
internal sealed class ThumbnailerFailure : Exception
{
    /// <summary>Initializes the failure with the reason to report.</summary>
    /// <param name="message">The reason the run failed.</param>
    internal ThumbnailerFailure(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A miniature thumbnail service, performed in the order a real one performs it: accept encoded bytes,
/// inspect them before trusting them, decode under an explicit budget, resize, write the result out in two
/// formats and read it back. It takes no arguments and synthesises its own input, because both CI jobs that
/// use it invoke it bare — the trimming smoke job runs the published binary directly, and the samples job
/// runs <c>dotnet run --project samples/Thumbnailer -c Release --no-build</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is deliberately reflection-free: no <c>System.Text.Json</c>, no <c>Enum.Parse</c>, no
/// <c>Activator</c>, no custom <see cref="IFormattable"/>. The trimming job publishes this project with
/// <c>TrimmerSingleWarn=false</c> and <c>SuppressTrimAnalysisWarnings=false</c>, which is far stricter than an
/// ordinary build: the SDK suppresses trim analysis by default, and single-warn aggregates one warning per
/// assembly. A project that builds clean can still fail that publish. Any construct ILLink cannot follow
/// surfaces as an <c>IL2xxx</c> warning and, under the <c>TreatWarningsAsErrors</c> inherited from the root
/// Directory.Build.props, fails it outright.
/// </para>
/// <para>
/// Running the binary afterwards matters because publishing only proves the trimmer had nothing to complain
/// about. Executing it proves the codecs still work once the unused parts of the closure have actually been
/// removed, which an analyzer-clean build is necessary for but not sufficient to establish.
/// </para>
/// <para>
/// Every value interpolated into the output below is a non-negative integer, whose "G" formatting carries no
/// group separator and no sign in any culture, so the text this prints is the same on every runner without
/// the sample having to force a culture and thereby drift away from what a real consumer runs.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>The width of the synthesised upload. With the height below this is 3:2, the shape of most camera output.</summary>
    private const int SourceWidth = 2400;

    /// <summary>The height of the synthesised upload.</summary>
    private const int SourceHeight = 1600;

    /// <summary>The quality the synthesised upload is encoded at, high enough to stay recognisably photographic.</summary>
    private const int JpegQuality = 88;

    /// <summary>The width of the box the thumbnail is fitted into.</summary>
    private const int ThumbnailBoxWidth = 320;

    /// <summary>The height of the box the thumbnail is fitted into.</summary>
    private const int ThumbnailBoxHeight = 240;

    /// <summary>
    /// The per-frame decode budget. Generous enough for the synthesised upload and small enough to be a real
    /// limit, because the point of the step is to show what accepting untrusted bytes ought to look like.
    /// </summary>
    private const long MaxDecodedPixels = 40_000_000;

    /// <summary>The frame budget: a thumbnail service has no use for a thousand-page TIFF.</summary>
    private const int MaxDecodedFrames = 8;

    /// <summary>Runs the pipeline and reports the outcome.</summary>
    /// <returns>0 when every assertion held, 1 otherwise.</returns>
    internal static async Task<int> Main()
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), "easyimagesharp-thumbnailer");
        try
        {
            Directory.CreateDirectory(workDirectory);
            await RunAsync(workDirectory).ConfigureAwait(false);
            return 0;
        }
        catch (ThumbnailerFailure failure)
        {
            Console.Error.WriteLine("FAIL: " + failure.Message);
            return 1;
        }
        catch (Exception unexpected)
        {
            // Any escaping exception is a failure of the sample, a decoder exception included: the input is
            // synthesised right here, so nothing in this pipeline is entitled to throw.
            Console.Error.WriteLine("FAIL: unexpected exception" + Environment.NewLine + unexpected);
            return 1;
        }
        finally
        {
            RemoveWorkDirectory(workDirectory);
        }
    }

    /// <summary>Runs the five stages of the pipeline, throwing <see cref="ThumbnailerFailure"/> at the first bad result.</summary>
    /// <param name="workDirectory">The directory the encoded thumbnails are written to and read back from.</param>
    /// <returns>A task that completes once every stage has passed.</returns>
    private static async Task RunAsync(string workDirectory)
    {
        // 1. Stand in for the uploaded bytes. A real service receives these from the network; synthesising
        //    them keeps the sample self-contained and its result independent of any fixture on disk.
        byte[] uploaded = SynthesizeUpload();

        // 2. Inspect before trusting. Identify reads the header only, so a hostile file claiming enormous
        //    dimensions is rejected before a pixel buffer is ever allocated.
        ImageInfo info = Image.Identify(uploaded);
        Check(
            info.Width == SourceWidth && info.Height == SourceHeight,
            $"Identify reported {info.Width}x{info.Height}, expected {SourceWidth}x{SourceHeight}.");
        Check(info.FrameCount == 1, $"Identify reported {info.FrameCount} frames, expected 1.");

        DecoderOptions options = new() { MaxPixels = MaxDecodedPixels, MaxFrames = MaxDecodedFrames };
        using Image<Rgba32> source = Image.Load<Rgba32>(uploaded, options);
        Check(
            source.Width == info.Width && source.Height == info.Height,
            $"the decode produced {source.Width}x{source.Height} but Identify promised {info.Width}x{info.Height}.");

        // 3. Fit inside the thumbnail box without distorting the picture.
        using Image<Rgba32> thumb = source.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(ThumbnailBoxWidth, ThumbnailBoxHeight),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Bicubic,
        }));

        CheckThumbnailGeometry(source, thumb);
        Check(!IsUniform(thumb), "the thumbnail is a single flat colour, so the resize sampled nothing.");

        // 4. Write both encodings out, then read them back off the disk. The round trip through the file
        //    system is the part an in-memory unit test does not cover.
        string pngPath = Path.Combine(workDirectory, "thumb.png");
        string webpPath = Path.Combine(workDirectory, "thumb.webp");

        await using (FileStream pngFile = File.Create(pngPath))
        {
            await thumb.SaveAsPngAsync(pngFile).ConfigureAwait(false);
        }

        thumb.SaveAsWebp(webpPath);

        long pngBytes = new FileInfo(pngPath).Length;
        long webpBytes = new FileInfo(webpPath).Length;
        Check(pngBytes > 0, "the PNG written to disk is empty.");
        Check(webpBytes > 0, "the WebP written to disk is empty.");

        using (Image<Rgba32> reloadedPng = Image.Load<Rgba32>(pngPath))
        {
            Check(
                reloadedPng.Width == thumb.Width && reloadedPng.Height == thumb.Height,
                $"the reloaded PNG is {reloadedPng.Width}x{reloadedPng.Height}, expected {thumb.Width}x{thumb.Height}.");
            Check(
                PixelsEqual(thumb, reloadedPng),
                "the PNG round trip changed pixels; PNG is lossless, so it has to come back exact.");
        }

        using (Image<Rgba32> reloadedWebp = Image.Load<Rgba32>(webpPath))
        {
            Check(
                reloadedWebp.Width == thumb.Width && reloadedWebp.Height == thumb.Height,
                $"the reloaded WebP is {reloadedWebp.Width}x{reloadedWebp.Height}, expected {thumb.Width}x{thumb.Height}.");
        }

        // 5. Report. The shape of this line is what a human scanning a CI log actually reads.
        Console.WriteLine($"Thumbnailer: ok ({thumb.Width}x{thumb.Height}, png {pngBytes} bytes, webp {webpBytes} bytes)");
    }

    /// <summary>
    /// Builds the bytes the pipeline treats as an upload: a synthetic 3:2 photograph encoded as JPEG. The
    /// picture is gradients plus a low-amplitude linear-congruential grain and a coarse checker, which gives
    /// the encoder both smooth ramps and high-frequency detail rather than a flat field that would compress
    /// to almost nothing and exercise almost none of the decoder.
    /// </summary>
    /// <returns>The encoded JPEG bytes.</returns>
    private static byte[] SynthesizeUpload()
    {
        Rgba32[] pixels = new Rgba32[SourceWidth * SourceHeight];
        uint state = 0x2545F491u;

        for (int y = 0; y < SourceHeight; y++)
        {
            int rowStart = y * SourceWidth;
            int vertical = y * 255 / (SourceHeight - 1);

            for (int x = 0; x < SourceWidth; x++)
            {
                state = (state * 1664525u) + 1013904223u;
                int grain = (int)((state >> 24) & 0x1Fu) - 16;
                int horizontal = x * 255 / (SourceWidth - 1);
                int block = (((x >> 6) + (y >> 6)) & 1) == 0 ? 24 : -24;

                pixels[rowStart + x] = new Rgba32(
                    ClampToByte(horizontal + grain),
                    ClampToByte(vertical + block + grain),
                    ClampToByte(((horizontal + vertical) / 2) - block + grain),
                    255);
            }
        }

        using Image<Rgba32> photo = Image<Rgba32>.WrapMemory(pixels, SourceWidth, SourceHeight);
        using MemoryStream buffer = new();
        photo.SaveAsJpeg(buffer, JpegQuality);
        return buffer.ToArray();
    }

    /// <summary>
    /// Asserts the geometry <see cref="ResizeMode.Max"/> is contracted to produce. The expected height is
    /// computed from the source rather than hard-coded, so a change of resampler, or of the rounding inside
    /// the resize planner, cannot turn into a false failure here.
    /// </summary>
    /// <param name="source">The decoded upload.</param>
    /// <param name="thumb">The thumbnail produced from it.</param>
    private static void CheckThumbnailGeometry(Image<Rgba32> source, Image<Rgba32> thumb)
    {
        Check(
            thumb.Width <= ThumbnailBoxWidth && thumb.Height <= ThumbnailBoxHeight,
            $"the thumbnail is {thumb.Width}x{thumb.Height}, which does not fit inside {ThumbnailBoxWidth}x{ThumbnailBoxHeight}.");

        // The source is wider than the box, so Max is bounded by the width and the long edge has to land
        // exactly on it.
        Check(
            thumb.Width == ThumbnailBoxWidth,
            $"the long edge is {thumb.Width}, expected exactly {ThumbnailBoxWidth}.");

        int expectedHeight = (int)Math.Round((double)source.Height * thumb.Width / source.Width);
        Check(
            Math.Abs(thumb.Height - expectedHeight) <= 1,
            $"the thumbnail is {thumb.Width}x{thumb.Height}; the aspect ratio of {source.Width}x{source.Height} wants a height of {expectedHeight}.");
    }

    /// <summary>Reports whether every pixel of the image is the same colour, which a working resize never produces here.</summary>
    /// <param name="image">The image to inspect.</param>
    /// <returns><see langword="true"/> when the image is one flat colour.</returns>
    private static bool IsUniform(Image<Rgba32> image)
    {
        Rgba32[] pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);

        for (int i = 1; i < pixels.Length; i++)
        {
            if (pixels[i] != pixels[0])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Compares two images pixel for pixel.</summary>
    /// <param name="left">The first image.</param>
    /// <param name="right">The second image.</param>
    /// <returns><see langword="true"/> when both images have the same size and identical pixels.</returns>
    private static bool PixelsEqual(Image<Rgba32> left, Image<Rgba32> right)
    {
        if (left.Width != right.Width || left.Height != right.Height)
        {
            return false;
        }

        Rgba32[] leftPixels = new Rgba32[left.Width * left.Height];
        Rgba32[] rightPixels = new Rgba32[right.Width * right.Height];
        left.CopyPixelDataTo(leftPixels);
        right.CopyPixelDataTo(rightPixels);

        return leftPixels.AsSpan().SequenceEqual(rightPixels);
    }

    /// <summary>Throws <see cref="ThumbnailerFailure"/> when the condition does not hold.</summary>
    /// <param name="condition">The condition that has to be true.</param>
    /// <param name="message">The reason reported when it is not.</param>
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new ThumbnailerFailure(message);
        }
    }

    /// <summary>Clamps a value into the 0..255 range of a colour channel.</summary>
    /// <param name="value">The value to clamp.</param>
    /// <returns>The clamped value.</returns>
    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>
    /// Removes the working directory, reporting rather than throwing when it cannot: a cleanup failure after a
    /// successful run must not turn into a failed CI job.
    /// </summary>
    /// <param name="directory">The directory to remove.</param>
    private static void RemoveWorkDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException cleanup)
        {
            Console.Error.WriteLine("warning: could not remove " + directory + ": " + cleanup.Message);
        }
        catch (UnauthorizedAccessException cleanup)
        {
            Console.Error.WriteLine("warning: could not remove " + directory + ": " + cleanup.Message);
        }
    }
}
