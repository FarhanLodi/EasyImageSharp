using Microsoft.Extensions.DependencyInjection;

namespace EasyImageSharp.Web.Internal;

/// <summary>The <see cref="IEasyImageSharpWebBuilder"/> returned by <c>AddEasyImageSharpWeb</c>.</summary>
internal sealed class EasyImageSharpWebBuilder : IEasyImageSharpWebBuilder
{
    public EasyImageSharpWebBuilder(IServiceCollection services) => this.Services = services;

    public IServiceCollection Services { get; }
}
