using EasyImageSharp.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace EasyImageSharp.Web.Tests;

/// <summary>Registration, configuration binding, start-up validation and the health check.</summary>
public class HostingTests : IClassFixture<TestImages>
{
    private static readonly string ValidKey = Convert.ToBase64String(new byte[32]);

    private readonly TestImages images;

    public HostingTests(TestImages images) => this.images = images;

    [Fact]
    public void Configuration_BindsEveryKind_OfSetting()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EasyImageSharp:Web:RequestPathPrefix"] = "/img",
                ["EasyImageSharp:Web:SourceRootPath"] = "/srv/originals",
                ["EasyImageSharp:Web:SigningKeys:0"] = ValidKey,
                ["EasyImageSharp:Web:AllowedFormats:0"] = "jpeg",
                ["EasyImageSharp:Web:AllowedFormats:1"] = "webp",
                ["EasyImageSharp:Web:NegotiatedFormats:0"] = "webp",
                ["EasyImageSharp:Web:AllowedSizes:0"] = "320x240",
                ["EasyImageSharp:Web:MaxWidth"] = "2000",
                ["EasyImageSharp:Web:MaxHeight"] = "1500",
                ["EasyImageSharp:Web:MaxOutputPixels"] = "3000000",
                ["EasyImageSharp:Web:MaxSourceBytes"] = "1048576",
                ["EasyImageSharp:Web:MaxSourcePixels"] = "20000000",
                ["EasyImageSharp:Web:EnableFormatNegotiation"] = "false",
                ["EasyImageSharp:Web:AllowUpscaling"] = "true",
                ["EasyImageSharp:Web:AutoOrient"] = "false",
                ["EasyImageSharp:Web:StripMetadata"] = "false",
                ["EasyImageSharp:Web:DefaultQuality"] = "80",
                ["EasyImageSharp:Web:DefaultBackgroundColor"] = "#000",
                ["EasyImageSharp:Web:BrowserCacheMaxAge"] = "01:00:00",
                ["EasyImageSharp:Web:CacheMaxAge"] = "2.00:00:00",
                ["EasyImageSharp:Web:MaxConcurrentProcessing"] = "3",
                ["EasyImageSharp:Web:MaxQueueLength"] = "7",
            })
            .Build();

        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharpWeb(configuration.GetSection(EasyImageSharpWebOptions.DefaultSectionName))
            .Services
            .BuildServiceProvider();
        EasyImageSharpWebOptions options = provider.GetRequiredService<IOptions<EasyImageSharpWebOptions>>().Value;

        Assert.Equal("/img", options.RequestPathPrefix);
        Assert.Equal("/srv/originals", options.SourceRootPath);
        Assert.Equal(new[] { ValidKey }, options.SigningKeys);
        Assert.Equal(new[] { "jpeg", "webp" }, options.AllowedFormats);
        Assert.Equal(new[] { "webp" }, options.NegotiatedFormats);
        Assert.Equal(new[] { "320x240" }, options.AllowedSizes);
        Assert.Equal(2000, options.MaxWidth);
        Assert.Equal(1500, options.MaxHeight);
        Assert.Equal(3_000_000, options.MaxOutputPixels);
        Assert.Equal(1_048_576, options.MaxSourceBytes);
        Assert.Equal(20_000_000, options.MaxSourcePixels);
        Assert.False(options.EnableFormatNegotiation);
        Assert.True(options.AllowUpscaling);
        Assert.False(options.AutoOrient);
        Assert.False(options.StripMetadata);
        Assert.Equal(80, options.DefaultQuality);
        Assert.Equal("#000", options.DefaultBackgroundColor);
        Assert.Equal(TimeSpan.FromHours(1), options.BrowserCacheMaxAge);
        Assert.Equal(TimeSpan.FromDays(2), options.CacheMaxAge);
        Assert.Equal(3, options.MaxConcurrentProcessing);
        Assert.Equal(7, options.MaxQueueLength);

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Defaults_AreValidAndRegisterTheCoreOptions()
    {
        using ServiceProvider provider = new ServiceCollection().AddEasyImageSharpWeb().Services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.NotNull(provider.GetRequiredService<IImageOptionsFactory>());
        EasyImageSharpWebOptions options = provider.GetRequiredService<IOptions<EasyImageSharpWebOptions>>().Value;
        Assert.Equal("/", options.RequestPathPrefix);
        Assert.Empty(options.SigningKeys);
        Assert.Equal(Environment.ProcessorCount, options.MaxConcurrentProcessing);
    }

    [Fact]
    public void CallingTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddEasyImageSharpWeb();
        services.AddEasyImageSharpWeb(o => o.MaxWidth = 100);

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<EasyImageSharpWebOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(ImageUrlSigner));
        Assert.Single(services, d => d.ServiceType == typeof(Internal.ImageProcessingLimiter));
    }

    public static TheoryData<string, Action<EasyImageSharpWebOptions>> InvalidOptions => new()
    {
        { "RequestPathPrefix", o => o.RequestPathPrefix = "images" },
        { "MaxWidth", o => o.MaxWidth = 0 },
        { "MaxHeight", o => o.MaxHeight = 70_000 },
        { "MaxOutputPixels", o => o.MaxOutputPixels = 0 },
        { "MaxSourceBytes", o => o.MaxSourceBytes = 0 },
        { "MaxSourcePixels", o => o.MaxSourcePixels = -1 },
        { "MaxConcurrentProcessing", o => o.MaxConcurrentProcessing = 0 },
        { "MaxQueueLength", o => o.MaxQueueLength = -1 },
        { "DefaultQuality", o => o.DefaultQuality = 0 },
        { "BrowserCacheMaxAge", o => o.BrowserCacheMaxAge = TimeSpan.FromSeconds(-1) },
        { "CacheMaxAge", o => o.CacheMaxAge = TimeSpan.Zero },
        { "SigningKeys", o => o.SigningKeys.Add(Convert.ToBase64String(new byte[16])) },
        { "SigningKeys", o => o.SigningKeys.Add("%%% not base64 %%%") },
        { "AllowedFormats", o => o.AllowedFormats.Add("heic") },
        { "NegotiatedFormats", o => o.NegotiatedFormats.Add("auto") },
        { "AllowedSizes", o => o.AllowedSizes.Add("big") },
        { "AllowedSizes", o => o.AllowedSizes.Add("x") },
        { "AllowedSizes", o => o.AllowedSizes.Add("10x-5") },
        { "DefaultBackgroundColor", o => o.DefaultBackgroundColor = "not-a-colour" },
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptions_FailStartupValidation(string property, Action<EasyImageSharpWebOptions> configure)
    {
        using ServiceProvider provider = new ServiceCollection().AddEasyImageSharpWeb(configure).Services.BuildServiceProvider();

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, failure => failure.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidSigningKey_FailureDoesNotEchoTheKey()
    {
        string shortKey = Convert.ToBase64String(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        using ServiceProvider provider = new ServiceCollection()
            .AddEasyImageSharpWeb(o => o.SigningKeys.Add(shortKey))
            .Services
            .BuildServiceProvider();

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.DoesNotContain(ex.Failures, failure => failure.Contains(shortKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidOptions_FailHostStart()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => WebTestHost.StartAsync(this.images, o => o.MaxWidth = 0));
    }

    [Fact]
    public void UseEasyImageSharp_WithoutServices_Throws()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => app.UseEasyImageSharp());
        Assert.Contains("AddEasyImageSharpWeb", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var services = new ServiceCollection();
        IEasyImageSharpWebBuilder builder = services.AddEasyImageSharpWeb();

        Assert.Throws<ArgumentNullException>(() => EasyImageSharpWebServiceCollectionExtensions.AddEasyImageSharpWeb(null!));
        Assert.Throws<ArgumentNullException>(() => services.AddEasyImageSharpWeb((Action<EasyImageSharpWebOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddEasyImageSharpWeb((IConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => builder.AddSourceProvider(null!));
        Assert.Throws<ArgumentNullException>(() => builder.AddOutputFormat(null!));
        Assert.Throws<ArgumentException>(() => builder.UsePhysicalCache(" "));
        Assert.Throws<ArgumentNullException>(() => new FileProviderImageSourceProvider(null!));
    }

    [Fact]
    public async Task HealthCheck_IsHealthyWithAWritableCache()
    {
        string cacheRoot = Path.Combine(this.images.Outside, "hc-" + Guid.NewGuid().ToString("N"));
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            build: b => b.UsePhysicalCache(cacheRoot),
            services: s => s.AddHealthChecks().AddEasyImageSharp(tags: new[] { "ready" }));

        HealthReport report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        HealthReportEntry entry = report.Entries["easyimagesharp"];
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal(Path.GetFullPath(cacheRoot), entry.Data["cacheRoot"]);
        Assert.Contains("ready", entry.Tags);
        Assert.Empty(Directory.GetFiles(cacheRoot));
    }

    [Fact]
    public async Task HealthCheck_ReportsAnUnwritableCache()
    {
        string blocked = Path.Combine(this.images.Outside, "hc-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "not a directory");
        await using WebTestHost host = await WebTestHost.StartAsync(
            this.images,
            build: b => b.UsePhysicalCache(blocked),
            services: s => s.AddHealthChecks().AddEasyImageSharp(name: "images", failureStatus: HealthStatus.Degraded));

        HealthReport report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Degraded, report.Entries["images"].Status);
    }
}
