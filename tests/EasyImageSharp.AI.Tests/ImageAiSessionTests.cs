using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Session lifetime: one cached <see cref="InferenceSession"/> per model file, disposal releasing them, option
/// snapshotting and execution-provider resolution.
/// </summary>
/// <remarks>
/// Which accelerators exist is decided by the ONNX Runtime native package the test host resolved, and that
/// differs per platform: the base package is CPU-only on Windows and Linux but carries the CoreML provider on
/// macOS. So the provider tests below assert the contract — nothing throws, and the provider that ends up active
/// is one the loaded runtime actually reports — rather than hard-coding CPU, which only holds on some agents.
/// </remarks>
public class ImageAiSessionTests
{
    private static ImageModelContract Rgb => new() { InputChannels = 3 };

    // ----- Session caching -----

    [Fact]
    public void TheSameModelFile_IsLoadedOnce()
    {
        using var ai = new ImageAiSession();
        Assert.Equal(0, ai.LoadedSessionCount);

        InferenceSession first = ai.GetSession(TestModels.IdentityRgb);
        InferenceSession second = ai.GetSession(TestModels.IdentityRgb);

        Assert.Same(first, second);
        Assert.Equal(1, ai.LoadedSessionCount);
    }

    [Fact]
    public void TheSamePathSpelledDifferently_SharesOneSession()
    {
        using var ai = new ImageAiSession();
        string direct = TestModels.IdentityRgb;
        string indirect = Path.Combine(Path.GetDirectoryName(direct)!, ".", Path.GetFileName(direct));

        InferenceSession first = ai.GetSession(direct);
        InferenceSession second = ai.GetSession(indirect);

        Assert.Same(first, second);
        Assert.Equal(1, ai.LoadedSessionCount);
    }

    [Fact]
    public void DifferentModels_GetTheirOwnSessions()
    {
        using var ai = new ImageAiSession();
        InferenceSession rgb = ai.GetSession(TestModels.IdentityRgb);
        InferenceSession gray = ai.GetSession(TestModels.IdentityGray);

        Assert.NotSame(rgb, gray);
        Assert.Equal(2, ai.LoadedSessionCount);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneSession()
    {
        using var ai = new ImageAiSession();

        InferenceSession[] sessions = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => ai.GetSessionAsync(TestModels.IdentityRgb)));

