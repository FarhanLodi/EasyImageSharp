# Support

EasyImageSharp is an MIT-licensed project maintained in people's spare time. This page says where to ask
for help, which releases receive fixes and for how long, how the supported .NET versions are chosen,
and how an API is retired. Security reports follow [SECURITY.md](SECURITY.md), not this page.

> **Status: draft.** The support windows below have not been confirmed by the maintainer yet. Until they
> are, treat them as the intended policy rather than a commitment.

---

## Getting help

| You have | Go to |
|---|---|
| A question about how to do something | [GitHub Discussions](https://github.com/FarhanLodi/EasyImageSharp/discussions) |
| A bug: a crash, wrong pixels, an exception outside the documented contract | [Bug report](https://github.com/FarhanLodi/EasyImageSharp/issues/new/choose) |
| A file the library should decode but does not | The *format support* issue template |
| A feature proposal | The *feature request* issue template |
| A security problem | [Private vulnerability reporting](https://github.com/FarhanLodi/EasyImageSharp/security/advisories/new). Never a public issue. |

A useful report names the package and version, the target framework, the operating system and CPU
architecture, and includes the input file or the script that generates it. For an image that decodes
wrongly, the file itself is usually the whole report.

There is no paid support and no response-time guarantee outside the security targets in
[SECURITY.md](SECURITY.md#what-to-expect).

---

## Versioning

All packages follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) and are released together
with the same version number: `EasyImageSharp`, `EasyImageSharp.AI`, `EasyImageSharp.SkiaSharp` and
`EasyImageSharp.LittleCms`. Use the same version of every EasyImageSharp package in one application.

- **Patch** releases (`1.2.x`) contain bug and security fixes only. No API is added or removed.
- **Minor** releases (`1.x.0`) add API and may change behaviour that was documented as a defect.
  Intentional output changes are called out in [CHANGELOG.md](CHANGELOG.md).
- **Major** releases (`x.0.0`) are the only releases that remove or change public API.

Every pack of `EasyImageSharp` and `EasyImageSharp.AI` is compared against a published baseline release
with .NET package validation, so a binary-breaking change cannot ship in a minor or patch release by
accident. The two newer add-ons join that check once they have a second published version.

---

## Supported releases

Only the newest minor release line is actively developed. When a new minor release ships, the previous
line receives **security fixes for six months** from that date. Bug fixes that are not security
fixes go into the newest line only; upgrading within a major version is meant to be safe.

| Release line | Status | Fixes | Security fixes until |
|---|---|---|---|
| 1.2.x | Current | Bugs and security | Six months after 1.3.0 ships |
| 1.1.x | Security only | Security | 2027-04-14 (six months after 1.2.0, released 2026-10-14) |
| 1.0.x | Security only | Security | 2027-03-03 (six months after 1.1.0, released 2026-09-03) |
| 0.x previews | Unsupported | None | Ended |

When a new **major** version ships, the last minor line of the previous major receives security fixes
for **twelve months**, so applications have a full year to move across a breaking release.

A security fix for an older line is released as a new patch of that line. Fixes are not backported to
lines whose window has ended.

---

## .NET versions

The packages target the long-term-support (LTS) releases of .NET that Microsoft supports, currently
`net8.0` and `net10.0`. Standard-term releases are not targeted separately: an application on one uses
the nearest lower target, so a .NET 9 application gets the `net8.0` assets.

- **New LTS releases** are added as a target in the first minor release after their general availability.
- **.NET 8 reaches the end of Microsoft support on 2026-11-10.** The `net8.0` target stays in every 1.x
  release so that no minor release removes a platform from existing users. After that date, problems that
  reproduce only on .NET 8 are fixed on a best-effort basis, and CI keeps testing `net8.0` for as long as
  GitHub's hosted runners can install it. The target is removed in 2.0.
- **.NET 10 is supported by Microsoft until 2028-11-14.**
- A target framework is never removed in a patch release, and its removal is announced in
  [CHANGELOG.md](CHANGELOG.md) at least one release in advance.

The support dates come from Microsoft's
[.NET release policy](https://dotnet.microsoft.com/platform/support/policy/dotnet-core).

---

## Third-party dependencies of the add-on packages

The core package has no package dependencies. The add-ons depend on ONNX Runtime, SkiaSharp and
HarfBuzz, and Little CMS. Their versions are raised in minor releases, or in a patch release when an
upstream security fix affects how EasyImageSharp uses them. Models fetched by `EasyImageSharp.AI` are
pinned by SHA-256 checksum, and changing a pinned model is treated as a behaviour change and listed in
the changelog.

---

## Deprecation

1. A public API that is going away is first marked `[Obsolete]`, with a message that names its
   replacement, in a minor release. It keeps working.
2. It is removed no earlier than the next major release, and the removal is listed in
   [CHANGELOG.md](CHANGELOG.md) under that release.
3. A whole package that is discontinued is marked deprecated on NuGet with the recommended alternative,
   and its last release line keeps the security window described above.

Behaviour that was documented as a defect can be corrected in a minor release; the changelog says so
explicitly when the correction changes existing output.
