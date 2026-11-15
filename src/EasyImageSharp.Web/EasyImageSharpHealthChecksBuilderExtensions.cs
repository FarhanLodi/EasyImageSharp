using EasyImageSharp.Web;
using EasyImageSharp.Web.Internal;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// Extension methods for IHealthChecksBuilder conventionally live in this namespace so they are found without an
// extra using directive.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the EasyImageSharp image middleware's health check.</summary>
public static class EasyImageSharpHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a health check that runs a tiny in-memory decode, resize and encode, probes the disk cache directory when
    /// <c>UsePhysicalCache</c> is used, and reports <see cref="HealthStatus.Degraded"/> while the concurrency limiter is
    /// saturated. Its data lists the permit and queue limits and current usage. Requires
    /// <c>services.AddEasyImageSharpWeb(...)</c>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The check's name. Defaults to <c>"easyimagesharp"</c>.</param>
    /// <param name="failureStatus">The status reported on failure, or <see langword="null"/> for <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Tags for filtering checks, or <see langword="null"/>.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IHealthChecksBuilder AddEasyImageSharp(
        this IHealthChecksBuilder builder,
        string name = "easyimagesharp",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.Add(new HealthCheckRegistration(
            name,
            static provider => new EasyImageSharpHealthCheck(
                provider.GetService<ImageProcessingLimiter>()
                    ?? throw new InvalidOperationException("The EasyImageSharp health check requires services.AddEasyImageSharpWeb(...)."),
                provider.GetService<IImageCache>()),
            failureStatus,
            tags));
    }
}
