using System.Net;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// How <see cref="ImageAiOptions.Quantize"/> picks between a model's int8 and fp32 files: which sources win, when the
/// fp32 file is the documented fallback, and which failures are never hidden by a fallback.
/// </summary>
public class QuantizedModelResolutionTests
{
    private const string BaseUrl = "https://models.invalid/repo";
    private const string Fp32Name = "q-model.onnx";
    private const string Int8Name = "q-model.int8.onnx";

    private static readonly byte[] Fp32Body = Hash.Bytes(900, seed: 11);
    private static readonly byte[] Int8Body = Hash.Bytes(300, seed: 12);

    private static ModelDescriptor Descriptor(string? int8Sha256) => new()
    {
        Name = "q-model",
        FileName = Fp32Name,
        Sha256 = Hash.Sha256Hex(Fp32Body),
        Int8FileName = Int8Name,
        Int8Sha256 = int8Sha256,
        BaseUrl = BaseUrl,
        License = "MIT",
    };

    /// <summary>Serves the fp32 body, and the int8 body only when <paramref name="int8Status"/> is OK.</summary>
    private static FakeHttpHandler Server(HttpStatusCode int8Status = HttpStatusCode.OK, byte[]? int8Body = null) => new(request =>
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith(Int8Name, StringComparison.Ordinal))
        {
            return int8Status == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(int8Body ?? Int8Body) }
                : new HttpResponseMessage(int8Status);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fp32Body) };
    });

    private static ImageAiOptions Options(TempCache cache, FakeHttpHandler handler, bool quantize = true, int retries = 0) => new()
    {
        CachePath = cache.Path,
        HttpMessageHandler = handler,
        Quantize = quantize,
        MaxRetries = retries,
        RetryBaseDelay = TimeSpan.Zero,
    };

    [Fact]
    public async Task WithoutQuantize_TheInt8FileIsNeverUsed_EvenWhenCached()
    {
        using var cache = new TempCache();
        File.WriteAllBytes(Path.Combine(cache.Path, Int8Name), Int8Body);
        FakeHttpHandler handler = Server();
        using var hub = new ModelHub(Options(cache, handler, quantize: false));

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(Int8Body)));

        Assert.False(resolved.IsQuantized);
        Assert.Equal(Fp32Name, resolved.FileName);
        Assert.Equal(ModelSource.Download, resolved.Source);
    }

    [Fact]
    public async Task APinnedInt8Variant_IsDownloadedAndVerified()
    {
        using var cache = new TempCache();
        FakeHttpHandler handler = Server();
        using var hub = new ModelHub(Options(cache, handler));

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(Int8Body)));

        Assert.True(resolved.IsQuantized);
        Assert.Equal(Int8Body, File.ReadAllBytes(resolved.Path));
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task AnInt8VariantTheSourceDoesNotServe_FallsBackToFp32WithoutRetrying(HttpStatusCode status)
    {
        using var cache = new TempCache();
        FakeHttpHandler handler = Server(status);
        var log = new List<string>();
        ImageAiOptions options = Options(cache, handler, retries: 3);
        options.Log = log.Add;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(Int8Body)));

        Assert.False(resolved.IsQuantized);
        Assert.Equal(Fp32Name, resolved.FileName);
        Assert.Equal(2, handler.RequestCount); // one int8 attempt (a missing file is not retried), one fp32 download
        Assert.Contains(log, line => line.Contains("using fp32", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(cache.Path, Int8Name)));
    }

    [Fact]
    public async Task AnInt8ChecksumMismatch_IsRaised_NotHiddenByAFallback()
    {
        using var cache = new TempCache();
        FakeHttpHandler handler = Server(int8Body: Hash.Bytes(300, seed: 99));
        using var hub = new ModelHub(Options(cache, handler));

        await Assert.ThrowsAsync<ModelChecksumException>(() => hub.ResolveAsync(Descriptor(Hash.Sha256Hex(Int8Body))));
        Assert.False(File.Exists(Path.Combine(cache.Path, Int8Name)));
        Assert.False(File.Exists(Path.Combine(cache.Path, Fp32Name)));
    }

    [Fact]
    public async Task AServerErrorOnTheInt8File_IsRaised()
    {
        using var cache = new TempCache();
        FakeHttpHandler handler = Server(HttpStatusCode.InternalServerError);
        using var hub = new ModelHub(Options(cache, handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => hub.ResolveAsync(Descriptor(Hash.Sha256Hex(Int8Body))));
    }

    [Fact]
    public async Task Offline_UsesTheCachedFp32FileWhenTheInt8FileIsNotCached()
    {
        using var cache = new TempCache();
        File.WriteAllBytes(Path.Combine(cache.Path, Fp32Name), Fp32Body);
        FakeHttpHandler handler = Server();
        ImageAiOptions options = Options(cache, handler);
        options.Offline = true;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(Int8Body)));

        Assert.False(resolved.IsQuantized);
        Assert.Equal(ModelSource.Cache, resolved.Source);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Offline_PrefersACachedInt8File()
    {
        using var cache = new TempCache();
        File.WriteAllBytes(Path.Combine(cache.Path, Fp32Name), Fp32Body);
        File.WriteAllBytes(Path.Combine(cache.Path, Int8Name), Int8Body);
        ImageAiOptions options = Options(cache, Server());
        options.Offline = true;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(int8Sha256: null));

        Assert.True(resolved.IsQuantized);
        Assert.Equal(ModelSource.Cache, resolved.Source);
    }

    [Fact]
    public async Task Offline_WithNeitherFileCached_ReportsTheMissingModel()
    {
        using var cache = new TempCache();
        ImageAiOptions options = Options(cache, Server());
        options.Offline = true;
        using var hub = new ModelHub(options);

        await Assert.ThrowsAsync<OfflineModelMissingException>(() => hub.ResolveAsync(Descriptor(Hash.Sha256Hex(Int8Body))));
    }

    [Fact]
    public async Task AnUnpinnedInt8Variant_IsDownloadedOnlyWithAllowUnverifiedModels_AndFallsBackWhenMissing()
    {
        using var cache = new TempCache();
        FakeHttpHandler handler = Server(HttpStatusCode.NotFound);
        ImageAiOptions options = Options(cache, handler);
        options.AllowUnverifiedModels = true;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(int8Sha256: null));

        Assert.False(resolved.IsQuantized);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AnInt8PathOverride_IsReportedAsQuantized()
    {
        using var cache = new TempCache();
        string local = Path.Combine(cache.Path, "local");
        Directory.CreateDirectory(local);
        string file = Path.Combine(local, Int8Name);
        File.WriteAllBytes(file, Int8Body);
        ImageAiOptions options = Options(cache, Server());
        options.ModelPathOverrides[Int8Name] = file;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(int8Sha256: null));

        Assert.True(resolved.IsQuantized);
        Assert.Equal(ModelSource.Override, resolved.Source);
    }

    [Fact]
    public async Task AMissingFp32File_IsNotRetried()
    {
        using var cache = new TempCache();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var hub = new ModelHub(Options(cache, handler, quantize: false, retries: 3));

        await Assert.ThrowsAsync<HttpRequestException>(() => hub.ResolveAsync(Descriptor(int8Sha256: null)));
        Assert.Equal(1, handler.RequestCount);
    }
}
