using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.AI;

/// <summary>A generic callback over a strongly typed image, used to dispatch on the runtime pixel format.</summary>
internal interface IImageVisitor
{
    void Visit<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>;
}

/// <summary>
/// Lets the <c>IImageProcessingContext</c> extension methods reach the image a <c>Mutate</c> / <c>Clone</c>
/// pipeline is operating on. The core keeps its context class internal and exposes no pixel access through the
/// interface, so this add-on locates the context's image field reflectively (annotated for the trimmer / NativeAOT
/// through <see cref="DynamicDependencyAttribute"/>) and dispatches on the five built-in pixel formats.
/// </summary>
internal static class ProcessingContextBridge
{
    private static readonly ConcurrentDictionary<Type, FieldInfo?> ImageFieldCache = new();

    /// <summary>Returns the image behind a processing context and runs <paramref name="visitor"/> on it with its real pixel type.</summary>
    public static void Dispatch(IImageProcessingContext context, IImageVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(context);
        Image image = GetImage(context);
        switch (image)
        {
            case Image<Rgba32> rgba32:
                visitor.Visit(rgba32);
                break;
            case Image<Rgb24> rgb24:
                visitor.Visit(rgb24);
                break;
            case Image<Bgra32> bgra32:
                visitor.Visit(bgra32);
                break;
            case Image<Bgr24> bgr24:
                visitor.Visit(bgr24);
                break;
            case Image<L8> l8:
                visitor.Visit(l8);
                break;
            default:
                throw new NotSupportedException(
                    $"AI operations on IImageProcessingContext support the built-in pixel formats (Rgba32, Rgb24, Bgra32, Bgr24, L8); " +
                    $"the pipeline image is {image.GetType().Name}. Call the Image<TPixel> overload directly instead.");
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "EasyImageSharp.Processing.ImageProcessingContext`1", "EasyImageSharp")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The pipeline context type and its non-public fields are preserved by the DynamicDependency above; an unexpected context type produces a clear NotSupportedException.")]
    private static Image GetImage(IImageProcessingContext context)
    {
        Type type = context.GetType();
        if (!ImageFieldCache.TryGetValue(type, out FieldInfo? field))
        {
            field = FindImageField(type);
            ImageFieldCache[type] = field;
        }

        if (field?.GetValue(context) is Image image)
        {
            return image;
        }

        throw new NotSupportedException(
            $"Cannot access the image behind processing context '{type.FullName}'. AI operations on IImageProcessingContext " +
            "require the EasyImageSharp pipeline context; call the Image<TPixel> overloads directly instead.");
    }

    private static FieldInfo? FindImageField(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] Type type)
    {
        foreach (FieldInfo candidate in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (typeof(Image).IsAssignableFrom(candidate.FieldType))
            {
                return candidate;
            }
        }

        return null;
    }
}
