namespace EasyImageSharp.AI;

/// <summary>Progress of a single model download, reported through <see cref="ImageAiOptions.Progress"/>.</summary>
/// <param name="FileName">The file being downloaded (for example <c>UVDoc.onnx</c>).</param>
/// <param name="BytesDownloaded">Bytes on disk so far, including any resumed prefix.</param>
/// <param name="TotalBytes">Total size in bytes, or <c>-1</c> when the server did not report it.</param>
public readonly record struct ModelDownloadProgress(string FileName, long BytesDownloaded, long TotalBytes)
{
    /// <summary>Completion fraction in 0-1, or <c>null</c> when the total size is unknown.</summary>
    public double? Fraction => this.TotalBytes > 0 ? (double)this.BytesDownloaded / this.TotalBytes : null;
}

/// <summary>
/// Configures an <see cref="ImageAiSession"/> and its <see cref="ModelHub"/>: which execution provider to
/// use, where models are cached, whether the network may be touched, and how downloads are verified.
/// The defaults are safe: HTTPS only, SHA-256 verified fail-closed, CPU inference.
/// </summary>
public sealed class ImageAiOptions
{
    private int maxBatchSize = ImageModelRunner.DefaultMaxBatchSize;

    /// <summary>Environment variable that overrides the default cache directory.</summary>
    public const string CacheEnvironmentVariable = "EASYIMAGESHARP_CACHE";

    /// <summary>Environment variable that overrides the model base URL (private mirror).</summary>
    public const string BaseUrlEnvironmentVariable = "EASYIMAGESHARP_MODEL_BASE_URL";

    /// <summary>The execution provider to use. Default <see cref="ExecutionProvider.Auto"/>.</summary>
    public ExecutionProvider ExecutionProvider { get; set; } = ExecutionProvider.Auto;

    /// <summary>GPU device ordinal passed to the CUDA / DirectML providers. Default 0.</summary>
    public int DeviceId { get; set; }

    /// <summary>
    /// Prefer the int8-quantised <c>{name}.int8.onnx</c> variant of a model. Default <c>false</c>: the int8 file is
    /// never used, even when it is in the cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <c>Quantize = true</c>, <see cref="ModelHub.ResolveAsync"/> looks for a model in this order: a
    /// <see cref="ModelPathOverrides"/> entry keyed by the model name, by the int8 file name, then by the fp32 file
    /// name; the int8 file in the cache; the int8 file downloaded and verified against
    /// <see cref="ModelDescriptor.Int8Sha256"/> (or, with <see cref="AllowUnverifiedModels"/>, unverified from a mirror);
    /// and finally the fp32 file (cache, then download). The fp32 file is used, with a <see cref="Log"/> message, when
    /// the descriptor has no int8 variant, when the variant has no pinned checksum and unverified downloads are not
    /// allowed, when <see cref="Offline"/> is set and only the fp32 file is cached, or when the server answers the int8
    /// request with HTTP 404 / 410. Any other failure of the int8 download (checksum mismatch, policy, network) is
    /// raised: it is never hidden by a silent switch to fp32. <see cref="ResolvedModel.IsQuantized"/> reports which
    /// file was chosen.
    /// </para>
    /// <para>
    /// The int8 files are static QDQ quantisations (per-channel int8 weights, uint8 activations) that were kept only
    /// when they met a documented accuracy tolerance against the fp32 model (see <c>tools/ModelExport</c>). They are
    /// smaller downloads (28% to 86% of the fp32 size for the current variants); they are not necessarily faster, especially on
    /// CPUs without int8 dot-product instructions, so measure before switching for speed.
    /// </para>
    /// </remarks>
    public bool Quantize { get; set; }

