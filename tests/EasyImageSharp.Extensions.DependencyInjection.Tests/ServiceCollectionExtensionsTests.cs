using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EasyImageSharp.Extensions.DependencyInjection.Tests;

/// <summary>
/// <c>AddEasyImageSharp</c>: code and configuration registration, validation on start, reloads, and the
/// <see cref="DecoderOptions"/> / <see cref="ImageExecutionOptions"/> it resolves - including that the limits are
/// really enforced by the decoder and the processing pipeline.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static IConfigurationRoot Settings(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    // ----- Defaults -----

    [Fact]
    public void Defaults_MatchTheLibraryDefaults()
    {
        using ServiceProvider provider = new ServiceCollection().AddEasyImageSharp().BuildServiceProvider();

        DecoderOptions decoder = provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions();

        Assert.Equal(DecoderOptions.Default.MaxPixels, decoder.MaxPixels);
        Assert.Equal(DecoderOptions.Default.MaxTotalPixels, decoder.MaxTotalPixels);
        Assert.Equal(DecoderOptions.Default.MaxFrames, decoder.MaxFrames);
        Assert.Equal(DecoderOptions.Default.MaxEncodedBytes, decoder.MaxEncodedBytes);
        Assert.Empty(decoder.AdditionalFormats);
        Assert.NotNull(decoder.ExecutionOptions);
        Assert.Equal(long.MaxValue, decoder.ExecutionOptions!.MaxPixelBufferBytes);

        // No parallelism configured: the operation captures the process defaults, exactly as without DI.
        Assert.Null(decoder.ExecutionOptions.Configuration);
    }

    [Fact]
    public void RegistersTransientDecoderAndExecutionOptions()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o => o.MaxPixels = 1234)
            .BuildServiceProvider();

        DecoderOptions first = provider.GetRequiredService<DecoderOptions>();
        DecoderOptions second = provider.GetRequiredService<DecoderOptions>();

        Assert.Equal(1234, first.MaxPixels);
        Assert.NotSame(first, second);
        Assert.NotNull(provider.GetRequiredService<ImageExecutionOptions>());
    }

    [Fact]
    public void CallingTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddEasyImageSharp();
        services.AddEasyImageSharp(o => o.MaxFrames = 3);

        Assert.Single(services, d => d.ServiceType == typeof(IImageOptionsFactory));
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<EasyImageSharpOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(DecoderOptions));

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal(3, provider.GetRequiredService<DecoderOptions>().MaxFrames);
    }

    // ----- Code configuration -----

    [Fact]
    public void ConfigureAction_FlowsIntoDecoderAndExecutionOptions()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o =>
            {
                o.MaxPixels = 1_000_000;
                o.MaxTotalPixels = 3_000_000;
                o.MaxFrames = 7;
                o.MaxEncodedBytes = 65_536;
                o.MaxPixelBufferBytes = 8_000_000;
                o.MaxDegreeOfParallelism = 1;
                o.MinimumPixelsPerTask = 1024;
            })
            .BuildServiceProvider();
        IImageOptionsFactory factory = provider.GetRequiredService<IImageOptionsFactory>();

        DecoderOptions decoder = factory.CreateDecoderOptions();
        ImageExecutionOptions execution = factory.CreateExecutionOptions();

        Assert.Equal(1_000_000, decoder.MaxPixels);
        Assert.Equal(3_000_000, decoder.MaxTotalPixels);
        Assert.Equal(7, decoder.MaxFrames);
        Assert.Equal(65_536, decoder.MaxEncodedBytes);
        Assert.Equal(8_000_000, decoder.ExecutionOptions!.MaxPixelBufferBytes);
        Assert.Equal(8_000_000, execution.MaxPixelBufferBytes);
        Assert.NotNull(execution.Configuration);
        Assert.Equal(1, execution.Configuration!.MaxDegreeOfParallelism);
        Assert.Equal(1024, execution.Configuration.MinimumPixelsPerTask);
    }

    [Fact]
    public void PartialParallelism_FillsTheOtherSettingFromTheProcessDefaults()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o => o.MaxDegreeOfParallelism = 2)
            .BuildServiceProvider();

        Configuration? configuration = provider.GetRequiredService<IImageOptionsFactory>().CreateExecutionOptions().Configuration;

        Assert.NotNull(configuration);
        Assert.Equal(2, configuration!.MaxDegreeOfParallelism);
        Assert.Equal(Configuration.Default.MinimumPixelsPerTask, configuration.MinimumPixelsPerTask);
    }

    [Fact]
    public void CancellationToken_FlowsIntoBothOptionTypes()
    {
        using ServiceProvider provider = new ServiceCollection().AddEasyImageSharp().BuildServiceProvider();
        IImageOptionsFactory factory = provider.GetRequiredService<IImageOptionsFactory>();
        using var cts = new CancellationTokenSource();

        Assert.Equal(cts.Token, factory.CreateDecoderOptions(cts.Token).ExecutionOptions!.CancellationToken);
        Assert.Equal(cts.Token, factory.CreateExecutionOptions(cts.Token).CancellationToken);
    }

    // ----- Configuration binding -----

    [Fact]
    public void ConfigurationSection_BindsEveryKey()
    {
        IConfigurationRoot configuration = Settings(
            ("EasyImageSharp:MaxPixels", "4000000"),
            ("EasyImageSharp:MaxTotalPixels", "16000000"),
            ("EasyImageSharp:MaxFrames", "12"),
            ("EasyImageSharp:MaxEncodedBytes", "10485760"),
            ("EasyImageSharp:MaxPixelBufferBytes", "268435456"),
            ("EasyImageSharp:MaxDegreeOfParallelism", "1"),
            ("EasyImageSharp:MinimumPixelsPerTask", "4096"));

        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(configuration.GetSection(EasyImageSharpOptions.DefaultSectionName))
            .BuildServiceProvider();
        EasyImageSharpOptions options = provider.GetRequiredService<IOptions<EasyImageSharpOptions>>().Value;

        Assert.Equal(4_000_000, options.MaxPixels);
        Assert.Equal(16_000_000, options.MaxTotalPixels);
        Assert.Equal(12, options.MaxFrames);
        Assert.Equal(10_485_760, options.MaxEncodedBytes);
        Assert.Equal(268_435_456, options.MaxPixelBufferBytes);
        Assert.Equal(1, options.MaxDegreeOfParallelism);
        Assert.Equal(4096, options.MinimumPixelsPerTask);
    }

    [Fact]
    public void ConfigurationSection_MissingKeysKeepDefaults()
    {
        IConfigurationRoot configuration = Settings(("EasyImageSharp:MaxFrames", "2"));

        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(configuration.GetSection("EasyImageSharp"))
            .BuildServiceProvider();
        EasyImageSharpOptions options = provider.GetRequiredService<IOptions<EasyImageSharpOptions>>().Value;

        Assert.Equal(2, options.MaxFrames);
        Assert.Equal(DecoderOptions.DefaultMaxPixels, options.MaxPixels);
        Assert.Null(options.MaxDegreeOfParallelism);
    }

    [Fact]
    public void ConfigureAction_RegisteredAfterBinding_Wins()
    {
        IConfigurationRoot configuration = Settings(("EasyImageSharp:MaxPixels", "100"));

        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(configuration.GetSection("EasyImageSharp"))
            .AddEasyImageSharp(o => o.MaxPixels = 200)
            .BuildServiceProvider();

        Assert.Equal(200, provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions().MaxPixels);
    }

    [Fact]
    public void ConfigurationReload_IsPickedUpByTheFactory()
    {
        IConfigurationRoot configuration = Settings(("EasyImageSharp:MaxPixels", "1000"));
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(configuration.GetSection("EasyImageSharp"))
            .BuildServiceProvider();
        IImageOptionsFactory factory = provider.GetRequiredService<IImageOptionsFactory>();
        Assert.Equal(1000, factory.CreateDecoderOptions().MaxPixels);

        configuration["EasyImageSharp:MaxPixels"] = "2000";
        configuration.Reload();

        Assert.Equal(2000, factory.CreateDecoderOptions().MaxPixels);
    }

    // ----- Validation -----

    [Theory]
    [InlineData("MaxPixels", "0")]
    [InlineData("MaxTotalPixels", "-1")]
    [InlineData("MaxFrames", "0")]
    [InlineData("MaxEncodedBytes", "0")]
    [InlineData("MaxPixelBufferBytes", "-5")]
    [InlineData("MaxDegreeOfParallelism", "0")]
    [InlineData("MinimumPixelsPerTask", "0")]
    public void InvalidValue_FailsStartupValidation(string key, string value)
    {
        IConfigurationRoot configuration = Settings(("EasyImageSharp:" + key, value));
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(configuration.GetSection("EasyImageSharp"))
            .BuildServiceProvider();

        // IStartupValidator is what the generic host runs for ValidateOnStart.
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains(ex.Failures, failure => failure.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidValue_FailsWhenOptionsAreResolved()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o => o.MaxPixels = -1)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions());
    }

    [Fact]
    public void UnparsableValue_FailsStartup()
    {
        IConfigurationRoot configuration = Settings(("EasyImageSharp:MaxPixels", "lots"));
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(configuration.GetSection("EasyImageSharp"))
            .BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void ValidOptions_PassStartupValidation()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o => o.MaxPixels = 10)
            .BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    // ----- The limits are enforced -----

    [Fact]
    public void DecoderOptions_FromContainer_EnforceMaxPixels()
    {
        byte[] png = Png(100, 100);
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o => o.MaxPixels = 100 * 100 - 1)
            .BuildServiceProvider();

        DecoderOptions options = provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions();

        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(png, options));
    }

    [Fact]
    public void DecoderOptions_FromContainer_EnforceMaxEncodedBytes()
    {
        byte[] png = Png(16, 16);
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o => o.MaxEncodedBytes = png.Length - 1)
            .BuildServiceProvider();

        DecoderOptions options = provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions();

        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(png, options));
    }

    [Fact]
    public void DecoderOptions_FromContainer_HonourCancellation()
    {
        byte[] png = Png(16, 16);
        using ServiceProvider provider = new ServiceCollection().AddEasyImageSharp().BuildServiceProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        DecoderOptions options = provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions(cts.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => Image.Load<Rgba32>(png, options));
    }

    [Fact]
    public void ExecutionOptions_FromContainer_EnforcePixelBufferBudget()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharp(o => o.MaxPixelBufferBytes = 64)
            .BuildServiceProvider();
        ImageExecutionOptions execution = provider.GetRequiredService<IImageOptionsFactory>().CreateExecutionOptions();
        using var image = new Image<Rgba32>(32, 32);

        // A 64x64 Rgba32 resize needs 16 KiB of new pixels, far over the 64-byte budget.
        Assert.Throws<ImageSizeLimitExceededException>(() => image.Mutate(execution, ctx => ctx.Resize(64, 64)));
    }

    // ----- Custom formats -----

    [Fact]
    public void AddEasyImageSharpFormat_AppearsInAdditionalFormats_Once()
    {
        ImageFormat format = ImageFormat.Create(
            "TEST", "image/x-test", new[] { "tst" }, static data => data.Length > 0 && data[0] == 0x42, static () => new RejectingDecoder());

        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharpFormat(format)
            .AddEasyImageSharpFormat(format)
            .BuildServiceProvider();

        DecoderOptions options = provider.GetRequiredService<IImageOptionsFactory>().CreateDecoderOptions();

        Assert.Same(format, Assert.Single(options.AdditionalFormats));
        Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(new byte[] { 0x42, 0x00 }, options));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => EasyImageSharpServiceCollectionExtensions.AddEasyImageSharp(null!));
        Assert.Throws<ArgumentNullException>(() => services.AddEasyImageSharp((Action<EasyImageSharpOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddEasyImageSharp((IConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddEasyImageSharpFormat(null!));
    }

    /// <summary>A decoder for the test format that refuses every input.</summary>
    private sealed class RejectingDecoder : IImageDecoder
    {
        public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
            where TPixel : unmanaged, IPixel<TPixel>
            => throw new NotSupportedException("Test format.");

        public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
            => throw new NotSupportedException("Test format.");
    }
}
