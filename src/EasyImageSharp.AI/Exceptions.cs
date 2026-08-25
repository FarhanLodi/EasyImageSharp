namespace EasyImageSharp.AI;

/// <summary>Base exception for errors raised by the EasyImageSharp.AI add-on.</summary>
public class ImageAiException : Exception
{
    public ImageAiException(string message)
        : base(message)
    {
    }

    public ImageAiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A model file could not be obtained: a network or I/O failure while downloading, a rejected
/// (non-HTTPS or malformed) source, or a refused file name.
/// </summary>
public class ModelDownloadException : ImageAiException
{
    public ModelDownloadException(string message)
        : base(message)
    {
    }

    public ModelDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A downloaded model failed integrity verification: its SHA-256 did not match the pinned value, or it
/// has no pinned checksum and <see cref="ImageAiOptions.AllowUnverifiedModels"/> was not enabled.
/// The offending file is deleted before this is thrown.
/// </summary>
public sealed class ModelChecksumException : ModelDownloadException
{
    public ModelChecksumException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A required model is not present in the cache and <see cref="ImageAiOptions.Offline"/> is enabled,
/// so it cannot be downloaded. Pre-seed the cache, add a <see cref="ImageAiOptions.ModelPathOverrides"/>
/// entry, or disable offline mode.
/// </summary>
public sealed class OfflineModelMissingException : ImageAiException
{
    public OfflineModelMissingException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A model's inputs or outputs do not match the contract the operation expects (wrong rank, channel count,
/// element type or an unexpected output size). Points at an export that does not follow the documented I/O
/// contract rather than at bad image data.
/// </summary>
public sealed class ModelContractException : ImageAiException
{
    public ModelContractException(string message)
        : base(message)
    {
    }
}