    /// <summary>
    /// Upper bound on the inputs that the batch APIs (<see cref="ImageAiSession.RunImageToImageBatchAsync{TPixel}(IImageModel, IReadOnlyList{Image{TPixel}}, CancellationToken)"/>,
    /// <see cref="ImageAiSession.DetectOrientationBatchAsync{TPixel}"/>) stack into one inference call. Default
    /// <see cref="ImageModelRunner.DefaultMaxBatchSize"/> (8); 1 runs every input on its own. Models whose batch
    /// dimension is fixed always run one input at a time. Larger batches trade memory for throughput, mostly on GPUs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or greater than 1024.</exception>
    public int MaxBatchSize
    {
        get => this.maxBatchSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1024);
            this.maxBatchSize = value;
        }
    }

    /// <summary>
    /// Strict offline mode: a model that is not in the cache (and not overridden) raises
    /// <see cref="OfflineModelMissingException"/> instead of being downloaded. Default <c>false</c>.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Directory that holds downloaded models. <c>null</c> uses the <c>EASYIMAGESHARP_CACHE</c> environment
    /// variable when set, otherwise <c>%LOCALAPPDATA%/EasyImageSharp/models</c> (or the platform equivalent of
    /// the local application data folder).
    /// </summary>
    public string? CachePath { get; set; }

    /// <summary>
    /// Base URL to fetch every model from instead of the per-model Hugging Face URL, for a private mirror.
    /// Takes precedence over the <c>EASYIMAGESHARP_MODEL_BASE_URL</c> environment variable. Files are
    /// resolved as <c>{BaseUrlOverride}/{FileName}</c> (flat layout).
    /// </summary>
    public string? BaseUrlOverride { get; set; }

    /// <summary>
    /// Allow a model download whose registry entry has no pinned SHA-256 (or a mirror serving unlisted files).
    /// Default <c>false</c>: every downloaded file must verify against a pinned checksum, because a model is
    /// parsed by native code and is the supply-chain trust root of this package. Local files supplied through
    /// <see cref="ModelPathOverrides"/> are never verified and are unaffected by this switch.
    /// </summary>
    public bool AllowUnverifiedModels { get; set; }

    /// <summary>
    /// Allow a plain <c>http://</c> model source. Default <c>false</c>: any non-HTTPS URL (built-in, override
    /// or environment) is refused. Enable only for a trusted on-host mirror you control.
    /// </summary>
    public bool AllowInsecureModelSource { get; set; }

    /// <summary>
    /// Maps a model name (<see cref="ModelDescriptor.Name"/>) or file name to a local ONNX file that is used
    /// as-is, without any download or checksum verification. This is how you supply your own export of a
    /// model whose registry entry is not yet published, or a private fine-tune. Keys are case-insensitive.
    /// </summary>
    public IDictionary<string, string> ModelPathOverrides { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>ONNX Runtime intra-op thread count; <c>null</c> keeps the runtime default.</summary>
    public int? IntraOpNumThreads { get; set; }

    /// <summary>ONNX Runtime inter-op thread count; <c>null</c> keeps the runtime default.</summary>
    public int? InterOpNumThreads { get; set; }

    /// <summary>
    /// A ready-made <see cref="System.Net.Http.HttpClient"/> for downloads (proxy, custom handlers, an
    /// <c>IHttpClientFactory</c> client). Not disposed by this package. Takes precedence over
    /// <see cref="HttpMessageHandler"/>.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// A handler to build the download client from (for tests or custom transports). Not disposed by this
    /// package. Ignored when <see cref="HttpClient"/> is set.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }

    /// <summary>Optional progress callback invoked while a model downloads.</summary>
    public IProgress<ModelDownloadProgress>? Progress { get; set; }

    /// <summary>Optional diagnostics sink (provider selection, downloads, fallbacks). Default none.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Additional attempts after a transient network/I/O failure. Default 3; 0 disables retries.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay of the exponential back-off between retries. Default 2 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Returns a copy of these options; the session snapshots its options at construction.</summary>
    internal ImageAiOptions Clone()
    {
        var copy = new ImageAiOptions
        {
            ExecutionProvider = this.ExecutionProvider,
            DeviceId = this.DeviceId,
            Quantize = this.Quantize,
            MaxBatchSize = this.MaxBatchSize,
            Offline = this.Offline,
            CachePath = this.CachePath,
            BaseUrlOverride = this.BaseUrlOverride,
            AllowUnverifiedModels = this.AllowUnverifiedModels,
            AllowInsecureModelSource = this.AllowInsecureModelSource,
            IntraOpNumThreads = this.IntraOpNumThreads,
            InterOpNumThreads = this.InterOpNumThreads,
            HttpClient = this.HttpClient,
            HttpMessageHandler = this.HttpMessageHandler,
            Progress = this.Progress,
            Log = this.Log,
            MaxRetries = this.MaxRetries,
            RetryBaseDelay = this.RetryBaseDelay,
        };

        foreach (KeyValuePair<string, string> pair in this.ModelPathOverrides)
        {
            copy.ModelPathOverrides[pair.Key] = pair.Value;
        }

        return copy;
    }
}
