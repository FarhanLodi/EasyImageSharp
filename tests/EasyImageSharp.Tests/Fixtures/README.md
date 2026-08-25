# Test fixtures

Sample files encoded by independent tools, used to verify decode paths that this library's own encoders
never produce (interlaced/16-bit/palette PNG, progressive/subsampled/CMYK JPEG, animated GIF, LZW/PackBits/
predictor TIFF, and so on).

- Regenerate everything with `python generate.py` from this directory (Python 3.11 + Pillow 11 + numpy).
- Keep each file small (well under 50 KB); the point is coverage, not size.
- One sub-folder per format: `png/`, `jpeg/`, `gif/`, `bmp/`, `tiff/`.
- Hand-crafted byte-level fixtures live next to the test that uses them; only tool-generated files belong here.

Test code locates the folder via `FixturePath.Get("png/basn0g16.png")` (see `Fixtures.cs`).
