<div align="center">

<img src="https://raw.githubusercontent.com/FarhanLodi/EasyImageSharp/main/src/EasyImageSharp/Assets/icon.png" alt="EasyImageSharp" width="128" height="128" />

# EasyImageSharp.Extensions.DependencyInjection

[![NuGet](https://img.shields.io/nuget/v/EasyImageSharp.Extensions.DependencyInjection.svg?label=NuGet)](https://www.nuget.org/packages/EasyImageSharp.Extensions.DependencyInjection)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE)

</div>

`Microsoft.Extensions.DependencyInjection` integration for [EasyImageSharp](https://www.nuget.org/packages/EasyImageSharp):
configure decoder limits, the pixel-buffer budget and parallelism once — in code or in `appsettings.json` —
validate them when the host starts, and resolve ready-made `DecoderOptions` and `ImageExecutionOptions`
wherever images are decoded.

```bash
dotnet add package EasyImageSharp.Extensions.DependencyInjection
```

## Quick start

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

## Settings

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

## What gets registered

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

Targets **net8.0** and **net10.0**. Depends only on `Microsoft.Extensions.DependencyInjection.Abstractions`,
`Microsoft.Extensions.Options`, `Microsoft.Extensions.Options.ConfigurationExtensions` and
`Microsoft.Extensions.Configuration.Binder` (all MIT), on the release line of each target framework.

## License

[MIT](https://github.com/FarhanLodi/EasyImageSharp/blob/main/LICENSE) — Copyright © 2026 Farhan Lodi.
