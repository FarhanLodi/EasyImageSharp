using System.Numerics;
using EasyImageSharp;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace AotSmoke;

internal static class AdvancedDocumentChecks
{
    internal static void RunAll(Report report)
    {
        report.Record("direct reduced JPEG decoder", static () =>
        {
            using var image = new Image<Rgb24>(37, 23, new Rgb24(100, 40, 20));
            byte[] encoded = Convert.FromBase64String(image.ToBase64String(ImageFormat.Jpeg));
            using var reduced = new EasyImageSharp.Formats.Jpeg.JpegDecoder().DecodeReduced<Rgb24>(encoded, 4);
            return reduced.Width == 10 && reduced.Height == 6 && Math.Abs(reduced[0, 0].R - 100) < 5
                ? null : "reduced dimensions or constant-color reconstruction failed";
        });

        report.Record("seekable TIFF region decoder", static () =>
        {
            using var image = new Image<Rgb24>(37, 23, new Rgb24(100, 40, 20));
            using var stream = new MemoryStream(Convert.FromBase64String(image.ToBase64String(ImageFormat.Tiff)));
            using var reader = Image.OpenTiffRegionReader(stream);
            using var region = reader.ReadRegion<Rgb24>(new EasyImageSharp.Formats.Tiff.TiffRegion(5, 7, 8, 9));
            return region.Width == 8 && region.Height == 9 && region[0, 0].R == 100
                ? null : "region dimensions or selected pixels failed";
        });

        report.Record("adaptive profile diagnostics and coordinate interface", static () =>
        {
            using var image = new Image<Rgb24>(40, 30, new Rgb24(255, 255, 255));
            using DocumentPreprocessingResult<Rgb24> result = image.PreprocessDocument(new()
            {
                Profile = DocumentPreprocessingProfile.PhoneDocument,
                ExecutionOptions = new() { Configuration = new() { MaxDegreeOfParallelism = 1 } },
            });
            DocumentPagePreprocessingResult page = result.Pages[0];
            if (!page.Analysis.IsLikelyBlank || page.Analysis.MeanLuminance < 0.99
                || page.Stages.Any(stage => (stage.Name is "Orient" or "Perspective") && stage.Applied))
            {
                return "blank page diagnostics or abstention failed";
            }

            if (!page.Coordinates.TryMapToProcessed(new PointF(12.5f, 8.5f), out PointF processed)
                || !page.Coordinates.TryMapToSource(processed, out PointF original)
                || original.X != 12.5f || original.Y != 8.5f)
            {
                return "bidirectional coordinate-map dispatch failed";
            }

            result.Image[0, 0] = new Rgb24(0, 0, 0);
            return image[0, 0].R == 255 ? null : "profile output aliases source pixels";
        });

        report.Record("color reference red and Lab round trip", static () =>
        {
            Vector3 xyz = ColorSpaceConverter.SrgbToXyz(new Vector3(1, 0, 0));
            if (Vector3.Distance(xyz, new Vector3(0.4123908f, 0.212639f, 0.01933082f)) > 0.000001f)
            {
                return "red D65 XYZ disagrees with reference";
            }

            Vector3 rgb = new(0.17f, 0.48f, 0.83f);
            Vector3 roundTrip = ColorSpaceConverter.XyzToSrgb(ColorSpaceConverter.LabToXyz(ColorSpaceConverter.XyzToLab(ColorSpaceConverter.SrgbToXyz(rgb))));
            return Vector3.Distance(rgb, roundTrip) < 0.00001f ? null : "Lab conversion lost round-trip precision";
        });

        report.Record("bounded incremental PNG header consumes only header", static () =>
        {
            using var image = new Image<Rgb24>(37, 23, new Rgb24(100, 40, 20));
            using var stream = new MemoryStream(Convert.FromBase64String(image.ToBase64String(ImageFormat.Png)));
            ImageHeaderInfo header = Image.IdentifyHeader(stream, 64);
            return header.Width == 37 && header.Height == 23 && header.Format == ImageFormat.Png
                && stream.Position <= 64 && stream.Position < stream.Length && stream.CanRead
                ? null : "incremental header dimensions, budget, consumption or ownership failed";
        });

        report.Record("request-local profile allocation and cancellation limits", static () =>
        {
            using var image = new Image<L8>(20, 10, new L8(255));
            try
            {
                using var unexpected = image.PreprocessDocument(new() { ExecutionOptions = new() { MaxPixelBufferBytes = 1 } });
                return "pixel allocation budget was not enforced";
            }
            catch (ImageSizeLimitExceededException)
            {
            }

            try
            {
                using var unexpected = image.PreprocessDocument(new() { ExecutionOptions = new() { CancellationToken = new CancellationToken(true) } });
                return "request cancellation was not enforced";
            }
            catch (OperationCanceledException)
            {
            }

            using var valid = image.PreprocessDocument();
            return valid.Image.Width == 20 ? null : "failed scope contaminated a later operation";
        });
    }
}
