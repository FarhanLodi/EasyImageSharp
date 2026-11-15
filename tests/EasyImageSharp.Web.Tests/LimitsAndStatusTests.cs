using System.Net;
using EasyImageSharp.PixelFormats;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace EasyImageSharp.Web.Tests;

/// <summary>Output and source limits, the precise status codes, and path-traversal attempts.</summary>
public class LimitsAndStatusTests : IClassFixture<TestImages>
{
    private readonly TestImages images;

    public LimitsAndStatusTests(TestImages images) => this.images = images;

    // ----- Output limits (422) -----

    [Theory]
    [InlineData("width=4097")]
    [InlineData("height=4097")]
    [InlineData("width=4096&height=4097")]
    [InlineData("width=4096&height=4096&mode=crop&format=tiff")]
    [InlineData("format=tiff")]
    [InlineData("format=bmp")]
    public async Task OverDefaultLimits_Returns422(string query)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?" + query);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task MaxOutputPixels_AppliesToTheRequestedBox()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.MaxOutputPixels = 10_000);

        using HttpResponseMessage over = await host.GetAsync("/photo.jpg?width=101&height=100&mode=crop");
        using HttpResponseMessage under = await host.GetAsync("/photo.jpg?width=100&height=100&mode=crop");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal(HttpStatusCode.OK, under.StatusCode);
    }

    [Fact]
    public async Task OutputLimits_ApplyToTheComputedSizeToo()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o =>
        {
            o.MaxWidth = 300;
            o.MaxHeight = 300;
        });

        // Only the height is asked for, but keeping the 4:3 aspect makes the width 400.
        using HttpResponseMessage derived = await host.GetAsync("/photo.jpg?height=300");

        // mode=min covers the box, so the result is larger than the box.
        using HttpResponseMessage cover = await host.GetAsync("/photo.jpg?width=300&height=300&mode=min");

        // No command resizes: the untouched 400x300 source is itself over the limit.
        using HttpResponseMessage transcode = await host.GetAsync("/photo.jpg?format=png");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, derived.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cover.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, transcode.StatusCode);
    }

    [Fact]
    public async Task AllowedFormats_RestrictExplicitFormats()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.AllowedFormats.Add("png"));

        using HttpResponseMessage png = await host.GetAsync("/photo.jpg?format=png");
        using HttpResponseMessage jpeg = await host.GetAsync("/photo.jpg?format=jpeg");
        using HttpResponseMessage auto = await host.GetAsync("/photo.jpg?width=20");

        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, jpeg.StatusCode);

        // Without a format the source format would be JPEG, which is not allowed, so the only allowed format is used.
        Assert.Equal("image/png", auto.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("width=320&height=240", HttpStatusCode.OK)]
    [InlineData("width=100", HttpStatusCode.OK)]
    [InlineData("height=50", HttpStatusCode.OK)]
    [InlineData("width=321&height=240", HttpStatusCode.UnprocessableEntity)]
    [InlineData("width=320", HttpStatusCode.UnprocessableEntity)]
    [InlineData("width=100&height=50", HttpStatusCode.UnprocessableEntity)]
    [InlineData("format=png", HttpStatusCode.OK)]
    public async Task AllowedSizes_ActAsAnAllowlist(string query, HttpStatusCode expected)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o =>
        {
            o.AllowedSizes.Add("320x240");
            o.AllowedSizes.Add("100x");
            o.AllowedSizes.Add("x50");
            o.MaxWidth = 1000;
        });

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?" + query);

        Assert.Equal(expected, response.StatusCode);
    }

    // ----- Source limits (413) -----

    [Fact]
    public async Task SourceLargerThanMaxSourceBytes_Returns413()
    {
        long size = new FileInfo(this.images.PathOf("photo.jpg")).Length;
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.MaxSourceBytes = size - 1);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=10");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task SourceOfUnknownLength_IsLimitedWhileReading()
    {
        byte[] photo = File.ReadAllBytes(this.images.PathOf("photo.jpg"));
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            o => o.MaxSourceBytes = photo.Length - 1,
            build: b => b.AddSourceProvider(new UnknownLengthSourceProvider(photo)));

        using HttpResponseMessage response = await host.GetAsync("/anything.jpg?width=10");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task SourceOfUnknownLength_WithinTheLimit_IsServed()
    {
        byte[] photo = File.ReadAllBytes(this.images.PathOf("photo.jpg"));
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, build: b => b.AddSourceProvider(new UnknownLengthSourceProvider(photo)));

        using HttpResponseMessage response = await host.GetAsync("/anything.jpg?width=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SourceOverMaxSourcePixels_Returns413()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.MaxSourcePixels = 3000L * 3000 - 1);

        using HttpResponseMessage response = await host.GetAsync("/large.png?width=10");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task CoreDecoderLimits_FromEasyImageSharpOptions_AlsoApply()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            services: s => s.AddEasyImageSharp(o => o.MaxPixels = 1000));

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=10");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task PixelBufferBudget_FromEasyImageSharpOptions_Returns413()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            services: s => s.AddEasyImageSharp(o => o.MaxPixelBufferBytes = 1024));

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=10");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // ----- Source problems (404 / 415 / 422) -----

    [Theory]
    [InlineData("/missing.jpg?width=10")]
    [InlineData("/sub/missing.png?width=10")]
    public async Task MissingSource_Returns404(string url)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(WebTestHost.FallthroughHeader));
    }

    [Fact]
    public async Task DirectoryPath_PassesThrough()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/sub?width=10");

        Assert.True(response.Headers.Contains(WebTestHost.FallthroughHeader));
    }

    [Fact]
    public async Task NestedSource_IsServed()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/sub/nested.png?width=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NotAnImage_Returns415()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/notimage.jpg?width=10");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task CorruptImage_Returns422()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/corrupt.png?width=10");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ErrorResponses_AreNotCacheable()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=abc");

        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Null(response.Headers.ETag);
    }

    // ----- Path traversal -----

    [Theory]
    [InlineData("/../secret.jpg")]
    [InlineData("/sub/../../secret.jpg")]
    [InlineData("/./photo.jpg")]
    [InlineData("/..%2fsecret.jpg")]
    [InlineData("/%2e%2e/secret.jpg")]
    [InlineData("/..\\secret.jpg")]
    [InlineData("/sub\\..\\..\\secret.jpg")]
    [InlineData("//photo.jpg")]
    [InlineData("/sub//nested.png")]
    [InlineData("/C:/Windows/secret.jpg")]
    [InlineData("/photo.jpg:stream.jpg")]
    [InlineData("/sub./nested.png")]
    [InlineData("/sub /nested.png")]
    [InlineData("/pho\u0000to.jpg")]
    [InlineData("/.hidden.png")]
    public async Task TraversalAndTrickPaths_Return404(string path)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        // TestServer.SendAsync sets the decoded path directly, bypassing the URI normalisation an HTTP client applies,
        // which is what a hostile raw request line can achieve against some servers.
        HttpContext context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = new PathString(path);
            c.Request.QueryString = new QueryString("?width=10");
        });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task TraversalOverHttp_NeverReachesTheParentDirectory()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        foreach (string url in new[] { "/../secret.jpg?width=10", "/%2E%2E/secret.jpg?width=10", "/sub/%2e%2e/%2e%2e/secret.jpg?width=10" })
        {
            using HttpResponseMessage response = await host.GetAsync(url);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task FileProvider_RefusesPathsOutsideItsRoot()
    {
        var provider = new FileProviderImageSourceProvider(new PhysicalFileProvider(this.images.Root));

        Assert.Null(await provider.GetSourceAsync(new DefaultHttpContext(), "/../secret.jpg", default));
        Assert.Null(await provider.GetSourceAsync(new DefaultHttpContext(), "/sub", default));
        Assert.NotNull(await provider.GetSourceAsync(new DefaultHttpContext(), "/photo.jpg", default));
    }

    [Fact]
    public async Task ResponseDoesNotLeakTheSecret()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);
        HttpContext context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = new PathString("/../secret.jpg");
            c.Request.QueryString = new QueryString("?format=png");
        });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
    }

    [Fact]
    public async Task OpaqueSource_StaysOpaque()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=40&format=png");
        using Image<Rgba32> image = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(255, image[5, 5].A);
    }
}
