using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EasyImageSharp.Web.Tests;

/// <summary>
/// A directory of generated source images: <c>{Root}</c> is the source root and <c>{Outside}</c> its parent, which
/// holds <c>secret.jpg</c> so traversal tests can prove it is never reached.
/// </summary>
public sealed class TestImages : IDisposable
{
    public const int PhotoWidth = 400;
    public const int PhotoHeight = 300;

    public TestImages()
    {
        this.Outside = Path.Combine(Path.GetTempPath(), "eis-web-tests", Guid.NewGuid().ToString("N"));
        this.Root = Path.Combine(this.Outside, "wwwroot");
        Directory.CreateDirectory(Path.Combine(this.Root, "sub"));

        // photo.jpg: opaque, left half red and right half blue, so a horizontal flip is visible.
        using (var photo = new Image<Rgba32>(PhotoWidth, PhotoHeight))
        {
            for (int y = 0; y < PhotoHeight; y++)
            {
                for (int x = 0; x < PhotoWidth; x++)
                {
                    photo[x, y] = x < PhotoWidth / 2 ? new Rgba32(220, 20, 20, 255) : new Rgba32(20, 20, 220, 255);
                }
            }

            this.Write("photo.jpg", stream => photo.SaveAsJpeg(stream, new JpegEncoder { Quality = 95 }));
            File.WriteAllBytes(Path.Combine(this.Outside, "secret.jpg"), File.ReadAllBytes(Path.Combine(this.Root, "photo.jpg")));
        }

        // alpha.png: 200x100, transparent left half, opaque green right half.
        using (var alpha = new Image<Rgba32>(200, 100))
        {
            for (int y = 0; y < 100; y++)
            {
                for (int x = 100; x < 200; x++)
                {
                    alpha[x, y] = new Rgba32(0, 200, 0, 255);
                }
            }

            this.Write("alpha.png", stream => alpha.SaveAsPng(stream));
            this.Write("sub/nested.png", stream => alpha.SaveAsPng(stream));
            this.Write(".hidden.png", stream => alpha.SaveAsPng(stream));
        }

        // anim.gif: two 64x64 frames.
        using (var animation = new Image<Rgba32>(64, 64, new Rgba32(255, 0, 0, 255)))
        {
            var second = new Rgba32[64 * 64];
            Array.Fill(second, new Rgba32(0, 0, 255, 255));
            animation.Frames.AddFrame(second);
            this.Write("anim.gif", stream => animation.SaveAsGif(stream, new GifEncoder()));
        }

        // photo.webp: an opaque WebP original.
        using (var webp = new Image<Rgba32>(120, 80, new Rgba32(10, 120, 200, 255)))
        {
            this.Write("photo.webp", stream => webp.SaveAsWebp(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossless }));
        }

        // A valid 3000x3000 PNG, for decoding limits.
        using (var large = new Image<Rgba32>(3000, 3000, new Rgba32(1, 2, 3, 255)))
        {
            this.Write("large.png", stream => large.SaveAsPng(stream));
        }

        // A PNG signature followed by garbage, and a text file with an image extension.
        File.WriteAllBytes(
            Path.Combine(this.Root, "corrupt.png"),
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 1, 2, 3 });
        File.WriteAllText(Path.Combine(this.Root, "notimage.jpg"), "This is not an image.");
        File.WriteAllText(Path.Combine(this.Root, "readme.txt"), "text");
    }

    public string Root { get; }

    public string Outside { get; }

    public string PathOf(string relative) => Path.Combine(this.Root, relative);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Outside, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the directory lives under the temp folder.
        }
    }

    private void Write(string relative, Action<Stream> write)
    {
        using FileStream stream = File.Create(this.PathOf(relative));
        write(stream);
    }
}

/// <summary>An in-memory ASP.NET Core host running <c>UseEasyImageSharp()</c> in front of a marker endpoint.</summary>
public sealed class WebTestHost : IAsyncDisposable
{
    /// <summary>The header the terminal middleware sets, proving a request passed through the image middleware.</summary>
    public const string FallthroughHeader = "X-Fallthrough";

    private WebTestHost(IHost host)
    {
        this.Host = host;
        this.Server = host.GetTestServer();
        this.Client = this.Server.CreateClient();
    }

    public IHost Host { get; }

