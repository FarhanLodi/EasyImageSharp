# OpenEXR fixtures

Every `.exr` file here was written by the OpenEXR reference library, never by EasyImageSharp, and its
ground truth is that same library's decode. `build_exr_fixtures.py` rebuilds the folder; it is not a
`gen_*.py` module because it needs the OpenEXR Python bindings and, for the official files, a download.

```bash
python -m pip install "OpenEXR>=3.3" "numpy>=2"
python build_exr_fixtures.py --official <checkout of openexr-images>   # rebuild everything
python build_exr_fixtures.py --check                                   # re-derive ground truth, write nothing
```

The folder was built with OpenEXR 3.5.3 (Python bindings) and NumPy 2.4.

## Provenance

| Files | Source | Licence |
| --- | --- | --- |
| `oei_*.exr` | verbatim copies from [openexr-images](https://github.com/AcademySoftwareFoundation/openexr-images) (`TestImages/`, `DisplayWindow/`, `MultiResolution/`); the SHA-256 of each is pinned in the script and recorded in `manifest.json` | BSD-3-Clause, see `LICENSE.openexr-images` |
| `damaged/*.bin` | the files of at most 4 KiB from openexr-images `Damaged/` (inputs that once crashed or misled the reference library), with `.bin` appended; SHA-256 in `damaged/manifest.json` | BSD-3-Clause, see `LICENSE.openexr-images` |
| `ref_*.exr` | written by `build_exr_fixtures.py` through the official `OpenEXR` Python bindings (BSD-3-Clause), which wrap the reference C++ library | generated for this repository |

| Official file | SHA-256 |
| --- | --- |
| `TestImages/stripes.exr` | `a4c11b79d256fb6945087dc9b244e814264a128aeb2fefacc303c32d08e426a1` |
| `TestImages/GrayRampsHorizontal.exr` | `7858e712af500e737ebaedbf40730646529e6093371ddb939becc58700b8fc6a` |
| `TestImages/GammaChart.exr` | `bb2819850542afae5e2208ba39724c23fe1a07b80b96f4fd093b3c9a3ca8ac2f` |
| `TestImages/AllHalfValues.exr` | `eede573a0b59b79f21de15ee9d3b7649d58d8f2a8e7787ea34f192db3b3c84a4` |
| `DisplayWindow/t01.exr` | `adc51278b0ceaa3475f58a9abc5cb5dec79c4590b9db773009e6ffcfdfd81643` |
| `DisplayWindow/t08.exr` | `fd92b685cdcd946aceb39bc033bf89abf53973b767a910907c5aabbe41abbb61` |
| `DisplayWindow/t09.exr` | `4b57630afbe015efd01998857f033dd26236be60a042827faba7afa2c853390c` |
| `MultiResolution/ColorCodedLevels.exr` | `ba836f7d37ea268ef9e96d7f0e28d035f4f197be12356133676282c578ada643` |

`oei_color_coded_levels.exr` (53 KB) and `oei_all_half_values.exr` (71 KB) exceed the usual 50 KB guideline:
the first is the only small official tiled, mipmapped file, the second the only one that holds every half
value including NaNs, infinities and denormals.

## Ground truth

`manifest.json` has one entry per fixture: `name`, `file`, `source`, `notes`, the SHA-256 and size of the
encoded file, and for decodable fixtures `width`, `height`, `data_window`, `display_window`, `compression`,
`tiled`, `line_order`, `chunks`, `raw_chunks` (chunks the writer stored uncompressed because compression would
not have helped) and `sha256_f32`. Entries with `expect` name the exception the decoder must throw.

The decoded pixels are mapped to RGBA exactly as `ExrDecoder` documents it: R, G, B and A of the default
layer, a missing colour channel is 0 and a missing alpha 1, a file with Y and no colour channels repeats Y in
R, G and B, and UINT samples become their numeric value. The result is float32, row-major, top-left origin,
covering the data window. `<name>.f32` holds that array as little-endian float32 for the small generated
fixtures; `sha256_f32` is the SHA-256 of the same array with every NaN replaced by `0x7FC00000`, which is how
the larger official images are checked, with nine `samples` per entry for diagnostics.
