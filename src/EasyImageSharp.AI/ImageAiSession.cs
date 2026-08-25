using System.Collections.Concurrent;
using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// The entry point of the add-on: owns a <see cref="ModelHub"/> (download / cache / verify) and a cache of
/// lazily created ONNX Runtime <see cref="InferenceSession"/>s, one per model file, shared by every operation
/// and every image. Create one per application (or per distinct configuration), pass it to the
/// <c>Image</c> / <c>IImageProcessingContext</c> extension methods, and dispose it on shutdown.
/// Thread-safe: sessions can be used from many threads at once.
/// </summary>
public sealed class ImageAiSession : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<InferenceSession>>> sessions = new(StringComparer.Ordinal);
    private readonly Lazy<ExecutionProvider> resolvedProvider;
    private ExecutionProvider? activeProvider;
    private bool disposed;

    /// <summary>Creates a session with default options (Auto provider, default cache, online).</summary>
    public ImageAiSession()
        : this(new ImageAiOptions())
    {
    }

    /// <summary>Creates a session over a snapshot of <paramref name="options"/> (later edits to the instance are ignored).</summary>
    public ImageAiSession(ImageAiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Options = options.Clone();
        this.Hub = new ModelHub(this.Options);
        this.resolvedProvider = new Lazy<ExecutionProvider>(
            () => ExecutionProviderResolver.Resolve(this.Options.ExecutionProvider, this.Options.Log),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The effective (snapshotted) options.</summary>
    public ImageAiOptions Options { get; }

    /// <summary>The model hub used to resolve model files.</summary>
    public ModelHub Hub { get; }

    /// <summary>
    /// The execution provider actually in use: <see cref="ExecutionProvider.Auto"/> resolved against the loaded
    /// runtime, downgraded to <see cref="ExecutionProvider.Cpu"/> if attaching the accelerator failed. Before the
    /// first inference session is created this reports the resolved candidate.
    /// </summary>
    public ExecutionProvider ActiveProvider => this.activeProvider ?? this.resolvedProvider.Value;

    /// <summary>Number of inference sessions currently loaded.</summary>
    public int LoadedSessionCount => this.sessions.Count;

    // ----- Warm-up -----

    /// <summary>
    /// Downloads (if needed) and loads every registry model that can be resolved with the current options, so the
    /// first call of each operation pays no cold-start cost. Placeholders without a checksum or override are skipped.
    /// </summary>
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
        => this.WarmUpAsync(ModelRegistry.All.Where(this.Hub.CanResolve), cancellationToken);

    /// <summary>Downloads (if needed) and loads the given models.</summary>
    public async Task WarmUpAsync(IEnumerable<ModelDescriptor> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        foreach (ModelDescriptor descriptor in models)
        {
            await this.GetSessionAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }

    // ----- Session access -----

    /// <summary>Returns the (cached) inference session for a registry model, resolving the file through the hub first.</summary>
    public async Task<InferenceSession> GetSessionAsync(ModelDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        this.ThrowIfDisposed();
        string path = await this.Hub.EnsureModelAsync(descriptor, cancellationToken).ConfigureAwait(false);
        return await this.GetSessionAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="GetSessionAsync(ModelDescriptor, CancellationToken)"/>.</summary>
    public InferenceSession GetSession(ModelDescriptor descriptor, CancellationToken cancellationToken = default)
        => this.GetSessionAsync(descriptor, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Returns the (cached) inference session for a local ONNX file (your own model; never downloaded or verified).</summary>
    public async Task<InferenceSession> GetSessionAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        this.ThrowIfDisposed();
        string key = Path.GetFullPath(modelPath);
        if (!File.Exists(key))
        {
            throw new FileNotFoundException($"Model file '{key}' does not exist.", key);
        }

        Lazy<Task<InferenceSession>> lazy = this.sessions.GetOrAdd(
            key,
            k => new Lazy<Task<InferenceSession>>(() => Task.Run(() => this.CreateSession(k), CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Let a later call retry instead of caching the failure forever.
            this.sessions.TryRemove(new KeyValuePair<string, Lazy<Task<InferenceSession>>>(key, lazy));
            throw;
        }
    }

    /// <summary>Synchronous <see cref="GetSessionAsync(string, CancellationToken)"/>.</summary>
    public InferenceSession GetSession(string modelPath, CancellationToken cancellationToken = default)
        => this.GetSessionAsync(modelPath, cancellationToken).GetAwaiter().GetResult();

    /// <summary>Returns the (cached) inference session for an <see cref="IImageModel"/>.</summary>
    public async Task<InferenceSession> GetSessionAsync(IImageModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.ThrowIfDisposed();
        string path = await model.ResolveModelPathAsync(this.Hub, cancellationToken).ConfigureAwait(false);
        return await this.GetSessionAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // ----- Generic image-to-image -----

    /// <summary>Runs an image through your own ONNX model (local file) using the given contract and returns the result image.</summary>
    public async Task<Image<TPixel>> RunImageToImageAsync<TPixel>(
        string modelPath, Image<TPixel> image, ImageModelContract contract, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(contract);
        InferenceSession session = await this.GetSessionAsync(modelPath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ImageModelRunner.Run(session, image, contract, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="RunImageToImageAsync{TPixel}(string, Image{TPixel}, ImageModelContract, CancellationToken)"/>.</summary>
    public Image<TPixel> RunImageToImage<TPixel>(
        string modelPath, Image<TPixel> image, ImageModelContract contract, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(contract);
        InferenceSession session = this.GetSession(modelPath, cancellationToken);
        return ImageModelRunner.Run(session, image, contract, cancellationToken);
    }

    /// <summary>Runs an image through an <see cref="IImageModel"/> and returns the result image.</summary>
    public async Task<Image<TPixel>> RunImageToImageAsync<TPixel>(
        IImageModel model, Image<TPixel> image, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(image);
        InferenceSession session = await this.GetSessionAsync(model, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ImageModelRunner.Run(session, image, model.Contract, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="RunImageToImageAsync{TPixel}(IImageModel, Image{TPixel}, CancellationToken)"/>.</summary>
    public Image<TPixel> RunImageToImage<TPixel>(IImageModel model, Image<TPixel> image, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(image);
        InferenceSession session = this.GetSessionAsync(model, cancellationToken).GetAwaiter().GetResult();
        return ImageModelRunner.Run(session, image, model.Contract, cancellationToken);
    }

    /// <summary>Disposes every loaded inference session and the hub.</summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        foreach (KeyValuePair<string, Lazy<Task<InferenceSession>>> pair in this.sessions)
        {
            if (pair.Value.IsValueCreated && pair.Value.Value.Status == TaskStatus.RanToCompletion)
            {
                pair.Value.Value.Result.Dispose();
            }
        }

        this.sessions.Clear();
        this.Hub.Dispose();
    }

    private InferenceSession CreateSession(string path)
    {
        this.ThrowIfDisposed();
        ExecutionProvider requested = this.resolvedProvider.Value;
        using SessionOptions sessionOptions = ExecutionProviderResolver.BuildSessionOptions(requested, this.Options, out ExecutionProvider active);
        this.activeProvider ??= active;
        this.Options.Log?.Invoke($"Loading ONNX model {path} on {active}.");
        var session = new InferenceSession(path, sessionOptions);
        if (this.disposed)
        {
            session.Dispose();
            throw new ObjectDisposedException(nameof(ImageAiSession));
        }

        return session;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(this.disposed, this);
}
