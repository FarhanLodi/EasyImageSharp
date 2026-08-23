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
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]` for anything user-visible

### Public API

- [ ] This change adds, removes or alters no public API
- [ ] Additions are listed in `PublicAPI.Unshipped.txt` and carry XML documentation
- [ ] Removals are recorded as `*REMOVED*` entries (**breaking** — describe the migration below)

### Codecs and decoding

<!-- Only if this touches a decoder, encoder or DecoderOptions. -->

- [ ] Size limits are enforced right after the header is parsed and **before** any pixel allocation
- [ ] `DecoderOptions.MaxFrames` is honoured (multi-frame formats)
- [ ] Malformed input throws `InvalidImageContentException`; unimplemented features throw `NotSupportedException`
- [ ] Crafted malformed inputs were added to `CorruptInputTests`
- [ ] The new format folder is listed in `FuzzSmokeTests.CollectSeeds` (new codecs only)

### Fixtures

<!-- Only if this touches tests/EasyImageSharp.Tests/Fixtures. -->

- [ ] Fixtures were produced by their generator script, never by hand or by this library
- [ ] `python generate.py` was re-run and `git status --porcelain` is clean afterwards
- [ ] The generator change and the produced files are in the same commit

### Performance

<!-- Only if this touches a hot path. -->

- [ ] Ran the relevant BenchmarkDotNet filter; before/after numbers and the hardware are below
- [ ] No allocation was added inside a per-pixel loop
- [ ] Row-parallel work goes through `ParallelRowIterator` and output is identical at `MaxDegreeOfParallelism = 1`

## Breaking changes

<!-- Delete this section if there are none. Otherwise: what breaks, and what callers should do instead. -->

## Notes for reviewers

<!-- Anything non-obvious: a trade-off you made, a path you deliberately did not take, a follow-up you plan. -->
