using EasyImageSharp.Web.Internal;
using Microsoft.Extensions.DependencyInjection;

// Extension methods for IApplicationBuilder conventionally live in this namespace so they are found without an
// extra using directive.
namespace Microsoft.AspNetCore.Builder;

/// <summary>Adds the EasyImageSharp image middleware to an ASP.NET Core pipeline.</summary>
public static class EasyImageSharpApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the image middleware. Place it before <c>UseStaticFiles()</c>: requests with processing commands are
    /// answered here, and everything else (originals, other files, other endpoints) passes through untouched.
    /// Requires <c>services.AddEasyImageSharpWeb(...)</c>.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns><paramref name="app"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The services were not registered with <c>AddEasyImageSharpWeb</c>.</exception>
    public static IApplicationBuilder UseEasyImageSharp(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        IServiceProvider services = app.ApplicationServices;
        if (services.GetService<ImageProcessingLimiter>() is null)
        {
            throw new InvalidOperationException(
                "UseEasyImageSharp requires services.AddEasyImageSharpWeb(...) in the application's service registration.");
        }

        return app.Use(next => new EasyImageSharpMiddleware(next, services).InvokeAsync);
    }
}
