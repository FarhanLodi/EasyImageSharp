using System.Diagnostics.CodeAnalysis;
using EasyImageSharp.Web.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyImageSharp.Web;

/// <summary>Configures the image middleware's sources, output formats and server-side cache.</summary>
public static class EasyImageSharpWebBuilderExtensions
{
    /// <summary>
    /// Adds a source provider instance. Providers are consulted in registration order; registering any provider
    /// replaces the default one over <see cref="EasyImageSharpWebOptions.SourceRootPath"/> or the web root (add a
    /// <see cref="FileProviderImageSourceProvider"/> explicitly to keep files as well).
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="provider">The provider.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IEasyImageSharpWebBuilder AddSourceProvider(this IEasyImageSharpWebBuilder builder, IImageSourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(provider);
        builder.Services.AddSingleton(provider);
        return builder;
    }

    /// <summary>Adds a source provider type, created once by the container. See <see cref="AddSourceProvider(IEasyImageSharpWebBuilder, IImageSourceProvider)"/>.</summary>
    /// <typeparam name="TProvider">The provider type.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IEasyImageSharpWebBuilder AddSourceProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(
        this IEasyImageSharpWebBuilder builder)
        where TProvider : class, IImageSourceProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IImageSourceProvider, TProvider>();
        return builder;
    }

    /// <summary>
    /// Adds an output format, for example an AVIF or JPEG XL encoder. It replaces a built-in format of the same name.
    /// Allow it in <see cref="EasyImageSharpWebOptions.AllowedFormats"/> before requests can use it.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="format">The format.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IEasyImageSharpWebBuilder AddOutputFormat(this IEasyImageSharpWebBuilder builder, IImageOutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(format);
        builder.Services.AddSingleton(format);
        return builder;
    }

    /// <summary>
    /// Caches processed images as files under <paramref name="cacheRootPath"/>, written atomically (temporary file and
    /// rename). A relative path is resolved against the content root. Keep the directory outside the web root.
    /// Replaces any cache chosen earlier.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="cacheRootPath">The cache directory; created on first write.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IEasyImageSharpWebBuilder UsePhysicalCache(this IEasyImageSharpWebBuilder builder, string cacheRootPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRootPath);
        builder.Services.Replace(ServiceDescriptor.Singleton<IImageCache>(provider =>
        {
            string root = cacheRootPath;
            if (!Path.IsPathRooted(root) && provider.GetService<IHostEnvironment>() is IHostEnvironment environment)
            {
                root = Path.Combine(environment.ContentRootPath, root);
            }

            return new PhysicalImageCache(
                root,
                provider.GetRequiredService<IOptionsMonitor<EasyImageSharpWebOptions>>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<PhysicalImageCache>>());
        }));
        return builder;
    }

    /// <summary>
    /// Caches processed images in the application's <see cref="IDistributedCache"/> (register one, for example
    /// <c>AddDistributedMemoryCache</c> or a Redis cache, separately). Replaces any cache chosen earlier.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IEasyImageSharpWebBuilder UseDistributedCache(this IEasyImageSharpWebBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IImageCache>(static provider => new DistributedImageCache(
            provider.GetRequiredService<IDistributedCache>(),
            provider.GetRequiredService<IOptionsMonitor<EasyImageSharpWebOptions>>(),
            provider.GetRequiredService<TimeProvider>())));
        return builder;
    }

    /// <summary>Uses a custom <see cref="IImageCache"/> implementation, created once by the container. Replaces any cache chosen earlier.</summary>
    /// <typeparam name="TCache">The cache type.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IEasyImageSharpWebBuilder UseCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCache>(
        this IEasyImageSharpWebBuilder builder)
        where TCache : class, IImageCache
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IImageCache, TCache>());
        return builder;
    }
}
