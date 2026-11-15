using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace EasyImageSharp.Web.Tests;

/// <summary>
/// The concurrency limiter (slots, queue, 503) and cancellation on client abort. A <see cref="GatedSourceProvider"/>
/// parks requests inside their processing slot until the test releases them.
/// </summary>
public class ConcurrencyTests : IClassFixture<TestImages>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TestImages images;

    public ConcurrencyTests(TestImages images) => this.images = images;

    [Fact]
    public async Task SaturatedWithoutQueue_Returns503WithRetryAfter()
    {
        var gate = new GatedSourceProvider(File.ReadAllBytes(this.images.PathOf("photo.jpg")));
        await using WebTestHost host = await StartAsync(this.images, gate, slots: 1, queue: 0);

        Task<HttpResponseMessage> first = host.GetAsync("/a.jpg?width=10");
        await gate.WaitForEntriesAsync(1);
        using HttpResponseMessage rejected = await host.GetAsync("/b.jpg?width=10");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter?.Delta);

        gate.Release();
        using HttpResponseMessage completed = await first.WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Fact]
    public async Task QueuedRequests_RunWhenASlotFrees_AndTheQueueIsBounded()
    {
        var gate = new GatedSourceProvider(File.ReadAllBytes(this.images.PathOf("photo.jpg")));
        await using WebTestHost host = await StartAsync(this.images, gate, slots: 1, queue: 1);

        Task<HttpResponseMessage> running = host.GetAsync("/a.jpg?width=10");
        await gate.WaitForEntriesAsync(1);
        Task<HttpResponseMessage> queued = host.GetAsync("/b.jpg?width=10");
        await WaitForQueuedAsync(host, 1);
        using HttpResponseMessage rejected = await host.GetAsync("/c.jpg?width=10");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);

        gate.Release();
        using HttpResponseMessage first = await running.WaitAsync(Timeout);
        using HttpResponseMessage second = await queued.WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task CacheHitsAndNotModified_DoNotNeedASlot()
    {
        var gate = new GatedSourceProvider(File.ReadAllBytes(this.images.PathOf("photo.jpg")));
        string cacheRoot = Path.Combine(this.images.Outside, "cc-" + Guid.NewGuid().ToString("N"));
        await using WebTestHost host = await StartAsync(this.images, gate, slots: 1, queue: 0, cacheRoot);

        // Warm the cache for one URL, then park another request in the only slot.
        gate.Release();
        using (HttpResponseMessage warm = await host.GetAsync("/warm.jpg?width=10"))
        {
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        }

        var blocker = new GatedSourceProvider(File.ReadAllBytes(this.images.PathOf("photo.jpg")));
        await using WebTestHost blocked = await StartAsync(this.images, blocker, slots: 1, queue: 0, cacheRoot);
        Task<HttpResponseMessage> parked = blocked.GetAsync("/other.jpg?width=11");
        await blocker.WaitForEntriesAsync(1);

        using HttpResponseMessage hit = await blocked.GetAsync("/warm.jpg?width=10");
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/warm.jpg?width=10");
        conditional.Headers.IfNoneMatch.Add(hit.Headers.ETag!);
        using HttpResponseMessage notModified = await blocked.Client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        blocker.Release();
        using HttpResponseMessage done = await parked.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ClientAbort_CancelsTheWorkAndReleasesTheSlot()
    {
        var gate = new GatedSourceProvider(File.ReadAllBytes(this.images.PathOf("photo.jpg")));
        await using WebTestHost host = await StartAsync(this.images, gate, slots: 1, queue: 0);

        using var abort = new CancellationTokenSource();
        Task<HttpResponseMessage> aborted = host.GetAsync("/a.jpg?width=10", cancellationToken: abort.Token);
        await gate.WaitForEntriesAsync(1);
        abort.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted.WaitAsync(Timeout));
        await gate.Cancelled.Task.WaitAsync(Timeout);

        // The slot is free again: with no queue, a still-held slot would make this a 503.
        gate.Release();
        using HttpResponseMessage next = await host.GetAsync("/b.jpg?width=10").WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task ClientAbortWhileQueued_LeavesTheQueue()
    {
        var gate = new GatedSourceProvider(File.ReadAllBytes(this.images.PathOf("photo.jpg")));
        await using WebTestHost host = await StartAsync(this.images, gate, slots: 1, queue: 1);

        Task<HttpResponseMessage> running = host.GetAsync("/a.jpg?width=10");
        await gate.WaitForEntriesAsync(1);
        using var abort = new CancellationTokenSource();
        Task<HttpResponseMessage> queued = host.GetAsync("/b.jpg?width=10", cancellationToken: abort.Token);
        await WaitForQueuedAsync(host, 1);
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        await WaitForQueuedAsync(host, 0);

        // The queue has room again, so this one waits instead of being rejected.
        Task<HttpResponseMessage> third = host.GetAsync("/c.jpg?width=10");
        await WaitForQueuedAsync(host, 1);
        gate.Release();
        using HttpResponseMessage first = await running.WaitAsync(Timeout);
        using HttpResponseMessage last = await third.WaitAsync(Timeout);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
    }

    [Fact]
    public async Task HealthCheck_ReportsDegradedWhileSaturated()
    {
        var gate = new GatedSourceProvider(File.ReadAllBytes(this.images.PathOf("photo.jpg")));
        await using WebTestHost host = await StartAsync(this.images, gate, slots: 1, queue: 0);
        HealthCheckService health = host.Services.GetRequiredService<HealthCheckService>();

        HealthReport idle = await health.CheckHealthAsync();
        Task<HttpResponseMessage> parked = host.GetAsync("/a.jpg?width=10");
        await gate.WaitForEntriesAsync(1);
        HealthReport busy = await health.CheckHealthAsync();
        gate.Release();
        using HttpResponseMessage done = await parked.WaitAsync(Timeout);

        Assert.Equal(HealthStatus.Healthy, idle.Status);
        Assert.Equal(HealthStatus.Degraded, busy.Status);
        Assert.Equal(0L, busy.Entries["easyimagesharp"].Data["availablePermits"]);
    }

    private static Task<WebTestHost> StartAsync(TestImages images, GatedSourceProvider gate, int slots, int queue, string? cacheRoot = null)
        => WebTestHost.StartAsync(
            images,
            o =>
            {
                o.MaxConcurrentProcessing = slots;
                o.MaxQueueLength = queue;
            },
            build: b =>
            {
                b.AddSourceProvider(gate);
                if (cacheRoot is not null)
                {
                    b.UsePhysicalCache(cacheRoot);
                }
            },
            services: s => s.AddHealthChecks().AddEasyImageSharp());

    private static async Task WaitForQueuedAsync(WebTestHost host, int count)
    {
        var limiter = host.Services.GetRequiredService<Internal.ImageProcessingLimiter>();
        using var timeout = new CancellationTokenSource(Timeout);
        while (limiter.GetCounts().Queued != count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
