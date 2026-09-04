<!--
  Thanks for contributing. Fill in what applies and delete what does not.
  See CONTRIBUTING.md for the extension points, the fixture rules and the API baseline workflow.
-->

## What this changes

<!-- One or two sentences on the effect, not the mechanics. -->

Fixes #

## Why

<!-- The problem this solves, or a link to the issue that describes it. -->

## How it was verified

<!--
  Every behavioural change needs expected values that did NOT come from this library:
  a reference implementation, a specification, Pillow/NumPy output, or a published algorithm.
  Say which, and where the test lives.
-->

## Checklist

- [ ] `dotnet build EasyImageSharp.slnx -c Release` is warning-free
- [ ] `dotnet test EasyImageSharp.slnx -c Release` is green on **net8.0** and **net10.0**
- [ ] New behaviour has a test, and its expected values came from outside this library
- [ ] A new C# block in `README.md` was transcribed verbatim into
      `tests/EasyImageSharp.Tests/ReadmeSamplesCompileTests.cs` (nothing detects this drift automatically)
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]` for anything user-visible

### Public API

- [ ] This change adds, removes or alters no public API
- [ ] Additions are listed in `src/<project>/PublicAPI/<tfm>/PublicAPI.Unshipped.txt` for **both**
      `net8.0` and `net10.0`, and carry XML documentation
- [ ] Removals are recorded as `*REMOVED*` entries (**breaking** — describe the migration below)

`RS0016` fails the build on a public member that is in neither baseline, so these boxes are a reminder
of what the analyzer will tell you anyway. See `src/EasyImageSharp/PublicAPI/README.md`.

### Codecs and decoding

<!-- Only if this touches a decoder, encoder or DecoderOptions. -->

- [ ] Size limits are enforced right after the header is parsed and **before** any pixel allocation
- [ ] `DecoderOptions.MaxFrames` is honoured (multi-frame formats)
- [ ] Malformed input throws `InvalidImageContentException`; unimplemented features throw `NotSupportedException`
- [ ] Crafted malformed inputs were added to `CorruptInputTests`
- [ ] The new format folder is listed in `FuzzSmokeTests.CollectSeeds` (new codecs only)
- [ ] The new public behaviour is exercised by `samples/AotSmoke`, and
      `dotnet publish samples/AotSmoke -c Release -p:PublishAot=true` still publishes and runs clean

### Fixtures

<!-- Only if this touches tests/EasyImageSharp.Tests/Fixtures. -->

- [ ] Fixtures were produced by their generator script, never by hand or by this library
- [ ] `python generate.py` was re-run and `python check_determinism.py` reports no differences
- [ ] The generator change and the produced files are in the same commit

A dirty `git status` after regenerating is recompression noise, not a determinism signal — Pillow's
zlib-ng output varies by version and by the SIMD path chosen from the host CPU. `check_determinism.py`
compares decoded pixels instead, which is why it replaced the old byte check. Stage only the fixtures
you meant to change.

### Performance

<!-- Only if this touches a hot path. -->

- [ ] Ran the relevant filter in `benchmarks/EasyImageSharp.Benchmarks`; before/after numbers, the
      hardware and the runtime version are below (a ratio from one machine, not an absolute time)
- [ ] Measured on **both** target frameworks if the runtime's own implementation is in the path
      (`ZLibStream` is native zlib on `net8.0` and zlib-ng on `net10.0`)
- [ ] No allocation was added inside a per-pixel loop
- [ ] Row-parallel work goes through `ParallelRowIterator` and output is identical at `MaxDegreeOfParallelism = 1`

## Breaking changes

<!-- Delete this section if there are none. Otherwise: what breaks, and what callers should do instead. -->

## Notes for reviewers

<!-- Anything non-obvious: a trade-off you made, a path you deliberately did not take, a follow-up you plan. -->
