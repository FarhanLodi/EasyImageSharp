# Public API baselines

`Microsoft.CodeAnalysis.PublicApiAnalyzers` tracks every public member of this assembly against the
files in this directory. The severities live in the repository `.editorconfig` (RS0016, RS0017,
RS0022, RS0024-RS0027, RS0036, RS0037 are all `warning`), and `Directory.Build.props` sets
`TreatWarningsAsErrors`, so an untracked public member fails the build.

## Layout

Both shipping projects multi-target `net8.0` and `net10.0`, and the two surfaces are allowed to
differ, so the baselines are per target framework:

```
PublicAPI/
  net8.0/{PublicAPI.Shipped.txt,PublicAPI.Unshipped.txt}
  net10.0/{PublicAPI.Shipped.txt,PublicAPI.Unshipped.txt}
```

The csproj wires them with `<AdditionalFiles Include="PublicAPI/$(TargetFramework)/..." />`. All four
files must exist even when they hold nothing but `#nullable enable` - a missing file is itself reported.

## File format

First line `#nullable enable`, then one fully-qualified declaration per line with nullability
annotations, sorted ordinally:

```
EasyImageSharp.AI.ModelHub.CacheDirectory.get -> string!
const EasyImageSharp.AI.ImageAiOptions.CacheEnvironmentVariable = "EASYIMAGESHARP_CACHE" -> string!
static EasyImageSharp.AI.ModelRegistry.Binarization.get -> EasyImageSharp.AI.ModelDescriptor!
```

A removal is a `*REMOVED*` line carrying the old signature. Removals are breaking changes.

## Adding public API

Put the new lines in `PublicAPI/<tfm>/PublicAPI.Unshipped.txt` for **both** target frameworks unless
the member genuinely exists on only one. In an IDE, the "Add to public API" code fix writes the entry
for you; from the command line, build once and each RS0016 diagnostic quotes the exact line to add:

```
warning RS0016: Symbol 'EasyImageSharp.AI.ModelHub.CacheDirectory.get -> string!' is not part of the declared public API
```

RS0016 fails the build if you forget, so this is enforced rather than merely requested.

## Releasing

At each release, promote: append `PublicAPI.Unshipped.txt` (minus its `#nullable enable` line) to
`PublicAPI.Shipped.txt`, re-sort Shipped, and truncate Unshipped back to `#nullable enable`. Do this
in the same commit as the version bump and the CHANGELOG section.

## Regenerating a baseline from scratch

`PublicAPI.Shipped.txt` must describe the surface that was actually *published*, not merely the
current source. To rebuild it, generate from the source tree at the release tag and then prove the
result with `Microsoft.DotNet.ApiCompat.Tool` against the assemblies inside the published package:

```bash
apicompat --strict-mode \
  --left  <unpacked-nupkg>/lib/<tfm>/EasyImageSharp.AI.dll \
  --right src/EasyImageSharp.AI/bin/Release/<tfm>/EasyImageSharp.AI.dll
```

Strict mode reports additions as well as removals, so a clean run in both directions means the
baseline is exactly that release's surface. (On tool version 10.x the flag is `--strict-mode`;
older documentation calls it `--enable-strict-mode`.)

The current `PublicAPI.Shipped.txt` files were generated this way from the 1.0.1 source tree and
verified clean against `EasyImageSharp.1.0.1.nupkg` and `EasyImageSharp.AI.1.0.1.nupkg` for both
target frameworks.
