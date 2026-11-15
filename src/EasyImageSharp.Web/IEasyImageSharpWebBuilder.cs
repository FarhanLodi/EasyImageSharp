using Microsoft.Extensions.DependencyInjection;

namespace EasyImageSharp.Web;

/// <summary>
/// Returned by <c>services.AddEasyImageSharpWeb(...)</c> to add source providers, output formats and a server-side
/// cache; see <see cref="EasyImageSharpWebBuilderExtensions"/>.
/// </summary>
public interface IEasyImageSharpWebBuilder
{
    /// <summary>The service collection the middleware's services are registered in.</summary>
    IServiceCollection Services { get; }
}
