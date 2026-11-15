using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// Reports whether the image middleware can do its job: a tiny decode-resize-encode round trip must succeed, the disk
/// cache (when used) must accept a write, and the concurrency limiter must not be saturated (degraded when every slot
/// is busy and the queue is full, which is exactly when requests are being answered with 503).
/// </summary>
internal sealed class EasyImageSharpHealthCheck : IHealthCheck
{
    private readonly ImageProcessingLimiter limiter;
    private readonly IImageCache? cache;

    public EasyImageSharpHealthCheck(ImageProcessingLimiter limiter, IImageCache? cache)
    {
        this.limiter = limiter;
        this.cache = cache;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        (long available, long queued) = this.limiter.GetCounts();
        var data = new Dictionary<string, object>
        {
            ["permitLimit"] = this.limiter.PermitLimit,
            ["queueLimit"] = this.limiter.QueueLimit,
            ["availablePermits"] = available,
            ["queued"] = queued,
        };

        try
        {
            RoundTrip();
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "The image codec round trip failed.", ex, data));
        }

        if (this.cache is PhysicalImageCache physical)
        {
            data["cacheRoot"] = physical.RootPath;
            try
            {
                physical.Probe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "The image cache directory is not writable.", ex, data));
            }
        }

        if (available == 0 && queued >= this.limiter.QueueLimit)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Image processing is saturated; new requests are answered with 503.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Image processing is available.", data));
    }

    /// <summary>Encodes, decodes and resizes an 8 x 8 image entirely in memory.</summary>
    private static void RoundTrip()
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(32, 96, 160, 255));
        using var encoded = new MemoryStream();
        image.SaveAsPng(encoded);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded.GetBuffer().AsSpan(0, (int)encoded.Length));
        decoded.Mutate(ctx => ctx.Resize(4, 4));
        if (decoded.Width != 4 || decoded.Height != 4)
        {
            throw new InvalidOperationException("The round trip produced an image of the wrong size.");
        }
    }
}