        Assert.All(sessions, session => Assert.Same(sessions[0], session));
        Assert.Equal(1, ai.LoadedSessionCount);
    }

    [Fact]
    public void RepeatedRuns_ReuseTheLoadedSession()
    {
        using var ai = new ImageAiSession();
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(8, 8);

        for (int i = 0; i < 3; i++)
        {
            using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb);
            Assert.True(TestImages.PixelsEqual(source, result));
        }

        Assert.Equal(1, ai.LoadedSessionCount);
    }

    // ----- Disposal -----

    [Fact]
    public void Disposal_ReleasesEverySession()
    {
        var ai = new ImageAiSession();
        ai.GetSession(TestModels.IdentityRgb);
        ai.GetSession(TestModels.IdentityGray);
        Assert.Equal(2, ai.LoadedSessionCount);

        ai.Dispose();

        Assert.Equal(0, ai.LoadedSessionCount);
        Assert.Throws<ObjectDisposedException>(() => ai.GetSession(TestModels.IdentityRgb));
    }

    [Fact]
    public void Disposal_IsIdempotent()
    {
        var ai = new ImageAiSession();
        ai.GetSession(TestModels.IdentityGray);
        ai.Dispose();
        ai.Dispose();

        Assert.Equal(0, ai.LoadedSessionCount);
    }

    [Fact]
    public async Task ADisposedSession_AlsoDisposesItsHub()
    {
        var ai = new ImageAiSession();
        ModelHub hub = ai.Hub;
        ai.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => hub.ResolveAsync(ModelRegistry.DocumentOrientation));
    }

    [Fact]
    public void AFailedLoad_IsNotCached()
    {
        using var ai = new ImageAiSession();
        string missing = Path.Combine(AppContext.BaseDirectory, "Models", "does_not_exist.onnx");

        Assert.Throws<FileNotFoundException>(() => ai.GetSession(missing));
        Assert.Equal(0, ai.LoadedSessionCount);

        // A later successful load still works.
        ai.GetSession(TestModels.IdentityRgb);
        Assert.Equal(1, ai.LoadedSessionCount);
    }

    [Fact]
    public void GetSession_RejectsAnEmptyPath()
    {
        using var ai = new ImageAiSession();
        Assert.Throws<ArgumentException>(() => ai.GetSession(string.Empty));
        Assert.Throws<ArgumentNullException>(() => ai.GetSession((string)null!));
        Assert.Throws<ArgumentNullException>(() => ai.GetSession((ModelDescriptor)null!));
    }

    // ----- Execution providers -----

    /// <summary>
    /// The native provider name ONNX Runtime reports for each of our provider values.
    /// </summary>
    private static string NativeName(ExecutionProvider provider) => provider switch
    {
        ExecutionProvider.Cuda => "CUDAExecutionProvider",
        ExecutionProvider.DirectML => "DmlExecutionProvider",
        ExecutionProvider.CoreML => "CoreMLExecutionProvider",
        _ => "CPUExecutionProvider",
    };

    /// <summary>
    /// Whether the ONNX Runtime actually loaded by this test run carries the given provider. Asking the runtime
    /// is the only honest way to express these expectations, because the answer depends on the host platform.
    /// </summary>
    private static bool RuntimeHas(ExecutionProvider provider) =>
        Array.IndexOf(OrtEnv.Instance().GetAvailableProviders(), NativeName(provider)) >= 0;

    [Fact]
    public void AutoProvider_ResolvesToASupportedProviderWithoutThrowing()
    {
        var logs = new List<string>();
        using var ai = new ImageAiSession(new ImageAiOptions
        {
            ExecutionProvider = ExecutionProvider.Auto,
            Log = logs.Add,
        });

        // Auto must resolve to something concrete, and never to a provider the loaded runtime does not carry.
        Assert.NotEqual(ExecutionProvider.Auto, ai.ActiveProvider);
        Assert.True(
            RuntimeHas(ai.ActiveProvider),
            $"Auto resolved to {ai.ActiveProvider}, which the loaded runtime does not report as available.");

        // And whatever it chose, inference still runs and the identity model round-trips exactly.
        using Image<Rgb24> source = TestImages.Noise<Rgb24>(6, 6);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb);
        Assert.True(TestImages.PixelsEqual(source, result));
        Assert.NotEqual(ExecutionProvider.Auto, ai.ActiveProvider);
        Assert.True(RuntimeHas(ai.ActiveProvider));
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void ExplicitCpu_IsUsedAsIs()
    {
        using var ai = new ImageAiSession(new ImageAiOptions { ExecutionProvider = ExecutionProvider.Cpu });
        ai.GetSession(TestModels.IdentityGray);

        Assert.Equal(ExecutionProvider.Cpu, ai.ActiveProvider);
    }

    /// <summary>
    /// Asking for an accelerator must never throw, which is what makes the same code run on a build machine
    /// without a GPU. If the loaded runtime carries the provider it may be used; if it does not, the session must
    /// degrade to CPU. Either way the inference result is the same.
    /// </summary>
    [Theory]
    [InlineData(ExecutionProvider.Cuda)]
    [InlineData(ExecutionProvider.DirectML)]
    [InlineData(ExecutionProvider.CoreML)]
    public void ARequestedAccelerator_IsUsedIfPresentAndDegradesToCpuOtherwise(ExecutionProvider requested)
    {
        var logs = new List<string>();
        using var ai = new ImageAiSession(new ImageAiOptions { ExecutionProvider = requested, Log = logs.Add });

        using Image<L8> source = TestImages.Noise<L8>(6, 6);
        using Image<L8> result = ai.RunImageToImage(TestModels.IdentityGray, source, new ImageModelContract { InputChannels = 1 });

        // The accelerator must never change the answer.
        Assert.True(TestImages.PixelsEqual(source, result));

        if (RuntimeHas(requested))
        {
            // Present in the runtime: either it attached, or the device failed to initialise and we are on CPU.
            Assert.True(
                ai.ActiveProvider == requested || ai.ActiveProvider == ExecutionProvider.Cpu,
                $"Requested {requested}, which the runtime reports; active provider was {ai.ActiveProvider}.");
        }
        else
        {
            // Absent from the runtime: CPU is the only correct outcome, and it must be reached without throwing.
            Assert.Equal(ExecutionProvider.Cpu, ai.ActiveProvider);
        }
    }

    [Fact]
    public void ThreadCountsAreAccepted()
    {
        using var ai = new ImageAiSession(new ImageAiOptions
        {
            ExecutionProvider = ExecutionProvider.Cpu,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
        });

        using Image<Rgb24> source = TestImages.Noise<Rgb24>(8, 8);
        using Image<Rgb24> result = ai.RunImageToImage(TestModels.IdentityRgb, source, Rgb);
        Assert.True(TestImages.PixelsEqual(source, result));
    }

    // ----- Options -----

    [Fact]
    public void OptionsAreSnapshottedAtConstruction()
    {
        var options = new ImageAiOptions { Offline = false, DeviceId = 0 };
        using var ai = new ImageAiSession(options);

        options.Offline = true;
        options.DeviceId = 3;
        options.ModelPathOverrides["late"] = "late.onnx";

        Assert.False(ai.Options.Offline);
        Assert.Equal(0, ai.Options.DeviceId);
        Assert.False(ai.Options.ModelPathOverrides.ContainsKey("late"));
        Assert.NotSame(options, ai.Options);
    }

    [Fact]
    public void OptionsCloneCarriesTheOverridesAcross()
    {
        var options = new ImageAiOptions();
        options.ModelPathOverrides["doc-orientation"] = "custom.onnx";
        using var ai = new ImageAiSession(options);

        Assert.Equal("custom.onnx", ai.Options.ModelPathOverrides["doc-orientation"]);
    }

    [Fact]
    public void NullOptions_AreRejected()
        => Assert.Throws<ArgumentNullException>(() => new ImageAiSession(null!));

    [Fact]
    public void DefaultOptions_HaveTheDocumentedValues()
    {
        var options = new ImageAiOptions();

        Assert.Equal(ExecutionProvider.Auto, options.ExecutionProvider);
        Assert.False(options.Offline);
        Assert.False(options.Quantize);
        Assert.False(options.AllowUnverifiedModels);
        Assert.False(options.AllowInsecureModelSource);
        Assert.Equal(3, options.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryBaseDelay);
        Assert.Empty(options.ModelPathOverrides);
    }

    // ----- Warm-up -----

    [Fact]
    public async Task WarmUp_WithAnEmptyModelList_DoesNothing()
    {
        using var ai = new ImageAiSession();
        await ai.WarmUpAsync(Array.Empty<ModelDescriptor>());

        Assert.Equal(0, ai.LoadedSessionCount);
    }

    [Fact]
    public async Task WarmUp_RejectsANullModelList()
    {
        using var ai = new ImageAiSession();
        await Assert.ThrowsAsync<ArgumentNullException>(() => ai.WarmUpAsync((IEnumerable<ModelDescriptor>)null!));
    }

    /// <summary>
    /// Offline warm-up must be a no-op rather than an error when nothing is cached: every registry model is
    /// simply unresolvable, so the filtered set is empty.
    /// </summary>
    [Fact]
    public async Task WarmUp_OfflineWithAnEmptyCache_LoadsNothing()
    {
        using var cache = new TempCache();
        using var ai = new ImageAiSession(new ImageAiOptions { Offline = true, CachePath = cache.Path });

        await ai.WarmUpAsync();

        Assert.Equal(0, ai.LoadedSessionCount);
    }
}
