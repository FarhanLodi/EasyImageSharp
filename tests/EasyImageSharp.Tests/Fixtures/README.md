# Test fixtures

Sample files encoded by independent tools, used to verify decode paths that this library's own encoders
never produce (interlaced/16-bit/palette PNG, APNG frame compositing, progressive/subsampled/CMYK JPEG,
animated GIF, LZW/PackBits/predictor and BigTIFF TIFF, and so on).

The governing rule is that **ground truth never comes from EasyImageSharp**. Every file here is written by
Pillow, libjpeg, libwebp, or assembled byte by byte in Python, and the pixels a decoder must produce are
recorded alongside it. A test that compared this library's decoder against this library's encoder would
prove nothing.

- Regenerate everything with `python generate.py` from this directory.
- Requires Python 3.11+, Pillow 11, NumPy 2 and tifffile:
  `python -m pip install "pillow==11.3.0" "numpy>=2,<3" "tifffile>=2024.1"`.
  tifffile is required, not optional. Pillow detects BigTIFF by testing `ifh[2] == 43`, an index that only
  lands on the version word in the little-endian layout, so it cannot open a big-endian BigTIFF at all;
  tifffile is the only independent reader for `bigtiff_be_rgb` and `bigtiff_mm_tiled`, and `gen_tiffadv.py`
  refuses to emit a fixture that no independent reader has confirmed. It is not pinned hard because it only
  verifies fixtures and never produces committed bytes. `imagecodecs` is not needed: the one fixture whose
  codec would require it (`bigtiff_lzw`) is little-endian, so Pillow cross-checks that one.
- Keep each file small (well under 50 KB); the point is coverage, not size.
- Hand-crafted byte-level fixtures that exist only for one test live next to that test; only files a
  generator writes belong here.

`generate.py` is the single entry point. Its `main()` runs every `gen_<format>` function defined in
`generate.py` itself, then imports each sibling `gen_<format>.py` and runs the one function named after
the module, passing it `<this directory>/<format>` as the output directory. Adding a format means adding a
`gen_<format>.py` with a matching function name; nothing else needs editing.

## Layout

| Folder | What it covers |
| --- | --- |
| `apng/` | Animated PNG: frame rectangles, dispose/blend combinations, delays, loop counts, and malformed `acTL`/`fcTL`/`fdAT` streams |
| `bmp/` | BMP: every header version, bit depth, bitfield layout and RLE variant |
| `document/` | Document imaging: synthetic text pages for skew, orientation, line and hole-punch removal, despeckling, illumination correction, layout segmentation |
| `drawing/` | Reference renderings for the annotation drawing tests, drawn with Pillow's `ImageDraw` |
| `effects/` | Colour, convolution, compositing and histogram reference images, derived from fixed formulas |
| `geometry/` | Resize kernels, affine and perspective warps |
| `gif/` | GIF: static, interlaced, local palettes, transparency, animation and disposal |
| `jpeg/` | Baseline, progressive, subsampled, restart-interval, CMYK and YCCK JPEG, plus libjpeg's own decode of each |
| `metadata/` | EXIF, orientation, DPI, ICC, XMP, PNG text chunks and GIF frame facts, across several container formats |
| `png/` | PNG: every colour type and bit depth, Adam7, `tRNS`, ancillary chunks, filter cycles, odd geometries |
| `smallformats/tga/` | TGA: raw and RLE, 8/15/16/24/32-bit, palettes, origin flags, extension areas |
| `smallformats/pbm/` | Netpbm: `P1`–`P7` (PBM, PGM, PPM, PAM), ASCII and binary, comments, out-of-range values |
| `smallformats/qoi/` | QOI: each opcode path, RGB and RGBA, plus malformed headers |
| `smallformats/ico/` | ICO and CUR: DIB and PNG entries, multiple sizes, hotspots, corrupt directories |
| `tiff/` | TIFF: strip layouts, compressions, predictors, photometrics, byte orders, multi-page, BigTIFF |
| `tiffadv/` | The harder TIFF features: CCITT bilevel, JPEG-in-TIFF, planar and tiled layouts, wide sample formats, remaining photometric interpretations |
| `vp8enc/` | Source images for the VP8 lossy *encoder* tests |
| `webp/` | WebP: lossy, lossless, alpha, animation, and truncated or malformed containers |
| `webpenc/` | Source images for the WebP *encoder* tests |

## Per-fixture files

Most folders follow the same layout:

| File | Meaning |
| --- | --- |
| `<name>.<ext>` | The fixture itself: the bytes handed to the decoder under test |
| `<name>.rgba` | Ground truth pixels (see the contract below) |
| `<name>.expected.png` | An 8-bit RGBA rendering of the first frame, written by Pillow, purely for eyeballing in review |
| `manifest.json` | The machine-readable index the C# tests enumerate |
| `EXPECTED.md` | The same contract in prose, where a folder needs explaining |

