namespace EasyImageSharp.AI;

/// <summary>
/// An image-to-image ONNX model that <see cref="ImageAiSession"/> can run through
/// <see cref="ImageModelRunner"/>: where the file comes from plus how to feed it. Implement it (or use
/// <see cref="LocalImageModel"/> / <see cref="RegistryImageModel"/>) to plug your own networks into the same
/// session cache, execution provider and tiling machinery the built-in operations use.
/// </summary>
public interface IImageModel
{
    /// <summary>A short name for diagnostics and session-cache keys.</summary>
    string Name { get; }

    /// <summary>How to feed the model and read its output.</summary>
    ImageModelContract Contract { get; }

    /// <summary>Resolves the local ONNX file (downloading / verifying through the hub when applicable).</summary>
    Task<string> ResolveModelPathAsync(ModelHub hub, CancellationToken cancellationToken);
}

/// <summary>An image-to-image model stored in a local ONNX file (never downloaded or verified).</summary>
public sealed class LocalImageModel : IImageModel
{
    /// <summary>Creates a model over a local file with the given contract.</summary>
    public LocalImageModel(string modelPath, ImageModelContract contract, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(contract);
        this.ModelPath = Path.GetFullPath(modelPath);
        this.Contract = contract;
        this.Name = name ?? Path.GetFileNameWithoutExtension(modelPath);
    }

    /// <summary>Absolute path of the ONNX file.</summary>
    public string ModelPath { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ImageModelContract Contract { get; }

    /// <inheritdoc />
    public Task<string> ResolveModelPathAsync(ModelHub hub, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(this.ModelPath))
        {
            throw new FileNotFoundException($"Model file '{this.ModelPath}' does not exist.", this.ModelPath);
        }

        return Task.FromResult(this.ModelPath);
    }
}

/// <summary>An image-to-image model described by a <see cref="ModelDescriptor"/> (downloaded through the hub).</summary>
public sealed class RegistryImageModel : IImageModel
{
    /// <summary>Creates a model over a registry descriptor with the given contract.</summary>
    public RegistryImageModel(ModelDescriptor descriptor, ImageModelContract contract)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(contract);
        this.Descriptor = descriptor;
        this.Contract = contract;
    }

    /// <summary>The descriptor (file names, checksums, source URL).</summary>
    public ModelDescriptor Descriptor { get; }

    /// <inheritdoc />
    public string Name => this.Descriptor.Name;

    /// <inheritdoc />
    public ImageModelContract Contract { get; }

    /// <inheritdoc />
    public Task<string> ResolveModelPathAsync(ModelHub hub, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.EnsureModelAsync(this.Descriptor, cancellationToken);
    }
}

/// <summary>Ready-made <see cref="IImageModel"/>s for the registry's image-to-image networks with their contracts.</summary>
public static class ImageModels
{
    /// <summary>Real-ESRGAN x4 (RGB 0-1 in, RGB 0-1 out, x4, tiled 256 / 16).</summary>
    public static RegistryImageModel SuperResolutionX4 { get; } = new(ModelRegistry.SuperResolutionX4, new ImageModelContract
    {
        InputName = ModelRegistry.SuperResolutionX4.InputName,
        InputChannels = 3,
        InputNormalization = TensorNormalization.Unit,
        OutputNormalization = TensorNormalization.Unit,
        ScaleFactor = 4,
        TileSize = 256,
        TileOverlap = 16,
    });

    /// <summary>DnCNN gray (luminance 0-1 in, noise residual out, tiled 256 / 16).</summary>
    public static RegistryImageModel DenoiseGray { get; } = new(ModelRegistry.DenoiseGray, new ImageModelContract
    {
        InputName = ModelRegistry.DenoiseGray.InputName,
        InputChannels = 1,
        InputNormalization = TensorNormalization.Unit,
        OutputNormalization = TensorNormalization.Unit,
        OutputKind = ImageModelOutputKind.Residual,
        ScaleFactor = 1,
        TileSize = 256,
        TileOverlap = 16,
    });

    /// <summary>UVDoc dewarp (RGB 0-1 at 488x712 in, rectified RGB 0-1 out, resized back to the source size).</summary>
    public static RegistryImageModel DocumentDewarp { get; } = new(ModelRegistry.DocumentDewarp, new ImageModelContract
    {
        InputName = ModelRegistry.DocumentDewarp.InputName,
        InputChannels = 3,
        InputNormalization = TensorNormalization.Unit,
        OutputNormalization = TensorNormalization.Unit,
        ScaleFactor = 1,
        FixedInputSize = new Size(488, 712),
    });
}
