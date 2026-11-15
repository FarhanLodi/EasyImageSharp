using System.Net;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Png;
using Xunit;

namespace EasyImageSharp.Web.Tests;

/// <summary>Accept-header format negotiation: WebP when listed, Vary: Accept, explicit formats, animations and added formats.</summary>
public class NegotiationTests : IClassFixture<TestImages>
{
    private const string ChromeAccept = "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8";

    private readonly TestImages images;

    public NegotiationTests(TestImages images) => this.images = images;

    [Fact]
    public async Task AcceptListingWebp_ServesWebpWithVary()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100", ChromeAccept);

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.True(Magic.IsWebp(await response.Content.ReadAsByteArrayAsync()));
        Assert.Contains("Accept", response.Headers.Vary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*/*")]
    [InlineData("image/*")]
    [InlineData("image/webp;q=0, */*")]
    [InlineData("image/png,image/jpeg")]
    public async Task AcceptWithoutWebp_KeepsTheSourceFormatButStillVaries(string? accept)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100", accept);

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Accept", response.Headers.Vary);
    }

    [Theory]
    [InlineData("image/webp;q=abc")]
    [InlineData(";;;,,,")]
    [InlineData("image/webp;q=0.5;;")]
    public async Task MalformedAccept_IsServedNotRejected(string accept)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100", accept);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ExplicitFormat_WinsAndDoesNotVary()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100&format=png", ChromeAccept);

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Accept", response.Headers.Vary);
    }

    [Fact]
    public async Task FormatAuto_Negotiates()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?format=auto", "image/webp");

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task NegotiationDisabled_KeepsTheSourceFormatAndDoesNotVary()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.EnableFormatNegotiation = false);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100", ChromeAccept);

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Accept", response.Headers.Vary);
    }

    [Fact]
    public async Task WebpNotAllowed_IsNeverNegotiated()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o =>
        {
            o.AllowedFormats.Add("jpeg");
            o.AllowedFormats.Add("png");
        });

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=100", ChromeAccept);

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DifferentAccept_HasDifferentETag()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage webp = await host.GetAsync("/photo.jpg?width=100", ChromeAccept);
        using HttpResponseMessage jpeg = await host.GetAsync("/photo.jpg?width=100", "*/*");

        Assert.NotEqual(webp.Headers.ETag, jpeg.Headers.ETag);
    }

    [Fact]
    public async Task Animation_KeepsItsFormat()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/anim.gif?width=32", ChromeAccept);
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal("image/gif", response.Content.Headers.ContentType?.MediaType);
        Assert.True(Magic.IsGif(body));
        Assert.Equal(2, Image.Identify(body).FrameCount);
    }

    [Fact]
    public async Task WebpSource_ForAClientWithoutWebp_FallsBackToJpeg()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage plain = await host.GetAsync("/photo.webp?width=60", "*/*");
        using HttpResponseMessage webp = await host.GetAsync("/photo.webp?width=60", "image/webp");

        Assert.Equal("image/jpeg", plain.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/webp", webp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TransparentSource_ForAClientWithoutWebp_StaysPng()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/alpha.png?width=60", "*/*");

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AddedFormat_CanBePreferredByNegotiation()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            o =>
            {
                o.AllowedFormats.Add("jpeg");
                o.AllowedFormats.Add("webp");
                o.AllowedFormats.Add("avif");
                o.NegotiatedFormats.Add("avif");
                o.NegotiatedFormats.Add("webp");
            },
            build: b => b.AddOutputFormat(new FakeAvifFormat()));

        using HttpResponseMessage avif = await host.GetAsync("/photo.jpg?width=40", ChromeAccept);
        using HttpResponseMessage webp = await host.GetAsync("/photo.jpg?width=40", "image/webp");
        using HttpResponseMessage explicitAvif = await host.GetAsync("/photo.jpg?width=40&format=av1f");

        Assert.Equal("image/avif", avif.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/webp", webp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/avif", explicitAvif.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>A stand-in for an AVIF add-on: it registers like one but writes PNG bytes.</summary>
    private sealed class FakeAvifFormat : IImageOutputFormat
    {
        public string Name => "avif";

        public IReadOnlyList<string> Aliases { get; } = new[] { "av1f" };

        public string MimeType => "image/avif";

        public bool SupportsTransparency => true;

        public IImageEncoder CreateEncoder(int? quality) => new PngEncoder();
    }
}
