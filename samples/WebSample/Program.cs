using System.Net;
using System.Security.Cryptography;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// A self-checking image server: one generated original, the middleware in front of static files, and a handful of
// requests a browser or CDN would make. Exits 0 when every check passes.
string root = Path.Combine(Path.GetTempPath(), "easyimagesharp-websample-" + Guid.NewGuid().ToString("N"));
string webRoot = Path.Combine(root, "wwwroot");
Directory.CreateDirectory(webRoot);
int failures = 0;

void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (!condition)
    {
        failures++;
    }
}

try
{
    using (var original = new Image<Rgba32>(640, 480, new Rgba32(30, 120, 200, 255)))
    {
        original.SaveAsJpeg(Path.Combine(webRoot, "photo.jpg"));
    }

    WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    {
        ContentRootPath = root,
        WebRootPath = webRoot,
    });
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();

    // Settings as they would come from appsettings.json: decoder limits and parallelism for every decode (one thread
    // per request suits a server), then signed URLs only. A real application reads the key from a secret store. Both
    // sections bind through the configuration binding source generator, which this sample exercises when trimmed.
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["EasyImageSharp:MaxPixels"] = "40000000",
        ["EasyImageSharp:MaxFrames"] = "50",
        ["EasyImageSharp:MaxDegreeOfParallelism"] = "1",
        ["EasyImageSharp:Web:SigningKeys:0"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["EasyImageSharp:Web:MaxWidth"] = "2048",
    });
    builder.Services.AddEasyImageSharp(builder.Configuration.GetSection("EasyImageSharp"));

    // A disk cache under the content root, and WebP for browsers that ask for it (the default).
    builder.Services
        .AddEasyImageSharpWeb(builder.Configuration.GetSection("EasyImageSharp:Web"))
        .UsePhysicalCache("image-cache");
    builder.Services.AddHealthChecks().AddEasyImageSharp();

    WebApplication app = builder.Build();
    app.UseEasyImageSharp();
    app.UseStaticFiles();
    app.MapHealthChecks("/health");
    await app.StartAsync();

    try
    {
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        ImageUrlSigner signer = app.Services.GetRequiredService<ImageUrlSigner>();
        string url = signer.CreateSignedUrl("/photo.jpg", new Dictionary<string, string> { ["width"] = "320" });

        using var webpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        webpRequest.Headers.TryAddWithoutValidation("Accept", "image/webp,*/*");
        using HttpResponseMessage webp = await http.SendAsync(webpRequest);
        byte[] webpBytes = await webp.Content.ReadAsByteArrayAsync();
        Check(webp.StatusCode == HttpStatusCode.OK && webp.Content.Headers.ContentType?.MediaType == "image/webp", "signed resize negotiates WebP");
        Check(webpBytes.Length > 0 && Image.Identify(webpBytes).Width == 320, "the output is 320 pixels wide");
        Check(webp.Headers.Vary.Contains("Accept"), "the negotiated response varies by Accept");

        using HttpResponseMessage jpeg = await http.GetAsync(url);
        Check(jpeg.Content.Headers.ContentType?.MediaType == "image/jpeg", "a client without WebP gets JPEG");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(jpeg.Headers.ETag!);
        using HttpResponseMessage notModified = await http.SendAsync(conditional);
        Check(notModified.StatusCode == HttpStatusCode.NotModified, "a matching If-None-Match is answered with 304");

        using HttpResponseMessage unsigned = await http.GetAsync("/photo.jpg?width=320");
        Check(unsigned.StatusCode == HttpStatusCode.Forbidden, "an unsigned command is refused with 403");

        using HttpResponseMessage tooWide = await http.GetAsync(signer.CreateSignedUrl("/photo.jpg", new Dictionary<string, string> { ["width"] = "3000" }));
        Check(tooWide.StatusCode == HttpStatusCode.UnprocessableEntity, "a width over the configured MaxWidth is refused with 422");

        using HttpResponseMessage original = await http.GetAsync("/photo.jpg");
        Check(
            original.StatusCode == HttpStatusCode.OK
                && (await original.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(webRoot, "photo.jpg"))),
            "the original passes through to static files");

        Check(Directory.GetFiles(Path.Combine(root, "image-cache"), "*.eisc", SearchOption.AllDirectories).Length == 2, "both outputs are in the disk cache");

        using HttpResponseMessage health = await http.GetAsync("/health");
        Check(health.StatusCode == HttpStatusCode.OK && await health.Content.ReadAsStringAsync() == "Healthy", "the health check reports Healthy");
    }
    finally
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
catch (Exception unexpected)
{
    Console.Error.WriteLine("FAIL unexpected exception" + Environment.NewLine + unexpected);
    failures++;
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
        // Best effort: the directory lives under the temp folder.
    }
}

return failures == 0 ? 0 : 1;
