using System.Net;
using System.Net.Http.Headers;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Web.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Xunit;

namespace EasyImageSharp.Web.Tests;

/// <summary>HTTP validators and 304 handling, and the disk and distributed server-side caches.</summary>
public class CachingTests : IClassFixture<TestImages>
{
    private readonly TestImages images;

    public CachingTests(TestImages images) => this.images = images;

    // ----- HTTP caching -----

    [Fact]
    public async Task Response_CarriesValidatorsAndCacheControl()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=64");

        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.NotNull(response.Content.Headers.LastModified);
        DateTimeOffset fileTime = File.GetLastWriteTimeUtc(this.images.PathOf("photo.jpg"));
        Assert.True(Math.Abs((response.Content.Headers.LastModified!.Value - fileTime).TotalSeconds) < 1);
        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromDays(7), response.Headers.CacheControl.MaxAge);
    }

    [Fact]
    public async Task BrowserCacheMaxAgeZero_SendsNoCache()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, o => o.BrowserCacheMaxAge = TimeSpan.Zero);

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=64");

        Assert.True(response.Headers.CacheControl!.NoCache);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IfNoneMatch_WithTheETag_Returns304(bool weak)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);
        using HttpResponseMessage first = await host.GetAsync("/photo.jpg?width=64");

        var request = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(first.Headers.ETag!.Tag, weak));
        using HttpResponseMessage second = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);
        Assert.Equal(first.Headers.CacheControl, second.Headers.CacheControl);
    }

    [Fact]
    public async Task IfNoneMatchStar_Returns304()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        var request = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using HttpResponseMessage response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task NotModified_WithNegotiation_KeepsVary()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);
        using HttpResponseMessage first = await host.GetAsync("/photo.jpg?width=64", "image/webp");

        var request = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        request.Headers.TryAddWithoutValidation("Accept", "image/webp");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using HttpResponseMessage second = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Contains("Accept", second.Headers.Vary);
    }

    [Fact]
    public async Task IfNoneMatch_Mismatch_WinsOverIfModifiedSince()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        var request = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"something-else\""));
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddYears(1);
        using HttpResponseMessage response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IfModifiedSince_IsHonoured()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);
        DateTimeOffset fileTime = File.GetLastWriteTimeUtc(this.images.PathOf("photo.jpg"));

        var notModified = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        notModified.Headers.IfModifiedSince = fileTime.AddSeconds(1);
        var modified = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        modified.Headers.IfModifiedSince = fileTime.AddHours(-1);

        using HttpResponseMessage first = await host.Client.SendAsync(notModified);
        using HttpResponseMessage second = await host.Client.SendAsync(modified);

        Assert.Equal(HttpStatusCode.NotModified, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task ChangedSource_ChangesTheETag()
    {
        using var images = new TestImages();
        await using WebTestHost host = await WebTestHost.StartAsync(images);
        using HttpResponseMessage before = await host.GetAsync("/photo.jpg?width=64");

        File.SetLastWriteTimeUtc(images.PathOf("photo.jpg"), DateTime.UtcNow.AddMinutes(5));
        using HttpResponseMessage after = await host.GetAsync("/photo.jpg?width=64");

        Assert.NotEqual(before.Headers.ETag, after.Headers.ETag);
    }

    // ----- Disk cache -----

    [Fact]
    public async Task PhysicalCache_ServesRepeatRequestsWithoutReprocessing()
    {
        string cacheRoot = this.NewCacheRoot();
        var counter = new CountingSourceProvider(new FileProviderImageSourceProvider(new PhysicalFileProvider(this.images.Root)));
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            build: b => b.AddSourceProvider(counter).UsePhysicalCache(cacheRoot));

        using HttpResponseMessage first = await host.GetAsync("/photo.jpg?width=70");
        using HttpResponseMessage second = await host.GetAsync("/photo.jpg?width=70");

        Assert.Equal(1, counter.Opens);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Content.Headers.ContentType, second.Content.Headers.ContentType);
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);

        string[] files = Directory.GetFiles(cacheRoot, "*", SearchOption.AllDirectories);
        string entry = Assert.Single(files);
        Assert.EndsWith(".eisc", entry, StringComparison.Ordinal);
        string key = Path.GetFileNameWithoutExtension(entry);
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(Path.Combine(cacheRoot, key[..2], key[2..4], key + ".eisc"), entry);
        Assert.StartsWith("\"" + key[..32], first.Headers.ETag!.Tag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhysicalCache_UnknownParametersDoNotMultiplyEntries_AcceptDoes()
    {
        string cacheRoot = this.NewCacheRoot();
        var counter = new CountingSourceProvider(new FileProviderImageSourceProvider(new PhysicalFileProvider(this.images.Root)));
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            build: b => b.AddSourceProvider(counter).UsePhysicalCache(cacheRoot));

        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage response = await host.GetAsync($"/photo.jpg?width=71&nonce={i}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using HttpResponseMessage webp = await host.GetAsync("/photo.jpg?width=71", "image/webp");

        Assert.Equal(2, counter.Opens);
        Assert.Equal(2, Directory.GetFiles(cacheRoot, "*.eisc", SearchOption.AllDirectories).Length);
        Assert.Equal("image/webp", webp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PhysicalCache_CorruptEntry_IsReprocessedAndReplaced()
    {
        string cacheRoot = this.NewCacheRoot();
        var counter = new CountingSourceProvider(new FileProviderImageSourceProvider(new PhysicalFileProvider(this.images.Root)));
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            build: b => b.AddSourceProvider(counter).UsePhysicalCache(cacheRoot));
        using HttpResponseMessage first = await host.GetAsync("/photo.jpg?width=72");
        string entry = Assert.Single(Directory.GetFiles(cacheRoot, "*.eisc", SearchOption.AllDirectories));

        File.WriteAllBytes(entry, new byte[] { (byte)'E', (byte)'I', (byte)'S', (byte)'C', 1, 2, 3 });
        using HttpResponseMessage second = await host.GetAsync("/photo.jpg?width=72");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, counter.Opens);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
        Assert.True(new FileInfo(entry).Length > 100);
        Assert.Empty(Directory.GetFiles(cacheRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PhysicalCache_ExpiredEntry_IsReprocessed()
    {
        string cacheRoot = this.NewCacheRoot();
        var clock = new ManualTimeProvider();
        var counter = new CountingSourceProvider(new FileProviderImageSourceProvider(new PhysicalFileProvider(this.images.Root)));
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            o => o.CacheMaxAge = TimeSpan.FromHours(1),
            build: b => b.AddSourceProvider(counter).UsePhysicalCache(cacheRoot),
            services: s => s.AddSingleton<TimeProvider>(clock));

        using (await host.GetAsync("/photo.jpg?width=73"))
        {
        }

        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        using (await host.GetAsync("/photo.jpg?width=73"))
        {
        }

        Assert.Equal(1, counter.Opens);
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        using (await host.GetAsync("/photo.jpg?width=73"))
        {
        }

        Assert.Equal(2, counter.Opens);
    }

    [Fact]
    public async Task PhysicalCache_UnwritableRoot_DoesNotFailTheRequest()
    {
        // A file where the cache directory should be makes every write fail.
        string blocked = Path.Combine(this.images.Outside, "blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "not a directory");
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, build: b => b.UsePhysicalCache(blocked));

        using HttpResponseMessage response = await host.GetAsync("/photo.jpg?width=74");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Magic.IsJpeg(await response.Content.ReadAsByteArrayAsync()));
    }

    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("..\\..\\x")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123")]
    [InlineData("")]
    public async Task PhysicalCache_RejectsKeysThatAreNotLowercaseHashes(string key)
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, build: b => b.UsePhysicalCache(this.NewCacheRoot()));
        IImageCache cache = host.Services.GetRequiredService<IImageCache>();

        await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync(key, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAsync(key, "image/png", new byte[] { 1 }, default));
    }

    [Fact]
    public async Task PhysicalCache_RelativeRoot_ResolvesAgainstTheContentRoot()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images, build: b => b.UsePhysicalCache("relative-cache"));

        var cache = (PhysicalImageCache)host.Services.GetRequiredService<IImageCache>();

        Assert.True(Path.IsPathRooted(cache.RootPath));
        Assert.EndsWith("relative-cache", cache.RootPath, StringComparison.Ordinal);
    }

    // ----- Distributed cache -----

    [Fact]
    public async Task DistributedCache_ServesRepeatRequestsWithoutReprocessing()
    {
        var counter = new CountingSourceProvider(new FileProviderImageSourceProvider(new PhysicalFileProvider(this.images.Root)));
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            build: b => b.AddSourceProvider(counter).UseDistributedCache(),
            services: s => s.AddDistributedMemoryCache());

        using HttpResponseMessage first = await host.GetAsync("/alpha.png?width=50");
        using HttpResponseMessage second = await host.GetAsync("/alpha.png?width=50");
        using HttpResponseMessage head = await host.SendAsync(HttpMethod.Head, "/alpha.png?width=50");

        Assert.Equal(1, counter.Opens);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(first.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task SharedDistributedCache_KeepsDifferentSourceRootsApart()
    {
        // Two roots hold a file with the same relative path, length and time stamp but different pixels. Sharing one
        // distributed cache must not let the first application's output answer the second's request.
        string rootA = Path.Combine(this.images.Outside, "root-a-" + Guid.NewGuid().ToString("N"));
        string rootB = Path.Combine(this.images.Outside, "root-b-" + Guid.NewGuid().ToString("N"));
        WriteUniformQoi(rootA, new Rgba32(255, 0, 0, 255));
        WriteUniformQoi(rootB, new Rgba32(0, 0, 255, 255));
        Assert.Equal(new FileInfo(Path.Combine(rootA, "same.qoi")).Length, new FileInfo(Path.Combine(rootB, "same.qoi")).Length);

        var shared = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await using WebTestHost hostA = await WebTestHost.StartAsync(rootA, build: b => b.UseDistributedCache(), services: s => s.AddSingleton<IDistributedCache>(shared));
        await using WebTestHost hostB = await WebTestHost.StartAsync(rootB, build: b => b.UseDistributedCache(), services: s => s.AddSingleton<IDistributedCache>(shared));

        using HttpResponseMessage fromA = await hostA.GetAsync("/same.qoi?format=png");
        using HttpResponseMessage fromB = await hostB.GetAsync("/same.qoi?format=png");
        using Image<Rgba32> imageA = Image.Load<Rgba32>(await fromA.Content.ReadAsByteArrayAsync());
        using Image<Rgba32> imageB = Image.Load<Rgba32>(await fromB.Content.ReadAsByteArrayAsync());

        Assert.Equal(new Rgba32(255, 0, 0, 255), imageA[0, 0]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), imageB[0, 0]);
        Assert.NotEqual(fromA.Headers.ETag, fromB.Headers.ETag);
    }

    [Fact]
    public async Task MalformedConditionalHeaders_AreIgnored()
    {
        await using WebTestHost host = await WebTestHost.StartAsync(this.images);

        var request = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        request.Headers.TryAddWithoutValidation("If-None-Match", "not-a-quoted-etag");
        var other = new HttpRequestMessage(HttpMethod.Get, "/photo.jpg?width=64");
        other.Headers.TryAddWithoutValidation("If-Modified-Since", "yesterday-ish");
        using HttpResponseMessage first = await host.Client.SendAsync(request);
        using HttpResponseMessage second = await host.Client.SendAsync(other);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task DistributedCache_WithoutAnIDistributedCache_FailsAtStartup()
    {
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => WebTestHost.StartAsync(this.images, build: b => b.UseDistributedCache()));
    }

    // ----- Entry layout -----

    [Fact]
    public void CacheEntryCodec_RoundTrips()
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        byte[] blob = CacheEntryCodec.Encode("image/webp", new byte[] { 9, 8, 7 }, created);

        Assert.True(CacheEntryCodec.TryReadHeader(blob, out string type, out long length, out DateTimeOffset when, out int header));
        Assert.Equal("image/webp", type);
        Assert.Equal(3, length);
        Assert.Equal(created, when);
        Assert.Equal(blob.Length - 3, header);
        Assert.Equal(new byte[] { 9, 8, 7 }, blob[header..]);
    }

    [Fact]
    public void CacheEntryCodec_RejectsForeignAndTruncatedData()
    {
        byte[] blob = CacheEntryCodec.Encode("image/png", new byte[] { 1 }, DateTimeOffset.UnixEpoch);

        Assert.False(CacheEntryCodec.TryReadHeader(blob.AsSpan(0, 10), out _, out _, out _, out _));
        Assert.False(CacheEntryCodec.TryReadHeader(blob.AsSpan(0, 18), out _, out _, out _, out _));
        byte[] foreign = (byte[])blob.Clone();
        foreign[0] = (byte)'X';
        Assert.False(CacheEntryCodec.TryReadHeader(foreign, out _, out _, out _, out _));
        byte[] future = (byte[])blob.Clone();
        future[4] = 2;
        Assert.False(CacheEntryCodec.TryReadHeader(future, out _, out _, out _, out _));
        Assert.Throws<ArgumentException>(() => CacheEntryCodec.Encode("image/péng", new byte[1], DateTimeOffset.UnixEpoch));
    }

    private static void WriteUniformQoi(string root, Rgba32 color)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "same.qoi");
        using (var image = new Image<Rgba32>(4, 4, color))
        using (FileStream stream = File.Create(path))
        {
            image.SaveAsQoi(stream);
        }

        File.SetLastWriteTimeUtc(path, new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc));
    }

    private string NewCacheRoot() => Path.Combine(this.images.Outside, "cache-" + Guid.NewGuid().ToString("N"));
}
