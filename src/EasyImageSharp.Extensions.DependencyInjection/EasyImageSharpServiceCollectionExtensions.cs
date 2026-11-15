using EasyImageSharp;
using EasyImageSharp.Extensions.DependencyInjection;
using EasyImageSharp.Extensions.DependencyInjection.Internal;
using EasyImageSharp.Formats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// Extension methods for IServiceCollection conventionally live in this namespace so they are found without an
// extra using directive.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers EasyImageSharp's options and factories with an <see cref="IServiceCollection"/>.</summary>
public static class EasyImageSharpServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EasyImageSharpOptions"/> (validated on start), <see cref="IImageOptionsFactory"/>, and
    /// transient <see cref="DecoderOptions"/> and <see cref="ImageExecutionOptions"/> built from the current options.
    /// Safe to call more than once; later calls add nothing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// The transient <see cref="DecoderOptions"/> and <see cref="ImageExecutionOptions"/> carry no cancellation
    /// token. In request-scoped code prefer <see cref="IImageOptionsFactory"/> and pass the request's token.
    /// </remarks>
    public static IServiceCollection AddEasyImageSharp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<EasyImageSharpOptions>().ValidateOnStart();
        services.AddOptions<ImageFormatRegistrations>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EasyImageSharpOptions>, EasyImageSharpOptionsValidator>());
        services.TryAddSingleton<IImageOptionsFactory, ImageOptionsFactory>();
        services.TryAddTransient(static provider => provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions());
        services.TryAddTransient(static provider => provider.GetRequiredService<IImageOptionsFactory>().CreateExecutionOptions());
        return services;
    }

    /// <summary>Registers the EasyImageSharp services and configures <see cref="EasyImageSharpOptions"/> in code.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Applies settings to the options; runs after any configuration binding registered earlier.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddEasyImageSharp(this IServiceCollection services, Action<EasyImageSharpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddEasyImageSharp();
        services.Configure(configure);
        return services;
    }

    /// <summary>
    /// Registers the EasyImageSharp services and binds <see cref="EasyImageSharpOptions"/> from
    /// <paramref name="configuration"/>, typically <c>builder.Configuration.GetSection("EasyImageSharp")</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The section whose keys (<c>MaxPixels</c>, <c>MaxTotalPixels</c>, <c>MaxFrames</c>, <c>MaxEncodedBytes</c>,
    /// <c>MaxPixelBufferBytes</c>, <c>MaxDegreeOfParallelism</c>, <c>MinimumPixelsPerTask</c>) are bound. Binding is
    /// generated at compile time, so it is trim and Native AOT safe, and it follows configuration reloads.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddEasyImageSharp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddEasyImageSharp();
        services.AddOptions<EasyImageSharpOptions>().Bind(configuration);
        return services;
    }

    /// <summary>
    /// Registers the EasyImageSharp services and adds an opt-in format provider (see
    /// <see cref="ImageFormat.Create"/>) to the <see cref="DecoderOptions.AdditionalFormats"/> of every
    /// <see cref="DecoderOptions"/> created from the container. Adding the same instance twice has no effect.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="format">The format provider to consider before the built-in formats.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddEasyImageSharpFormat(this IServiceCollection services, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        services.AddEasyImageSharp();
        services.Configure<ImageFormatRegistrations>(registrations => registrations.Add(format));
        return services;
    }
}
