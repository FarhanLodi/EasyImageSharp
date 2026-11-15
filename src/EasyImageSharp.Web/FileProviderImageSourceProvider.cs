using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace EasyImageSharp.Web;

/// <summary>
/// An <see cref="IImageSourceProvider"/> that reads originals from an <see cref="IFileProvider"/>, for example a
/// <see cref="PhysicalFileProvider"/> over a directory of originals or the application's web root.
/// </summary>
/// <remarks>
/// <see cref="PhysicalFileProvider"/> itself refuses paths that leave its root and, by default, hidden, system and
/// dot-prefixed files; the middleware's own path checks run before this provider is consulted. Directories are never
/// returned. The source version is the file's physical path, last-write time and length.
/// </remarks>
public sealed class FileProviderImageSourceProvider : IImageSourceProvider
{
    private readonly IFileProvider fileProvider;

    /// <summary>Creates a provider that resolves request paths against <paramref name="fileProvider"/>.</summary>
    /// <param name="fileProvider">The file provider to read originals from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fileProvider"/> is <see langword="null"/>.</exception>
    public FileProviderImageSourceProvider(IFileProvider fileProvider)
    {
        ArgumentNullException.ThrowIfNull(fileProvider);
        this.fileProvider = fileProvider;
    }

    /// <inheritdoc />
    public ValueTask<IImageSource?> GetSourceAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        IFileInfo file = this.fileProvider.GetFileInfo(path);
        if (!file.Exists || file.IsDirectory)
        {
            return ValueTask.FromResult<IImageSource?>(null);
        }

        return ValueTask.FromResult<IImageSource?>(new FileImageSource(file));
    }

    /// <summary>One file found by the provider.</summary>
    private sealed class FileImageSource : IImageSource
    {
        private readonly IFileInfo file;

        public FileImageSource(IFileInfo file)
        {
            this.file = file;

            // The physical path (when there is one) keeps two roots apart when they share one distributed cache, even
            // for files with the same relative path, time stamp and length. It only ever reaches the cache key hash.
            this.Version = string.Create(
                CultureInfo.InvariantCulture,
                $"{file.PhysicalPath ?? file.Name}|{file.LastModified.UtcTicks:x}-{file.Length:x}");
        }

        public string Version { get; }

        public DateTimeOffset? LastModified => this.file.LastModified;

        public long? Length => this.file.Length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(this.file.CreateReadStream());
        }
    }
}
