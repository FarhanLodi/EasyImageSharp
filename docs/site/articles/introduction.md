# Guides

These pages are the repository's own documents, rendered in place rather than copied into the site's
source, so each one is also readable on GitHub at the same relative path. This page is a map of them.

## The core library

The [overview](../_repo/README.md) is the main reference for the core package. The sections most people
need first:

- [Install](../_repo/README.md#install) and [Getting started](../_repo/README.md#getting-started):
  loading, mutating, cloning and saving, and why every image must be disposed.
- [Recipes](../_repo/README.md#recipes): thumbnails with resource limits, validating an upload,
  encoder options, OCR preparation, document rectification, annotation, pages and frames, animated PNG
  and direct pixel access.
- [Format support](../_repo/README.md#format-support), [Processing](../_repo/README.md#processing) and
  [Metadata](../_repo/README.md#metadata): what each codec and operation covers.
- [Working with untrusted input](../_repo/README.md#working-with-untrusted-input): decoder limits, the
  closed exception contract and cancellation.
- [Deployment notes](../_repo/README.md#deployment-notes): target frameworks, thread safety, memory and
  versioning.

## Imaging guides

- [Advanced imaging capability matrix](../_repo/docs/ADVANCED_IMAGING.md) states the format, memory,
  precision, coordinate-mapping and native-dependency limits of the document and OCR features.
- [Reduced-size JPEG decoding](../_repo/docs/reduced-jpeg-decoding.md) and
  [TIFF region decoding](../_repo/docs/tiff-region-decoding.md) describe the two partial-decode paths.
- [Platform verification](../_repo/docs/platform-verification.md) records which platforms were exercised
  and how.

## Add-on packages

Each add-on is optional and released with the same version number as the core package:

- [EasyImageSharp.AI](../_repo/src/EasyImageSharp.AI/README.md): ONNX Runtime models fetched on demand
  with pinned checksums.
- [EasyImageSharp.SkiaSharp](../_repo/src/EasyImageSharp.SkiaSharp/README.md): native pixel interop,
  vector drawing and HarfBuzz text shaping.
- [EasyImageSharp.LittleCms](../_repo/src/EasyImageSharp.LittleCms/README.md): ICC transforms through
  the native Little CMS library.

## API reference

The [API reference](../api/index.md) is generated from the XML documentation comments of all four
packages, built for `net10.0`. The tracked public surface of each target framework is listed in the
[public API baselines](../_repo/src/EasyImageSharp/PublicAPI/README.md).
