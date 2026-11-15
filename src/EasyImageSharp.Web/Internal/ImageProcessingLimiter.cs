using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// Bounds how many images are decoded and processed at once, with a bounded first-in-first-out wait queue. Sized
/// once from <see cref="EasyImageSharpWebOptions.MaxConcurrentProcessing"/> and
/// <see cref="EasyImageSharpWebOptions.MaxQueueLength"/>; a request that finds both full gets a lease that is not
/// acquired and is answered with 503.
/// </summary>
internal sealed class ImageProcessingLimiter : IDisposable
{
    private readonly ConcurrencyLimiter limiter;

    public ImageProcessingLimiter(IOptions<EasyImageSharpWebOptions> options)
    {
        EasyImageSharpWebOptions value = options.Value;
        this.PermitLimit = value.MaxConcurrentProcessing;
        this.QueueLimit = value.MaxQueueLength;
        this.limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = this.PermitLimit,
            QueueLimit = this.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    public int PermitLimit { get; }

    public int QueueLimit { get; }

    /// <summary>Waits for a processing slot; cancelling <paramref name="cancellationToken"/> leaves the queue.</summary>
    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
        => this.limiter.AcquireAsync(1, cancellationToken);

    /// <summary>The slots currently free and the requests currently waiting.</summary>
    public (long Available, long Queued) GetCounts()
    {
        RateLimiterStatistics? statistics = this.limiter.GetStatistics();
        return statistics is null ? (this.PermitLimit, 0) : (statistics.CurrentAvailablePermits, statistics.CurrentQueuedCount);
    }

    public void Dispose() => this.limiter.Dispose();
}