The `.rgba` contract is exact: **raw RGBA8, top-left origin, no header, no padding, stride is `width * 4`
bytes, and a multi-frame fixture concatenates every frame in order**, so the file is `width * height * 4 *
frames` bytes long. Frames are stored fully composited, as a viewer would show them, not as the on-the-wire
frame rectangles.

`manifest.json` is a JSON array of entries in most folders (`name`, `file`, `width`, `height`, `frames`,
`notes`, plus per-format header facts, and `expect` naming the exception type a decoder must throw for a
fixture that exercises something the library deliberately does not implement). A few folders
(`document/`, `drawing/`, `effects/`, `metadata/`, `webpenc/`) use an object instead, shaped for the one
test class that reads it.

Two folders are deliberately different. `gif/` has no manifest and no `.rgba`: its ground truth is prose in
`gif/EXPECTED.md`, transcribed by hand into `GifTests.cs` as `[InlineData]`. `jpeg/` has no manifest and no
`.rgba` either: the ground truth for each `<name>.jpg` is the sibling `<name>.decoded.png`, which is
libjpeg's own decode, and the tests compare against it by PSNR rather than exactly, because a compliant IDCT
is allowed to differ by a hair.

Test code locates a file through `FixturePath.Get("png/rgb8_97x61.png")` (see `Fixtures.cs`); the test
project copies `Fixtures/**/*` into the output directory, so the path is always relative to this folder.

## Checking determinism

```
python check_determinism.py
```

`check_determinism.py` copies `generate.py` and every `gen_*.py` into a scratch directory, regenerates the
whole corpus there from nothing, and compares the result against the corpus in this directory. It exits 0
when they agree, 1 when they differ, and 2 when the check could not run at all (Pillow missing, a generator
crashed, an unusable `--scratch` directory). It is not named `gen_*.py`, so `generate.py` never picks it up,
and it never writes into this directory.

**A dirty `git status` after `python generate.py` is not a determinism failure.** Pillow's wheels deflate
through zlib-ng, whose output depends on the zlib-ng version *and* on the SIMD path it selects at run time
from the host CPU. Regenerating on a different machine therefore rewrites hundreds of PNG byte streams that
carry byte-for-byte identical pixels. An earlier CI job compared file bytes and had to be deleted for
exactly this reason: it was flagging roughly 330 files that nothing was wrong with. Run the checker instead
of reading the diff.

So the checker compares the strongest thing that is actually stable for each kind of file:

| Rule | Applies to | What is compared |
| --- | --- | --- |
| decoded pixels | every image Pillow can open | mode, size, palette, frame count, every frame's pixel array exactly, and the frame info Pillow exposes that pixels cannot carry: delay, dispose, blend, frame rectangle, loop count, transparency, interlacing, gamma, DPI |
| exact bytes | `.rgba` dumps, `.bin` and `.xml` blobs, and any file **both** sides refuse to decode | the whole file |
| structural json | `manifest.json` | the parsed JSON, with `sha256`, `size` and `libwebp` deleted at every depth |
| masked text | `EXPECTED.md` and other prose | the text, with hex digests and byte counts masked |

Every frame is compared, not just the first: most of the `apng/` corpus differs from its neighbours only
after frame 0, so a first-frame-only check would be vacuous there. The volatile manifest keys are the ones
that hash or measure the *encoded* bytes, which is precisely what zlib-ng and libwebp make irreproducible;
everything else in a manifest is content and must match exactly. Widening that set weakens the check, so
treat adding a key to it as a change that needs justifying.

If a file both sides can normally decode suddenly fails to decode on one side only, that is reported as a
difference rather than quietly falling back to bytes. And if the check fails on a file nobody touched,
suspect the toolchain first: a Pillow major version that changes a *decode* (rather than an encode) produces
a real but confusing failure here.

Options:

| Flag | Effect |
| --- | --- |
| `--scratch DIR` | Regenerate into `DIR` and keep it afterwards, for inspecting the regenerated corpus by hand. `DIR` must be empty or not exist. The default is a temporary directory that is deleted on exit. |
| `--source worktree` (default) | Check every file on disk, including fixtures added in the current branch but not yet committed |
| `--source git` | Check only the files git tracks |
| `--json REPORT` | Write the full result to `REPORT` as JSON, for CI artifact upload |
| `--verbose` | Print `generate.py`'s own output and the rule applied to every file |

A file that only one side has is reported as `MISSING` (a generator writes it, the corpus does not have it,
so run `python generate.py`) or `EXTRA` (the corpus has it, no generator writes it, so it is stale and
should be deleted). Files git already ignores are skipped, so a local experiment does not read as a stale
fixture.
