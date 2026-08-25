using System.Net;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Model resolution: path overrides, cache hits, HTTPS-only downloads with pinned SHA-256 verification,
/// resumable partial files, offline mode and concurrent callers. Every test serves bytes from an in-memory
/// <see cref="FakeHttpHandler"/>, so nothing touches the network.
/// </summary>
public class ModelHubTests
{
    private const string BaseUrl = "https://models.invalid/repo";
    private const string FileName = "test-model.onnx";

    private static ModelDescriptor Descriptor(string? sha256, string baseUrl = BaseUrl, string fileName = FileName) => new()
    {
        Name = "test-model",
        FileName = fileName,
        Sha256 = sha256,
        BaseUrl = baseUrl,
        License = "MIT",
    };

    /// <summary>Options wired to a fake transport, a throw-away cache and no retry back-off.</summary>
    private static ImageAiOptions Options(TempCache cache, FakeHttpHandler handler) => new()
    {
        CachePath = cache.Path,
        HttpMessageHandler = handler,
        MaxRetries = 0,
        RetryBaseDelay = TimeSpan.Zero,
    };

    // ----- Checksum verification -----

    [Fact]
    public async Task CorrectChecksum_DownloadsVerifiesAndCaches()
    {
        byte[] body = Hash.Bytes(4096);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        using var hub = new ModelHub(Options(cache, handler));
        ModelDescriptor descriptor = Descriptor(Hash.Sha256Hex(body));

        ResolvedModel first = await hub.ResolveAsync(descriptor);

        Assert.Equal(ModelSource.Download, first.Source);
        Assert.Equal(FileName, first.FileName);
        Assert.False(first.IsQuantized);
        Assert.Equal(body, await File.ReadAllBytesAsync(first.Path));
        Assert.Equal(1, handler.RequestCount);

        // The second call is served from the cache without touching the transport again.
        ResolvedModel second = await hub.ResolveAsync(descriptor);
        Assert.Equal(ModelSource.Cache, second.Source);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task WrongChecksum_DeletesTheFileAndThrows()
    {
        byte[] body = Hash.Bytes(2048);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        using var hub = new ModelHub(Options(cache, handler));
        ModelDescriptor descriptor = Descriptor(Hash.Sha256Hex(Hash.Bytes(2048, seed: 1234)));

        ModelChecksumException error = await Assert.ThrowsAsync<ModelChecksumException>(
            () => hub.ResolveAsync(descriptor));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(cache.Path, FileName)), "The unverified file must not be cached.");
        Assert.False(File.Exists(Path.Combine(cache.Path, FileName + ".part")), "The partial file must be deleted.");
    }

    [Fact]
    public async Task NoPinnedChecksum_IsRefusedByDefault()
    {
        byte[] body = Hash.Bytes(512);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        using var hub = new ModelHub(Options(cache, handler));

        ModelChecksumException error = await Assert.ThrowsAsync<ModelChecksumException>(
            () => hub.ResolveAsync(Descriptor(null)));

        Assert.Contains("no pinned SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(cache.Path, FileName)));
    }

    [Fact]
    public async Task NoPinnedChecksum_IsAcceptedWhenExplicitlyAllowed()
    {
        byte[] body = Hash.Bytes(512);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        ImageAiOptions options = Options(cache, handler);
        options.AllowUnverifiedModels = true;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(null));

        Assert.Equal(ModelSource.Download, resolved.Source);
        Assert.Equal(body, await File.ReadAllBytesAsync(resolved.Path));
    }

    // ----- Offline -----

    [Fact]
    public async Task OfflineMode_WithNothingCached_Throws()
    {
        byte[] body = Hash.Bytes(256);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        ImageAiOptions options = Options(cache, handler);
        options.Offline = true;
        using var hub = new ModelHub(options);

        OfflineModelMissingException error = await Assert.ThrowsAsync<OfflineModelMissingException>(
            () => hub.ResolveAsync(Descriptor(Hash.Sha256Hex(body))));

        Assert.Contains("offline", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task OfflineMode_WithACachedFile_Succeeds()
    {
        byte[] body = Hash.Bytes(256);
        using var cache = new TempCache();
        await File.WriteAllBytesAsync(Path.Combine(cache.Path, FileName), body);

        var handler = FakeHttpHandler.Serving(body);
        ImageAiOptions options = Options(cache, handler);
        options.Offline = true;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(body)));

        Assert.Equal(ModelSource.Cache, resolved.Source);
        Assert.Equal(0, handler.RequestCount);
    }

    // ----- Resume -----

    [Fact]
    public async Task APartialFile_ResumesWithARangeRequest()
    {
        byte[] body = Hash.Bytes(8192);
        const int Prefix = 3000;
        using var cache = new TempCache();
        await File.WriteAllBytesAsync(Path.Combine(cache.Path, FileName + ".part"), body[..Prefix]);

        var handler = FakeHttpHandler.Serving(body);
        using var hub = new ModelHub(Options(cache, handler));

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(body)));

        Assert.Equal(ModelSource.Download, resolved.Source);
        Assert.Equal(body, await File.ReadAllBytesAsync(resolved.Path));
        Assert.Equal(1, handler.RequestCount);

        System.Net.Http.Headers.RangeHeaderValue? range = handler.Requests[0].Headers.Range;
        Assert.NotNull(range);
        Assert.Equal(Prefix, range!.Ranges.Single().From);
    }

    [Fact]
    public async Task APartialFile_IsRestartedWhenTheServerIgnoresTheRange()
    {
        byte[] body = Hash.Bytes(4096);
        using var cache = new TempCache();
        await File.WriteAllBytesAsync(Path.Combine(cache.Path, FileName + ".part"), body[..1000]);

        var handler = FakeHttpHandler.Serving(body, supportRange: false);
        using var hub = new ModelHub(Options(cache, handler));

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(body)));

        Assert.Equal(body, await File.ReadAllBytesAsync(resolved.Path));
    }

    [Fact]
    public async Task AnOverlongPartialFile_IsDiscardedAndRefetched()
    {
        byte[] body = Hash.Bytes(1024);
        using var cache = new TempCache();
        await File.WriteAllBytesAsync(Path.Combine(cache.Path, FileName + ".part"), Hash.Bytes(4096, seed: 77));

        var handler = FakeHttpHandler.Serving(body);
        using var hub = new ModelHub(Options(cache, handler));

        ResolvedModel resolved = await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(body)));

        Assert.Equal(body, await File.ReadAllBytesAsync(resolved.Path));
        Assert.Equal(2, handler.RequestCount); // the 416 answer, then a fresh full request
    }

    // ----- Transport policy -----

    [Fact]
    public async Task PlainHttp_IsRefused()
    {
        byte[] body = Hash.Bytes(128);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        using var hub = new ModelHub(Options(cache, handler));

        ModelDownloadException error = await Assert.ThrowsAsync<ModelDownloadException>(
            () => hub.ResolveAsync(Descriptor(Hash.Sha256Hex(body), baseUrl: "http://models.invalid/repo")));

        Assert.Contains("non-HTTPS", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PlainHttp_IsAllowedWhenExplicitlyOptedIn()
    {
        byte[] body = Hash.Bytes(128);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        ImageAiOptions options = Options(cache, handler);
        options.AllowInsecureModelSource = true;
        using var hub = new ModelHub(options);

        ResolvedModel resolved = await hub.ResolveAsync(
            Descriptor(Hash.Sha256Hex(body), baseUrl: "http://models.invalid/repo"));

        Assert.Equal(ModelSource.Download, resolved.Source);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AFailedResponse_IsReportedAsADownloadFailure()
    {
        using var cache = new TempCache();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var hub = new ModelHub(Options(cache, handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => hub.ResolveAsync(Descriptor("00")));
    }

    [Theory]
    [InlineData("../escape.onnx")]
    [InlineData("dir/model.onnx")]
    [InlineData("..")]
    [InlineData("")]
    public async Task AFileNameThatIsNotASinglePathSegment_IsRefused(string fileName)
    {
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(Hash.Bytes(16));
        using var hub = new ModelHub(Options(cache, handler));

        await Assert.ThrowsAsync<ModelDownloadException>(
            () => hub.ResolveAsync(Descriptor("00", fileName: fileName)));
        Assert.Equal(0, handler.RequestCount);
    }

    // ----- Path overrides -----

    [Fact]
    public async Task APathOverride_BypassesTheDownloadEntirely()
    {
        using var cache = new TempCache();
        string local = Path.Combine(cache.Path, "my-export.onnx");
        await File.WriteAllBytesAsync(local, Hash.Bytes(64));

        var handler = FakeHttpHandler.Serving(Hash.Bytes(64));
        ImageAiOptions options = Options(cache, handler);
        options.ModelPathOverrides["test-model"] = local;
        using var hub = new ModelHub(options);

        // A deliberately wrong checksum proves the override is never verified.
        ResolvedModel resolved = await hub.ResolveAsync(Descriptor("DEADBEEF"));

        Assert.Equal(ModelSource.Override, resolved.Source);
        Assert.Equal(local, resolved.Path);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task APathOverrideByFileName_IsAlsoHonoured()
    {
        using var cache = new TempCache();
        string local = Path.Combine(cache.Path, "by-file-name.onnx");
        await File.WriteAllBytesAsync(local, Hash.Bytes(32));

        var handler = FakeHttpHandler.Serving(Hash.Bytes(32));
        ImageAiOptions options = Options(cache, handler);
        options.ModelPathOverrides[FileName] = local;
        using var hub = new ModelHub(options);

        Assert.Equal(ModelSource.Override, (await hub.ResolveAsync(Descriptor("00"))).Source);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task APathOverridePointingAtNothing_Throws()
    {
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(Hash.Bytes(8));
        ImageAiOptions options = Options(cache, handler);
        options.ModelPathOverrides["test-model"] = Path.Combine(cache.Path, "absent.onnx");
        using var hub = new ModelHub(options);

        await Assert.ThrowsAsync<FileNotFoundException>(() => hub.ResolveAsync(Descriptor("00")));
    }

    [Fact]
    public void PathOverrideKeys_AreCaseInsensitive()
    {
        var options = new ImageAiOptions();
        options.ModelPathOverrides["Doc-Orientation"] = "a.onnx";
        Assert.True(options.ModelPathOverrides.ContainsKey("doc-orientation"));
    }

    // ----- Concurrency -----

    [Fact]
    public async Task ConcurrentCallersForTheSameModel_DownloadItOnce()
    {
        byte[] body = Hash.Bytes(32768);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        using var hub = new ModelHub(Options(cache, handler));
        ModelDescriptor descriptor = Descriptor(Hash.Sha256Hex(body));

        string[] paths = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => hub.EnsureModelAsync(descriptor))));

        Assert.Equal(1, handler.RequestCount);
        Assert.All(paths, path => Assert.Equal(paths[0], path));
        Assert.Equal(body, await File.ReadAllBytesAsync(paths[0]));
    }

    // ----- Progress and logging -----

    [Fact]
    public async Task ProgressIsReportedWithTheFinalByteCount()
    {
        byte[] body = Hash.Bytes(10000);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        var reports = new List<ModelDownloadProgress>();
        ImageAiOptions options = Options(cache, handler);
        options.Progress = new Progress<ModelDownloadProgress>(p =>
        {
            lock (reports)
            {
                reports.Add(p);
            }
        });
        var logs = new List<string>();
        options.Log = message =>
        {
            lock (logs)
            {
                logs.Add(message);
            }
        };
        using var hub = new ModelHub(options);

        await hub.ResolveAsync(Descriptor(Hash.Sha256Hex(body)));

        // Progress<T> posts asynchronously, so give the callbacks a moment to land.
        for (int i = 0; i < 50 && reports.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(reports);
        ModelDownloadProgress last = reports[^1];
        Assert.Equal(body.Length, last.BytesDownloaded);
        Assert.Equal(body.Length, last.TotalBytes);
        Assert.Equal(1.0, last.Fraction);
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void ProgressFractionIsNullWhenTheSizeIsUnknown()
        => Assert.Null(new ModelDownloadProgress("m.onnx", 10, -1).Fraction);

    // ----- Query helpers -----

    [Fact]
    public async Task IsAvailableLocallyAndCanResolve_ReflectTheCacheAndPolicy()
    {
        byte[] body = Hash.Bytes(64);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        ImageAiOptions options = Options(cache, handler);
        using var hub = new ModelHub(options);
        ModelDescriptor published = Descriptor(Hash.Sha256Hex(body));
        ModelDescriptor placeholder = Descriptor(null);

        Assert.False(hub.IsAvailableLocally(published));
        Assert.True(hub.CanResolve(published));
        Assert.False(hub.CanResolve(placeholder));

        await hub.ResolveAsync(published);
        Assert.True(hub.IsAvailableLocally(published));
    }

    [Fact]
    public void OfflineHub_CannotResolveAnUncachedModel()
    {
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(Hash.Bytes(8));
        ImageAiOptions options = Options(cache, handler);
        options.Offline = true;
        using var hub = new ModelHub(options);

        Assert.False(hub.CanResolve(Descriptor("00")));
    }

    [Fact]
    public void ResolveUrl_AppliesTheBaseUrlOverride()
    {
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(Hash.Bytes(8));
        ImageAiOptions options = Options(cache, handler);
        options.BaseUrlOverride = "https://mirror.invalid/models/";
        using var hub = new ModelHub(options);

        Assert.Equal($"https://mirror.invalid/models/{FileName}", hub.ResolveUrl(Descriptor("00")));
    }

    [Fact]
    public void GetCachePath_SitsInsideTheCacheDirectory()
    {
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(Hash.Bytes(8));
        using var hub = new ModelHub(Options(cache, handler));

        Assert.Equal(Path.Combine(hub.CacheDirectory, FileName), hub.GetCachePath(Descriptor("00")));
        Assert.Equal(Path.GetFullPath(cache.Path), hub.CacheDirectory);
    }

    [Fact]
    public async Task ComputeSha256Async_MatchesTheReferenceHash()
    {
        byte[] body = Hash.Bytes(1000);
        using var cache = new TempCache();
        string path = Path.Combine(cache.Path, "blob.bin");
        await File.WriteAllBytesAsync(path, body);

        Assert.Equal(Hash.Sha256Hex(body), await ModelHub.ComputeSha256Async(path));
    }

    [Fact]
    public void DefaultCacheDirectory_IsAnAbsolutePath()
        => Assert.True(Path.IsPathRooted(ModelHub.DefaultCacheDirectory));

    [Fact]
    public async Task ADisposedHub_RefusesToResolve()
    {
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(Hash.Bytes(8));
        var hub = new ModelHub(Options(cache, handler));
        hub.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => hub.ResolveAsync(Descriptor("00")));
    }

    [Fact]
    public void NullArguments_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ModelHub(null!));
        using var hub = new ModelHub();
        Assert.Throws<ArgumentNullException>(() => hub.GetCachePath(null!));
        Assert.Throws<ArgumentNullException>(() => hub.IsAvailableLocally(null!));
    }

    // ----- Quantised variants -----

    [Fact]
    public async Task Quantize_PrefersThePublishedInt8Variant()
    {
        byte[] int8Body = Hash.Bytes(700, seed: 5);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(int8Body);
        ImageAiOptions options = Options(cache, handler);
        options.Quantize = true;
        using var hub = new ModelHub(options);

        ModelDescriptor descriptor = Descriptor(Hash.Sha256Hex(Hash.Bytes(700))) with
        {
            Int8FileName = "test-model.int8.onnx",
            Int8Sha256 = Hash.Sha256Hex(int8Body),
        };

        ResolvedModel resolved = await hub.ResolveAsync(descriptor);

        Assert.True(resolved.IsQuantized);
        Assert.Equal("test-model.int8.onnx", resolved.FileName);
    }

    [Fact]
    public async Task Quantize_FallsBackToFp32WhenNoInt8IsPublished()
    {
        byte[] body = Hash.Bytes(300);
        using var cache = new TempCache();
        var handler = FakeHttpHandler.Serving(body);
        ImageAiOptions options = Options(cache, handler);
        options.Quantize = true;
        using var hub = new ModelHub(options);

        ModelDescriptor descriptor = Descriptor(Hash.Sha256Hex(body)) with
        {
            Int8FileName = "test-model.int8.onnx",
            Int8Sha256 = null,
        };

        ResolvedModel resolved = await hub.ResolveAsync(descriptor);

        Assert.False(resolved.IsQuantized);
        Assert.Equal(FileName, resolved.FileName);
    }
}
