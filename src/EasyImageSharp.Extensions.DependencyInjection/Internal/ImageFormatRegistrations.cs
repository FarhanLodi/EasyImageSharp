using EasyImageSharp.Formats;

namespace EasyImageSharp.Extensions.DependencyInjection.Internal;

/// <summary>
/// The custom format providers added with <c>services.AddEasyImageSharpFormat(...)</c>. Kept apart from
/// <see cref="EasyImageSharpOptions"/> on purpose: an <see cref="ImageFormat"/> cannot come from configuration,
/// and keeping it out of the bound type keeps that type fully supported by the binding source generator.
/// </summary>
internal sealed class ImageFormatRegistrations
{
    private readonly List<ImageFormat> formats = new();

    /// <summary>The registered formats, in registration order, without duplicates.</summary>
    public IReadOnlyList<ImageFormat> Formats => this.formats;

    public void Add(ImageFormat format)
    {
        if (!this.formats.Contains(format))
        {
            this.formats.Add(format);
        }
    }
}
