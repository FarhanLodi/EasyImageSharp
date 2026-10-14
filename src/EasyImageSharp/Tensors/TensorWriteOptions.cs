namespace EasyImageSharp.Tensors;

/// <summary>The ordering of dimensions in a tensor.</summary>
public enum TensorLayout
{
    /// <summary>Contiguous pixels within each channel plane.</summary>
    Chw,
    /// <summary>Contiguous channels within each pixel.</summary>
    Hwc,
}

/// <summary>The channels written to a tensor. Alpha is ignored.</summary>
public enum TensorChannels
{
    /// <summary>Red, green, blue.</summary>
    Rgb,
    /// <summary>Blue, green, red.</summary>
    Bgr,
    /// <summary>BT.709 luminance.</summary>
    Grayscale,
}

/// <summary>
/// Describes where image samples are written. Strides and offsets are measured in floats.
/// Only image samples are written; padding, gaps, and other batch entries remain untouched.
/// </summary>
public sealed class TensorWriteOptions
{
    /// <summary>Optional request-local execution scope, including cooperative cancellation. Null inherits the active scope.</summary>
    public ImageExecutionOptions? ExecutionOptions { get; init; }

    /// <summary>Gets or sets dimension ordering. Defaults to CHW.</summary>
    public TensorLayout Layout { get; init; }

    /// <summary>Gets or sets output channel order. Defaults to RGB.</summary>
    public TensorChannels Channels { get; init; }

    /// <summary>Gets or sets the first output element, allowing insertion into a batch.</summary>
    public int DestinationOffset { get; init; }

    /// <summary>Gets or sets row spacing. Zero means tightly packed rows.</summary>
    public int RowStride { get; init; }

    /// <summary>
    /// Gets or sets channel-plane spacing for CHW. Zero means RowStride times image height.
    /// Must be zero for HWC.
    /// </summary>
    public int PlaneStride { get; init; }
}
