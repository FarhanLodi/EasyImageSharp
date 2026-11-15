using EasyImageSharp.Diagnostics;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp;

public abstract partial class Image
{
    /// <summary>Buffers bounded TIFF input, then decodes pages lazily. The input stream remains open; dispose each yielded page.</summary>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(Stream stream)
        where TPixel : unmanaged, IPixel<TPixel> => LoadTiffPages<TPixel>(stream, DecoderOptions.Default);

    /// <summary>Buffers bounded TIFF input, then decodes pages lazily using the specified policy.</summary>
    /// <remarks>
    /// <see cref="DecoderOptions.ColorProfileHandling"/> applies to every page as it is decoded, each page converting
    /// from its own embedded profile.
    /// </remarks>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(Stream stream, DecoderOptions? options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        options ??= DecoderOptions.Default;

        // Each page is measured from the moment the caller asks for it until it is handed over. Pages produce no
        // activity, because the work for one page is split across two calls into the caller's loop.
        OperationTelemetry telemetry = OperationTelemetry.Start<TPixel>(TelemetryOperation.LoadPage);
        telemetry.SetFormat(ImageFormat.Tiff.Name);
        ReadOnlyMemory<byte> bytes;
        IEnumerator<Image<TPixel>> pages;
        try
        {
            bytes = BoundedImageInput.Read(stream, options);
            pages = new TiffDecoder().DecodePages<TPixel>(bytes, options).GetEnumerator();
        }
        catch (Exception ex) when (telemetry.Fail(ex))
        {
            // Unreachable: Fail always returns false.
            throw;
        }

        using (pages)
        {
            bool first = true;
            while (true)
            {
                Image<TPixel>? page;
                try
                {
                    page = pages.MoveNext() ? ConvertPage(pages.Current, options) : null;
                    if (page is not null)
                    {
                        telemetry.CompleteDecode(page, bytes.Length, countEncodedBytes: first);
                    }
                }
                catch (Exception ex) when (telemetry.Fail(ex))
                {
                    // Unreachable: Fail always returns false.
                    throw;
                }

                if (page is null)
                {
                    yield break;
                }

                first = false;
                yield return page;
                telemetry = OperationTelemetry.Start<TPixel>(TelemetryOperation.LoadPage);
                telemetry.SetFormat(ImageFormat.Tiff.Name);
            }
        }
    }

    /// <summary>
    /// Applies the colour profile policy to one decoded page under the request's execution options: the page decoder
    /// leaves its own execution scope before handing the page over, so the conversion enters one for itself.
    /// </summary>
    private static Image<TPixel> ConvertPage<TPixel>(Image<TPixel> page, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (options.ColorProfileHandling == ColorSpaces.ColorProfileHandling.Preserve)
        {
            return page;
        }

        try
        {
            using var execution = OperationExecution.Enter(options.ExecutionOptions);
            return ColorManagementOps.ApplyOnLoad(page, options);
        }
        catch
        {
            // ApplyOnLoad disposes the page when the conversion itself fails; entering the scope can fail first.
            page.Dispose();
            throw;
        }
    }

    /// <summary>Buffers bounded TIFF input, then decodes pages lazily. Dispose each yielded page.</summary>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(string path)
        where TPixel : unmanaged, IPixel<TPixel> => LoadTiffPages<TPixel>(path, DecoderOptions.Default);

    /// <summary>Buffers bounded TIFF input, then decodes pages lazily using the specified policy.</summary>
    public static IEnumerable<Image<TPixel>> LoadTiffPages<TPixel>(string path, DecoderOptions? options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using FileStream stream = File.OpenRead(path);
        foreach (Image<TPixel> page in LoadTiffPages<TPixel>(stream, options)) yield return page;
    }
}
