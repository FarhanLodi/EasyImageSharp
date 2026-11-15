using System.Net;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Web.Tests;

/// <summary>
/// The query command set: which requests the middleware claims, what each command does to the output, how invalid
/// commands are rejected, and that equivalent spellings and unknown parameters do not split the cache.
/// </summary>
public class CommandTests : IClassFixture<TestImages>
{
    private readonly TestImages images;

    public CommandTests(TestImages images) => this.images = images;

    // ----- Which requests are handled -----

    [Theory]
    [InlineData("/photo.jpg")]
    [InlineData("/photo.jpg?v=3")]
    [InlineData("/readme.txt?width=10")]
    [InlineData("/photo.jpg?sig=abc&expires=1")]
    public async Task RequestsWithoutCommandsOrImageExtension_PassThrough(string url)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync(url);

        Assert.True(response.Headers.Contains(WebTestHost.FallthroughHeader));
    }

    [Fact]
    public async Task PostRequests_PassThrough()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.SendAsync(HttpMethod.Post, "/photo.jpg?width=10");

        Assert.True(response.Headers.Contains(WebTestHost.FallthroughHeader));
    }

    [Fact]
    public async Task RequestPathPrefix_LimitsHandledRequestsAndIsStrippedForTheSource()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.RequestPathPrefix = "/images");

        using HttpResponseMessage handled = await host.GetAsync("/images/photo.jpg?width=40");
        using HttpResponseMessage outside = await host.GetAsync("/photo.jpg?width=40");

        Assert.Equal(HttpStatusCode.OK, handled.StatusCode);
        Assert.Equal(40, Image.Identify(await handled.Content.ReadAsByteArrayAsync()).Width);
        Assert.True(outside.Headers.Contains(WebTestHost.FallthroughHeader));
    }

    // ----- Resizing -----

    [Theory]
    [InlineData("width=200", 200, 150)]
    [InlineData("height=150", 200, 150)]
    [InlineData("width=100&height=100", 100, 75)]
    [InlineData("width=100&height=100&mode=max", 100, 75)]
    [InlineData("width=100&height=100&mode=crop", 100, 100)]
    [InlineData("width=100&height=100&mode=pad", 100, 100)]
    [InlineData("width=100&height=100&mode=boxpad", 100, 100)]
    [InlineData("width=100&height=100&mode=stretch", 100, 100)]
    [InlineData("width=100&height=100&mode=min", 133, 100)]
    [InlineData("width=100&height=100&mode=crop&anchor=topleft", 100, 100)]
    [InlineData("width=200&sampler=lanczos3", 200, 150)]
    [InlineData("width=200&sampler=nearest", 200, 150)]
    public async Task Resize_ProducesTheExpectedSize(string query, int width, int height)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?" + query);
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True(Magic.IsJpeg(body));
        ImageInfo info = Image.Identify(body);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
    }

    [Theory]
    [InlineData("width=800", 400, 300)]
    [InlineData("width=800&height=800&mode=crop", 300, 300)]
    [InlineData("width=800&height=800&mode=max", 400, 300)]
    [InlineData("width=800&height=600&mode=stretch", 400, 300)]
    [InlineData("width=800&height=800&mode=pad", 800, 800)]
    public async Task WithoutUpscaling_TheSourceIsNeverEnlarged(string query, int width, int height)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?" + query);
        ImageInfo info = Image.Identify(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
    }

    [Fact]
    public async Task WithUpscaling_TheSourceIsEnlarged()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.AllowUpscaling = true);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=800");
        ImageInfo info = Image.Identify(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(800, info.Width);
        Assert.Equal(600, info.Height);
    }

    [Fact]
    public async Task PadOnJpeg_FillsWithTheDefaultBackground()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100&height=200&mode=pad");
        using Image<Rgba32> image = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(100, image.Width);
        Assert.Equal(200, image.Height);
        Rgba32 corner = image[0, 0];
        Assert.True(corner.R > 240 && corner.G > 240 && corner.B > 240, $"Expected white padding, got {corner}.");
    }

    [Fact]
    public async Task PadWithBackgroundColor_UsesIt()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100&height=200&mode=pad&bgcolor=00ff00&format=png");
        using Image<Rgba32> image = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(new Rgba32(0, 255, 0, 255), image[0, 0]);
    }

    // ----- Format, quality and background -----

    [Theory]
    [InlineData("format=png", "image/png")]
    [InlineData("format=PNG", "image/png")]
    [InlineData("format=jpg", "image/jpeg")]
    [InlineData("format=jpeg", "image/jpeg")]
    [InlineData("format=webp", "image/webp")]
    [InlineData("format=gif", "image/gif")]
    public async Task Format_SelectsTheEncoder(string query, string contentType)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=50&" + query);
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        bool matches = contentType switch
        {
            "image/png" => Magic.IsPng(body),
            "image/jpeg" => Magic.IsJpeg(body),
            "image/webp" => Magic.IsWebp(body),
            _ => Magic.IsGif(body),
        };
        Assert.True(matches);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Quality_ChangesTheEncodedSize()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage low = await host.GetAsync("/photo.jpg?width=300&quality=5");
        using HttpResponseMessage high = await host.GetAsync("/photo.jpg?width=300&quality=100");

        Assert.True((await low.Content.ReadAsByteArrayAsync()).Length < (await high.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task TransparentSourceToJpeg_IsFlattenedOntoWhite()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/alpha.png?format=jpeg");
        using Image<Rgba32> image = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());

        Rgba32 transparentArea = image[10, 50];
        Assert.True(transparentArea.R > 240 && transparentArea.G > 240 && transparentArea.B > 240, $"Got {transparentArea}.");
    }

    [Fact]
    public async Task BackgroundColor_CompositesTransparentAreas()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/alpha.png?bgcolor=red");
        using Image<Rgba32> image = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new Rgba32(255, 0, 0, 255), image[10, 50]);
        Assert.Equal(new Rgba32(0, 200, 0, 255), image[150, 50]);
    }

    [Fact]
    public async Task TransparentSourceToPng_KeepsAlpha()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/alpha.png?width=100");
        using Image<Rgba32> image = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(0, image[5, 25].A);
    }

    // ----- Rotation and flipping -----

    [Fact]
    public async Task Rotate90_SwapsTheDimensions()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?rotate=90&format=png");
        ImageInfo info = Image.Identify(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(TestImages.PhotoHeight, info.Width);
        Assert.Equal(TestImages.PhotoWidth, info.Height);
    }

    [Fact]
    public async Task FlipHorizontal_MirrorsTheImage()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?flip=h&format=png");
        using Image<Rgba32> image = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());

        // The source is red on the left and blue on the right.
        Assert.True(image[10, 150].B > 150 && image[10, 150].R < 100, $"Left edge should be blue, got {image[10, 150]}.");
        Assert.True(image[390, 150].R > 150 && image[390, 150].B < 100, $"Right edge should be red, got {image[390, 150]}.");
    }

    [Fact]
    public async Task AutoOrientFalse_IsAccepted()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?autoorient=false&width=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----- Invalid commands -----

    [Theory]
    [InlineData("width=abc")]
    [InlineData("width=0")]
    [InlineData("width=-5")]
    [InlineData("width=+5")]
    [InlineData("width=%2010")]
    [InlineData("width=1234567890")]
    [InlineData("width=")]
    [InlineData("height=1.5")]
    [InlineData("quality=0")]
    [InlineData("quality=101")]
    [InlineData("mode=fill")]
    [InlineData("anchor=middle")]
    [InlineData("format=xyz")]
    [InlineData("bgcolor=notacolor")]
    [InlineData("autoorient=yes")]
    [InlineData("sampler=magic")]
    [InlineData("rotate=45")]
    [InlineData("flip=x")]
    [InlineData("width=10&width=20")]
    [InlineData("width=10&Width=20")]
    public async Task InvalidCommand_Returns400(string query)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    // ----- Normalisation -----

    [Theory]
    [InlineData("width=100&format=jpg", "format=JPEG&width=100")]
    [InlineData("width=100", "width=100&utm_source=mail")]
    [InlineData("width=100", "width=100&mode=max&anchor=center&sampler=bicubic")]
    [InlineData("format=png", "format=png&mode=crop")]
    [InlineData("width=100", "WIDTH=100")]
    [InlineData("width=100&bgcolor=f00", "width=100&bgcolor=red")]
    public async Task EquivalentRequests_ShareOneETag(string first, string second)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage a = await host.GetAsync("/photo.jpg?" + first);
        using HttpResponseMessage b = await host.GetAsync("/photo.jpg?" + second);

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(a.Headers.ETag, b.Headers.ETag);
    }

    [Theory]
    [InlineData("width=100", "width=101")]
    [InlineData("width=100", "width=100&quality=50")]
    [InlineData("width=100", "width=100&format=png")]
    [InlineData("width=100&height=50&mode=crop", "width=100&height=50&mode=pad")]
    public async Task DifferentRequests_HaveDifferentETags(string first, string second)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage a = await host.GetAsync("/photo.jpg?" + first);
        using HttpResponseMessage b = await host.GetAsync("/photo.jpg?" + second);

        Assert.NotEqual(a.Headers.ETag, b.Headers.ETag);
    }

    // ----- HEAD -----

    [Fact]
    public async Task Head_ReturnsTheHeadersWithoutABody()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage get = await host.GetAsync("/photo.jpg?width=64");
        using HttpResponseMessage head = await host.SendAsync(HttpMethod.Head, "/photo.jpg?width=64");

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(get.Headers.ETag, head.Headers.ETag);
        Assert.Equal("image/jpeg", head.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Head_FromTheCache_ReturnsTheHeadersWithoutABody()
    {
        string cacheRoot = Path.Combine(this.images.Outside, "head-cache-" + Guid.NewGuid().ToString("N"));
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, build: b => b.UsePhysicalCache(cacheRoot));

        using HttpResponseMessage get = await host.GetAsync("/photo.jpg?width=65");
        using HttpResponseMessage head = await host.SendAsync(HttpMethod.Head, "/photo.jpg?width=65");

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task SuccessfulResponses_CarryNoSniff()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=64");

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
}