    public TestServer Server { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => this.Host.Services;

    public static Task<WebTestHost> StartAsync(
        TestImages images,
        Action<EasyImageSharpWebOptions>? configure = null,
        Action<IEasyImageSharpWebBuilder>? build = null,
        Action<IServiceCollection>? services = null)
        => StartAsync(images.Root, configure, build, services);

    public static async Task<WebTestHost> StartAsync(
        string sourceRoot,
        Action<EasyImageSharpWebOptions>? configure = null,
        Action<IEasyImageSharpWebBuilder>? build = null,
        Action<IServiceCollection>? services = null)
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(collection =>
                {
                    services?.Invoke(collection);
                    IEasyImageSharpWebBuilder builder = collection.AddEasyImageSharpWeb(options =>
                    {
                        options.SourceRootPath = sourceRoot;
                        configure?.Invoke(options);
                    });
                    build?.Invoke(builder);
                })
                .Configure(app =>
                {
                    app.UseEasyImageSharp();
                    app.Run(static context =>
                    {
                        context.Response.Headers[FallthroughHeader] = "1";
                        return context.Response.WriteAsync("fallthrough");
                    });
                }))
            .Build();

        try
        {
            await host.StartAsync();
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return new WebTestHost(host);
    }

    public Task<HttpResponseMessage> GetAsync(string url, string? accept = null, CancellationToken cancellationToken = default)
        => this.SendAsync(HttpMethod.Get, url, accept, cancellationToken);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? accept = null, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, url);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return this.Client.SendAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.Host.StopAsync();
        this.Host.Dispose();
    }
}

/// <summary>A clock the tests move by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => this.UtcNow;
}

/// <summary>Byte-signature checks that do not depend on this library's decoders.</summary>
public static class Magic
{
    public static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static bool IsPng(byte[] data) => data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';

    public static bool IsGif(byte[] data) => data.Length > 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F';

    public static bool IsWebp(byte[] data)
        => data.Length > 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8);
}

/// <summary>Wraps a provider and counts how often a source is opened, i.e. how often an image is really processed.</summary>
public sealed class CountingSourceProvider : IImageSourceProvider
{
    private readonly IImageSourceProvider inner;
    private int opens;

    public CountingSourceProvider(IImageSourceProvider inner) => this.inner = inner;

    public int Opens => Volatile.Read(ref this.opens);

    public async ValueTask<IImageSource?> GetSourceAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        IImageSource? source = await this.inner.GetSourceAsync(context, path, cancellationToken);
        return source is null ? null : new Counting(source, this);
    }

    private sealed class Counting : IImageSource
    {
        private readonly IImageSource inner;
        private readonly CountingSourceProvider owner;

        public Counting(IImageSource inner, CountingSourceProvider owner)
        {
            this.inner = inner;
            this.owner = owner;
        }

        public string Version => this.inner.Version;

        public DateTimeOffset? LastModified => this.inner.LastModified;

        public long? Length => this.inner.Length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.owner.opens);
            return this.inner.OpenReadAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Serves fixed bytes for every path, but holds <see cref="IImageSource.OpenReadAsync"/> until <see cref="Release"/>
/// is called, so a request can be parked inside its processing slot. Records whether the wait was cancelled.
/// </summary>
public sealed class GatedSourceProvider : IImageSourceProvider
{
    private readonly byte[] content;
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int entered;

    public GatedSourceProvider(byte[] content) => this.content = content;

    /// <summary>Completes each time a request starts waiting at the gate (reset by <see cref="WaitForEntriesAsync"/>).</summary>
    public int Entered => Volatile.Read(ref this.entered);

    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => this.gate.TrySetResult();

    public async Task WaitForEntriesAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (this.Entered < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public ValueTask<IImageSource?> GetSourceAsync(HttpContext context, string path, CancellationToken cancellationToken)
        => ValueTask.FromResult<IImageSource?>(new Source(this));

    private sealed class Source : IImageSource
    {
        private readonly GatedSourceProvider owner;

        public Source(GatedSourceProvider owner) => this.owner = owner;

        public string Version => "gated";

        public DateTimeOffset? LastModified => null;

        public long? Length => this.owner.content.Length;

        public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.owner.entered);
            try
            {
                await this.owner.gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                this.owner.Cancelled.TrySetResult();
                throw;
            }

            return new MemoryStream(this.owner.content, writable: false);
        }
    }
}

/// <summary>Serves fixed bytes for every path without announcing a length, so the size limit is enforced while reading.</summary>
public sealed class UnknownLengthSourceProvider : IImageSourceProvider
{
    private readonly byte[] content;

    public UnknownLengthSourceProvider(byte[] content) => this.content = content;

    public ValueTask<IImageSource?> GetSourceAsync(HttpContext context, string path, CancellationToken cancellationToken)
        => ValueTask.FromResult<IImageSource?>(new Source(this.content));

    private sealed class Source : IImageSource
    {
        private readonly byte[] content;

        public Source(byte[] content) => this.content = content;

        public string Version => "unknown-length";

        public DateTimeOffset? LastModified => null;

        public long? Length => null;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream(this.content, writable: false));
    }
}
