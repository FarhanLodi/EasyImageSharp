using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EasyImageSharp.Web.Tests;

/// <summary>
/// HMAC-SHA256 signed URLs: the signer against vectors computed independently (Python's <c>hmac</c> and
/// <c>urllib.parse.quote</c>), and the middleware's handling of valid, tampered, expired, extended and rotated URLs.
/// </summary>
public class SignatureTests : IClassFixture<TestImages>
{
    /// <summary>bytes(range(32)): 00 01 02 ... 1F.</summary>
    private static readonly byte[] VectorKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(200, 32).Select(i => (byte)i).ToArray());

    private readonly TestImages images;

    public SignatureTests(TestImages images) => this.images = images;

    // ----- The signer, against independent vectors -----

    public static TheoryData<string, string[], string> Vectors => new()
    {
        // canonical "/photos/cat.jpg?format=webp&width=300"
        { "/photos/cat.jpg", new[] { "width", "300", "format", "webp" }, "KZrkBQxfk8cPxTLgNt6BURo8kjFem9F36z06m1UFJF8" },

        // canonical "/a%20b/%C3%BC.jpg?bgcolor=%23fff&expires=1767225600&width=10": segment and value escaping, lowercased names
        { "/a b/ü.jpg", new[] { "bgcolor", "#fff", "Width", "10", "expires", "1767225600" }, "GIPuiTNpTdTg0zNLPGXBMah3yTNtlkXa6idpqHG_WYM" },

        // canonical "/x.png?a=1%26b%3D2" and "/x.png?a=1&b=2" must differ
        { "/x.png", new[] { "a", "1&b=2" }, "GdRw_TiET8Uz4Iei2PBbxrF68vVI7wqcXdKhJVMlFE0" },
        { "/x.png", new[] { "a", "1", "b", "2" }, "dTYh3QscOhkIHzC-yLoA4J1rKOAkSR2ud47m5VmGUJo" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ComputeSignature_MatchesIndependentVectors(string path, string[] flat, string expected)
    {
        var signer = new ImageUrlSigner(VectorKey);

        Assert.Equal(expected, signer.ComputeSignature(path, Pairs(flat)));
    }

    [Fact]
    public void ComputeSignature_IgnoresParameterOrderAndSig()
    {
        var signer = new ImageUrlSigner(VectorKey);

        string a = signer.ComputeSignature("/photos/cat.jpg", Pairs("width", "300", "format", "webp"));
        string b = signer.ComputeSignature("/photos/cat.jpg", Pairs("format", "webp", "sig", "whatever", "width", "300"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void CreateSignedUrl_EscapesAndAppendsTheSignature()
    {
        var signer = new ImageUrlSigner(VectorKey);

        string url = signer.CreateSignedUrl("/a b/ü.jpg", Pairs("bgcolor", "#fff", "Width", "10"), DateTimeOffset.FromUnixTimeSeconds(1767225600));

        Assert.Equal("/a%20b/%C3%BC.jpg?bgcolor=%23fff&Width=10&expires=1767225600&sig=GIPuiTNpTdTg0zNLPGXBMah3yTNtlkXa6idpqHG_WYM", url);
    }

    [Fact]
    public void Signer_RejectsBadArguments()
    {
        var signer = new ImageUrlSigner(VectorKey);

        Assert.Throws<ArgumentException>(() => new ImageUrlSigner(new byte[31]));
        Assert.Throws<ArgumentNullException>(() => new ImageUrlSigner(null!));
        Assert.Throws<FormatException>(() => ImageUrlSigner.FromBase64("not base64!"));
        Assert.Throws<ArgumentException>(() => signer.ComputeSignature("relative.jpg", Pairs()));
        Assert.Throws<ArgumentException>(() => signer.CreateSignedUrl("/a.jpg", Pairs("sig", "x")));
        Assert.Throws<ArgumentException>(() => signer.CreateSignedUrl("/a.jpg", Pairs("EXPIRES", "1")));
    }

    [Fact]
    public void Signer_CopiesTheKey()
    {
        byte[] key = (byte[])VectorKey.Clone();
        var signer = new ImageUrlSigner(key);
        string before = signer.ComputeSignature("/a.jpg", Pairs("width", "1"));

        key[0] ^= 0xFF;

        Assert.Equal(before, signer.ComputeSignature("/a.jpg", Pairs("width", "1")));
    }

    // ----- The middleware -----

    [Fact]
    public async Task ValidSignature_IsServed()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);
        string url = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/photo.jpg", Pairs("width", "50"));

        using HttpResponseMessage response = await host.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, Image.Identify(await response.Content.ReadAsByteArrayAsync()).Width);
    }

    [Theory]
    [InlineData("/photo.jpg?width=50")]
    [InlineData("/photo.jpg?width=50&sig=")]
    [InlineData("/photo.jpg?width=50&sig=tooshort")]
    [InlineData("/photo.jpg?width=50&sig=KZrkBQxfk8cPxTLgNt6BURo8kjFem9F36z06m1UFJF8")]
    [InlineData("/photo.jpg?width=50&sig=KZrkBQxfk8cPxTLgNt6BURo8kjFem9F36z06m1UFJF*")]
    [InlineData("/photo.jpg?width=50&sig=a&sig=b")]
    public async Task MissingOrInvalidSignature_Returns403(string url)
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);

        using HttpResponseMessage response = await host.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TamperedCommand_Returns403()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);
        string url = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/photo.jpg", Pairs("width", "50"));

        using HttpResponseMessage response = await host.GetAsync(url.Replace("width=50", "width=51", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SignatureForAnotherPath_Returns403()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);
        string url = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/alpha.png", Pairs("width", "50"));

        using HttpResponseMessage response = await host.GetAsync(url.Replace("/alpha.png", "/photo.jpg", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddedParameter_Returns403()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);
        string url = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/photo.jpg", Pairs("width", "50"));

        using HttpResponseMessage extraCommand = await host.GetAsync(url + "&quality=1");
        using HttpResponseMessage extraUnknown = await host.GetAsync(url + "&cachebuster=1");

        Assert.Equal(HttpStatusCode.Forbidden, extraCommand.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, extraUnknown.StatusCode);
    }

    [Fact]
    public async Task ReorderedAndRecasedParameters_StillVerify()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);
        string signature = ImageUrlSigner.FromBase64(KeyA).ComputeSignature("/photo.jpg", Pairs("width", "50", "format", "png"));

        using HttpResponseMessage response = await host.GetAsync($"/photo.jpg?sig={signature}&FORMAT=png&Width=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WrongKey_Returns403()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);
        string url = ImageUrlSigner.FromBase64(KeyB).CreateSignedUrl("/photo.jpg", Pairs("width", "50"));

        using HttpResponseMessage response = await host.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RotatedKeys_AcceptEveryConfiguredKey()
    {
        // KeyB is the new signing key; KeyA is still accepted while URLs signed with it are in circulation.
        await using WebTestHost host = await StartSignedAsync(this.images, KeyB, KeyA);
        string oldUrl = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/photo.jpg", Pairs("width", "50"));
        string newUrl = host.Services.GetRequiredService<ImageUrlSigner>().CreateSignedUrl("/photo.jpg", Pairs("width", "60"));

        using HttpResponseMessage oldResponse = await host.GetAsync(oldUrl);
        using HttpResponseMessage newResponse = await host.GetAsync(newUrl);

        Assert.Equal(HttpStatusCode.OK, oldResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);
        Assert.Equal(newUrl, ImageUrlSigner.FromBase64(KeyB).CreateSignedUrl("/photo.jpg", Pairs("width", "60")));
    }

    [Fact]
    public async Task RetiredKey_Returns403()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyB);
        string oldUrl = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/photo.jpg", Pairs("width", "50"));

        using HttpResponseMessage response = await host.GetAsync(oldUrl);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Expiry_IsHonouredAndCapsTheBrowserCache()
    {
        var clock = new ManualTimeProvider();
        await using WebTestHost host = await StartSignedAsync(this.images, clock, KeyA);
        string url = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/photo.jpg", Pairs("width", "50"), clock.UtcNow.AddMinutes(10));

        using HttpResponseMessage fresh = await host.GetAsync(url);
        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        using HttpResponseMessage expired = await host.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(TimeSpan.FromMinutes(10), fresh.Headers.CacheControl?.MaxAge);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [Fact]
    public async Task TamperedExpiry_Returns403()
    {
        var clock = new ManualTimeProvider();
        await using WebTestHost host = await StartSignedAsync(this.images, clock, KeyA);
        DateTimeOffset expiry = clock.UtcNow.AddMinutes(1);
        string url = ImageUrlSigner.FromBase64(KeyA).CreateSignedUrl("/photo.jpg", Pairs("width", "50"), expiry);
        string extended = url.Replace(
            "expires=" + expiry.ToUnixTimeSeconds(),
            "expires=" + expiry.AddYears(1).ToUnixTimeSeconds(),
            StringComparison.Ordinal);

        using HttpResponseMessage response = await host.GetAsync(extended);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SignedButMalformedExpiry_Returns400()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);
        string signature = ImageUrlSigner.FromBase64(KeyA).ComputeSignature("/photo.jpg", Pairs("width", "50", "expires", "soon"));

        using HttpResponseMessage response = await host.GetAsync($"/photo.jpg?width=50&expires=soon&sig={signature}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignatureIsCheckedBeforeCommands()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);

        // An unsigned request learns nothing about command parsing.
        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=abc");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithSigningEnabled_RequestsWithoutCommandsStillPassThrough()
    {
        await using WebTestHost host = await StartSignedAsync(this.images, KeyA);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg");

        Assert.True(response.Headers.Contains(WebTestHost.FallthroughHeader));
    }

    [Fact]
    public async Task WithoutSigning_SigAndExpiresAreIgnored()
    {
        var clock = new ManualTimeProvider();
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, services: s => s.AddSingleton<TimeProvider>(clock));

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=50&expires=1&sig=bogus");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SignerFromContainer_WithoutKeys_Throws()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ImageUrlSigner>());
    }

    internal static IEnumerable<KeyValuePair<string, string>> Pairs(params string[] flat)
    {
        for (int i = 0; i < flat.Length; i += 2)
        {
            yield return new KeyValuePair<string, string>(flat[i], flat[i + 1]);
        }
    }

    private static Task<WebTestHost> StartSignedAsync(TestImages images, params string[] keys)
        => StartSignedAsync(images, null, keys);

    private static Task<WebTestHost> StartSignedAsync(TestImages images, ManualTimeProvider? clock, params string[] keys)
        => WebTestHost.StartAsync(
            images,
            options =>
            {
                foreach (string key in keys)
                {
                    options.SigningKeys.Add(key);
                }
            },
            services: clock is null ? null : s => s.AddSingleton<TimeProvider>(clock));
}
