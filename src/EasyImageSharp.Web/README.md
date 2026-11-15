<div align="center">

<img src="https://raw.githubusercontent.com/FarhanLodi/EasyImageSharp/main/src/EasyImageSharp/Assets/icon.png" alt="EasyImageSharp" width="128" height="128" />

# EasyImageSharp.Web

[![NuGet](https://img.shields.io/nuget/v/EasyImageSharp.Web.svg?label=NuGet)](https://www.nuget.org/packages/EasyImageSharp.Web)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE)

</div>

ASP.NET Core middleware that resizes, crops, pads, re-orients and transcodes images on the fly with
[EasyImageSharp](https://www.nuget.org/packages/EasyImageSharp): `/photos/cat.jpg?width=300&format=webp`.
Signed URLs, WebP negotiation, HTTP validators with 304 handling, a disk or distributed output cache,
output limits and a concurrency limiter are built in. Fully managed; the only dependency besides
EasyImageSharp is the ASP.NET Core shared framework.

```bash
dotnet add package EasyImageSharp.Web
```

## Quick start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddEasyImageSharpWeb(builder.Configuration.GetSection("EasyImageSharp:Web"))
    .UsePhysicalCache("image-cache");                 // optional; relative to the content root
builder.Services.AddHealthChecks().AddEasyImageSharp();

var app = builder.Build();
app.UseEasyImageSharp();                              // before UseStaticFiles
app.UseStaticFiles();
app.MapHealthChecks("/health");
app.Run();
```

A request is handled when it is a `GET` or `HEAD`, its path ends in the extension of a decodable
format, and its query has at least one command. Everything else — the originals, other files, your
endpoints — passes through untouched. Originals are read from `wwwroot` unless `SourceRootPath` or a
custom source provider says otherwise.

## Commands

| Command | Values | Meaning |
|---|---|---|
| `width`, `height` | 1 – `MaxWidth` / `MaxHeight` | Target size. With one side, the other follows the aspect ratio. |
| `mode` | `max` (default), `crop`, `pad`, `boxpad`, `min`, `stretch` | Fit inside the box / cover and crop / fit and pad / pad without scaling up / cover without cropping / exact size. |
| `anchor` | `center` (default), `top`, `bottom`, `left`, `right`, `topleft`, `topright`, `bottomleft`, `bottomright` | The part kept by `crop`; where the image sits for `pad`. |
| `format` | `jpeg`/`jpg`, `png`, `webp`, `gif`, `bmp`, `tiff`/`tif`, added formats, `auto` | Output format, subject to `AllowedFormats`. `auto` (or no `format`) negotiates. |
| `quality` | 1 – 100 | JPEG and WebP quality. |
| `bgcolor` | CSS name or hex `RGB`/`RGBA`/`RRGGBB`/`RRGGBBAA` (`#` as `%23` or omitted) | Composites transparent areas over the colour; also the padding colour. |
| `autoorient` | `true`/`false`, `1`/`0` | Rotate upright from EXIF (default `AutoOrient`, on). |
| `sampler` | `bicubic` (default), `lanczos3`, `bilinear`, `nearest`, `box`, `catmullrom`, `mitchell` | Resampling filter. |
| `rotate` | `0`, `90`, `180`, `270` | Clockwise, lossless. |
| `flip` | `h`, `v`, `hv` | Mirror horizontally, vertically or both. |

Commands are case-insensitive and run in a fixed order whatever their order in the URL: auto-orient,
rotate, flip, resize, background, encode. Each may appear once (a repeat is a 400). Unknown
parameters such as cache busters are ignored and never reach the cache key. Images are not enlarged
unless `AllowUpscaling` is on: a box larger than the source is shrunk to fit (keeping its aspect), and
`pad` pads around the unscaled image. Transparent areas are flattened onto `DefaultBackgroundColor`
(white) when the output has no alpha. EXIF and XMP are stripped (`StripMetadata`); the ICC profile is kept.

## Status codes

| Code | When |
|---|---|
| 200 / 304 | Processed (or from the cache) / `If-None-Match` or `If-Modified-Since` matched |
| 400 | A command is malformed, out of syntax range or repeated (short `text/plain` reason) |
| 403 | Signing is enabled and the signature is missing, invalid or expired |
| 404 | No source at that path, or the path is unsafe (dot segments, backslashes, `:` and similar) |
| 413 | The source exceeds `MaxSourceBytes`, `MaxSourcePixels` or a core decoder limit |
| 415 | The source is not an image in a supported format |
| 422 | The output exceeds `MaxWidth`, `MaxHeight` or `MaxOutputPixels`; the size is not in `AllowedSizes`; the format is not allowed; or the source is corrupt |
| 503 | Every processing slot is busy and the queue is full (`Retry-After: 1`) |

Error responses carry `Cache-Control: no-store`. When the client disconnects, decoding, processing and
waiting for a slot are cancelled.

## Signed URLs

Set one or more base64 keys of at least 32 bytes, and every request with a command must be signed:

```json
{ "EasyImageSharp": { "Web": { "SigningKeys": [ "NEW-KEY-BASE64", "OLD-KEY-BASE64" ] } } }
```

```csharp
// ImageUrlSigner is registered by AddEasyImageSharpWeb and signs with the first key.
string url = signer.CreateSignedUrl(
    "/photos/cat.jpg",
    new Dictionary<string, string> { ["width"] = "300", ["format"] = "webp" },
    expiresAt: DateTimeOffset.UtcNow.AddDays(1));
// /photos/cat.jpg?width=300&format=webp&expires=1767225600&sig=...
```

The `sig` parameter is the unpadded base64url HMAC-SHA256 of a canonical string: the path with each
segment percent-encoded, `?`, then every other query parameter as `name=value` joined with `&` —
names lowercased, pairs sorted ordinally by name then value, names and values percent-encoded per
RFC 3986 (everything outside `A-Z a-z 0-9 - . _ ~`). `/photos/cat.jpg?width=300&format=webp` signs
`/photos/cat.jpg?format=webp&width=300`. In Python:

```python
from urllib.parse import quote
import base64, hashlib, hmac

def sign(key: bytes, path: str, params: list[tuple[str, str]]) -> str:
    q = lambda s: quote(s, safe="-._~")
    canonical = "/".join(q(s) for s in path.split("/")) + "?" + "&".join(
        q(k) + "=" + q(v) for k, v in sorted((k.lower(), v) for k, v in params if k.lower() != "sig"))
    tag = hmac.new(key, canonical.encode(), hashlib.sha256).digest()
    return base64.urlsafe_b64encode(tag).rstrip(b"=").decode()
```

Verification is constant-time and tries every key, so rotation is: add the new key first, keep the old
one until the URLs signed with it have expired, then remove it (options reload without a restart).
Every parameter is signed, so a signed URL cannot be extended. `expires` (Unix seconds, signed) is
optional; `Cache-Control` never outlives it. Requests without commands are not checked — keep
originals that must not be public outside `wwwroot` and point `SourceRootPath` at them.

## Format negotiation

Without `format`, a client whose `Accept` header names `image/webp` explicitly gets WebP
(`image/*` and `*/*` do not count — browsers send them while supporting only a few formats), and
every such response carries `Vary: Accept`. Animations keep their format; a WebP original goes to a
client that did not ask for WebP as JPEG, or PNG when it has transparency.

To serve AVIF or JPEG XL, implement `IImageOutputFormat` over an encoder from an add-on package and
list it first:

```csharp
builder.Services.AddEasyImageSharpWeb(o =>
{
    o.AllowedFormats.Add("avif"); o.AllowedFormats.Add("webp"); o.AllowedFormats.Add("jpeg"); o.AllowedFormats.Add("png");
    o.NegotiatedFormats.Add("avif"); o.NegotiatedFormats.Add("webp");
}).AddOutputFormat(new MyAvifOutputFormat());
```

## Caching

**HTTP.** Every 200 and 304 carries a strong `ETag` (a hash of the source version, the normalised
commands, the negotiated selection, the output-affecting options and the library version),
`Last-Modified` from the source and `Cache-Control: public, max-age=…` (`BrowserCacheMaxAge`, 7 days).
`If-None-Match` takes precedence over `If-Modified-Since`, and a 304 is answered from the source's
metadata alone — no decoding, no cache read and no processing slot.

**Server.** `UsePhysicalCache(path)` stores each output as one file, written to a temporary file and
renamed into place, so readers never see a partial entry; `UseDistributedCache()` stores it in your
`IDistributedCache` (Redis, SQL Server, memory). Keys are SHA-256 hashes of everything that affects
the bytes and nothing else, so two outputs never share an entry, unknown parameters cannot multiply
entries, and a changed source is never served stale. Entries expire after `CacheMaxAge` (365 days);
the disk cache has no size-based eviction, so bound the directory externally if needed, and keep it
outside `wwwroot`. A failing cache is logged and bypassed. `UseCache<T>()` plugs in your own
`IImageCache`.

## Sources

The default provider reads `SourceRootPath` (or `wwwroot`) through `PhysicalFileProvider`, which
refuses paths outside its root and hidden or dot files; the middleware rejects dot segments,
backslashes, drive and stream colons, control characters, double-encoded percent signs and trailing
dots or spaces before any provider runs. Implement `IImageSourceProvider` for blob storage or a
database, and register it with `AddSourceProvider`; providers are tried in order.

## Configuration

| Setting | Default | |
|---|---|---|
| `RequestPathPrefix` | `/` | Only paths under it are handled; it is removed before the source lookup. |
| `SourceRootPath` | web root | Directory of originals for the default provider. |
| `SigningKeys` | none | Base64 HMAC keys (≥ 32 bytes). Non-empty enables signing. |
| `AllowedFormats` | `jpeg`, `png`, `webp`, `gif` | Output formats requests may produce. |
| `EnableFormatNegotiation` / `NegotiatedFormats` | on / `webp` | Accept-header negotiation, in preference order. |
| `AllowedSizes` | any | Allowlist such as `320x240`, `640x`, `x480`. |
| `MaxWidth` / `MaxHeight` / `MaxOutputPixels` | 4096 / 4096 / 16 Mpx | Output limits (422). |
| `MaxSourceBytes` / `MaxSourcePixels` | 32 MiB / 40 Mpx | Source limits (413), on top of `EasyImageSharpOptions`. |
| `AllowUpscaling`, `AutoOrient`, `StripMetadata` | off, on, on | |
| `DefaultQuality`, `DefaultBackgroundColor` | encoder default, `white` | |
| `BrowserCacheMaxAge` / `CacheMaxAge` | 7 days / 365 days | |
| `MaxConcurrentProcessing` / `MaxQueueLength` | CPU count / 64 | Processing slots and waiting requests; read at start-up. |

Settings bind from `EasyImageSharp:Web` through the configuration binding source generator (trim and
Native AOT safe), are validated on start, and reload live except the two limiter sizes. Frame counts,
total pixels, the pixel-buffer budget and parallelism come from the `EasyImageSharp` section of
[EasyImageSharp.Extensions.DependencyInjection](https://www.nuget.org/packages/EasyImageSharp.Extensions.DependencyInjection),
which this package registers.

## Health check

`AddHealthChecks().AddEasyImageSharp()` runs an in-memory decode-resize-encode round trip, probes the
disk cache directory when one is used, and reports `Degraded` while the limiter is saturated. Its data
lists `permitLimit`, `queueLimit`, `availablePermits` and `queued`.

## Logging

Events are generated with the `LoggerMessage` source generator under the `EasyImageSharp.Web`
category. Paths are logged; query strings are not, because a signed URL's query carries its signature.

## Limits of this release

- Concurrent requests for the same uncached output are each processed (no request coalescing).
- Sources are read fully into memory (bounded by `MaxSourceBytes`) before decoding.
- `Range` requests are answered with the full image.

## Dependency injection without the middleware

This package also contains the container integration that was planned as a separate `EasyImageSharp.Extensions.DependencyInjection` package (namespace `EasyImageSharp.Extensions.DependencyInjection`, unchanged). It is useful on its own in worker services and other hosts that load and process images through `DecoderOptions` from configuration.

`Microsoft.Extensions.DependencyInjection` integration for [EasyImageSharp](https://www.nuget.org/packages/EasyImageSharp):
configure decoder limits, the pixel-buffer budget and parallelism once — in code or in `appsettings.json` —
validate them when the host starts, and resolve ready-made `DecoderOptions` and `ImageExecutionOptions`
wherever images are decoded.

### Quick start

```csharp
builder.Services.AddEasyImageSharp(builder.Configuration.GetSection("EasyImageSharp"));
```

```json
{
  "EasyImageSharp": {
    "MaxPixels": 40000000,
    "MaxTotalPixels": 160000000,
    "MaxFrames": 100,
    "MaxEncodedBytes": 33554432,
    "MaxPixelBufferBytes": 1073741824,
    "MaxDegreeOfParallelism": 1
  }
}
```

Then take `IImageOptionsFactory` and pass the request's cancellation token:

```csharp
app.MapPost("/thumbnail", async (HttpRequest request, IImageOptionsFactory images, CancellationToken ct) =>
{
    DecoderOptions decoder = images.CreateDecoderOptions(ct);
    using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(request.Body, decoder, ct);
    image.Mutate(images.CreateExecutionOptions(ct), x => x.Resize(new ResizeOptions
    {
        Size = new Size(320, 240),
        Mode = ResizeMode.Max,
    }));

    var output = new MemoryStream();
    await image.SaveAsWebpAsync(output, ct);
    return Results.File(output.ToArray(), "image/webp");
});
```

The configuration overload binds through the configuration binding **source generator**, so it is
trim and Native AOT safe, and it follows configuration reloads: every `Create…` call reads the
current values.

### Settings

| Key | Default | Applies to |
|---|---|---|
| `MaxPixels` | 268 435 456 | `DecoderOptions.MaxPixels` — pixels in one frame, checked before allocation |
| `MaxTotalPixels` | 1 073 741 824 | `DecoderOptions.MaxTotalPixels` — pixels across all frames of one decode |
| `MaxFrames` | unlimited | `DecoderOptions.MaxFrames` — frames or pages read |
| `MaxEncodedBytes` | 2 147 483 647 | `DecoderOptions.MaxEncodedBytes` — encoded input, including non-seekable streams |
| `MaxPixelBufferBytes` | unlimited | `ImageExecutionOptions.MaxPixelBufferBytes` — cumulative pixel-buffer budget of one decode or one `Mutate` / `Clone` call |
| `MaxDegreeOfParallelism` | process default | threads one operation may use; `1` is often right in a web server |
| `MinimumPixelsPerTask` | process default | the smallest parallel work item |

The defaults are the library's own, so registering the services without configuring anything changes
no behaviour. A web application decoding untrusted uploads should lower `MaxPixels`,
`MaxTotalPixels` and `MaxEncodedBytes`.

Every value is validated with `ValidateOnStart`: a zero or negative limit, or a parallelism setting
below 1, fails host start-up with an `OptionsValidationException` naming the setting instead of
surfacing on the first request. A value that is not a number fails binding the same way.

### What gets registered

| Service | Lifetime | Notes |
|---|---|---|
| `IOptions<EasyImageSharpOptions>` | singleton | validated on start |
| `IImageOptionsFactory` | singleton | `CreateDecoderOptions(ct)` and `CreateExecutionOptions(ct)` return fresh instances |
| `DecoderOptions` | transient | from the factory, without a cancellation token |
| `ImageExecutionOptions` | transient | from the factory, without a cancellation token |

Prefer `IImageOptionsFactory` in request code so cancellation reaches the decoder. Code configuration
(`AddEasyImageSharp(o => o.MaxFrames = 1)`) runs after any binding registered before it, and calling
`AddEasyImageSharp` more than once adds nothing twice.

Opt-in format providers (for example from an add-on package) are added to every created
`DecoderOptions.AdditionalFormats` with `services.AddEasyImageSharpFormat(format)`.

## Requirements

Targets **net10.0**, with a framework reference to `Microsoft.AspNetCore.App`, which also supplies the `Microsoft.Extensions.*` packages the dependency-injection integration uses.

## License

[MIT](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE) — Copyright © 2026 Farhan Lodi.
