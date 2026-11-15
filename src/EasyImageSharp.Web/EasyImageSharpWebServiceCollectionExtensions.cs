using EasyImageSharp.Extensions.DependencyInjection;
using EasyImageSharp.Web;
using EasyImageSharp.Web.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// Extension methods for IServiceCollection conventionally live in this namespace so they are found without an
// extra using directive.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the EasyImageSharp image middleware's services.</summary>
public static class EasyImageSharpWebServiceCollectionExtensions
{
    /// <summary>
    /// Registers the middleware's services: <see cref="EasyImageSharpWebOptions"/> (validated on start), the core
    /// <see cref="EasyImageSharpOptions"/> through <c>AddEasyImageSharp()</c>, the concurrency limiter, and a transient
    /// <see cref="ImageUrlSigner"/> over the first signing key. Add the middleware with <c>app.UseEasyImageSharp()</c>.
    /// Safe to call more than once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A builder for sources, output formats and caching.</returns>
    public static IEasyImageSharpWebBuilder AddEasyImageSharpWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddEasyImageSharp();
        services.AddLogging();
        services.AddOptions<EasyImageSharpWebOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EasyImageSharpWebOptions>, EasyImageSharpWebOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ImageProcessingLimiter>();
        services.TryAddTransient(static provider =>
        {
            EasyImageSharpWebOptions options = provider.GetRequiredService<IOptionsMonitor<EasyImageSharpWebOptions>>().CurrentValue;
            return options.SigningKeys.Count > 0
                ? ImageUrlSigner.FromBase64(options.SigningKeys[0])
                : throw new InvalidOperationException(
                    "ImageUrlSigner needs at least one entry in EasyImageSharpWebOptions.SigningKeys.");
        });

        return new EasyImageSharpWebBuilder(services);
    }

    /// <summary>Registers the middleware's services and configures <see cref="EasyImageSharpWebOptions"/> in code.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Applies settings; runs after any configuration binding registered earlier.</param>
    /// <returns>A builder for sources, output formats and caching.</returns>
    public static IEasyImageSharpWebBuilder AddEasyImageSharpWeb(this IServiceCollection services, Action<EasyImageSharpWebOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        IEasyImageSharpWebBuilder builder = services.AddEasyImageSharpWeb();
        services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Registers the middleware's services and binds <see cref="EasyImageSharpWebOptions"/> from
    /// <paramref name="configuration"/>, typically <c>builder.Configuration.GetSection("EasyImageSharp:Web")</c>.
    /// Binding is generated at compile time (trim and Native AOT safe) and follows configuration reloads.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section to bind.</param>
    /// <returns>A builder for sources, output formats and caching.</returns>
    public static IEasyImageSharpWebBuilder AddEasyImageSharpWeb(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IEasyImageSharpWebBuilder builder = services.AddEasyImageSharpWeb();
        services.AddOptions<EasyImageSharpWebOptions>().Bind(configuration);
        return builder;
    }
}
