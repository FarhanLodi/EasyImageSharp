#!/usr/bin/env python
"""Builds the OpenEXR decoder fixtures and their ground truth.

Every encoded file here is written by the OpenEXR reference library, never by EasyImageSharp:

* ``oei_*.exr`` are verbatim copies of files from the official openexr-images repository
  (https://github.com/AcademySoftwareFoundation/openexr-images, BSD-3-Clause, see LICENSE.openexr-images).
  Their SHA-256 digests are pinned below; the script refuses to copy a file whose digest differs.
* ``ref_*.exr`` are small images written here through the official OpenEXR Python bindings
  (``pip install OpenEXR``, BSD-3-Clause), which wrap the reference C++ library.

Ground truth comes from the same reference library reading the file back. For every fixture the decoded
pixels are mapped to RGBA exactly as the C# decoder documents it (R, G, B, A of the default layer; a
missing colour channel is 0, a missing alpha is 1; a file with Y and no colour channels repeats Y in R, G
and B; UINT samples become their numeric value), as float32, row-major, top-left origin, covering the
data window. Small generated fixtures store that array as ``<name>.f32`` (little-endian float32 RGBA);
every fixture also records ``sha256_f32``, the SHA-256 of the same array with every NaN canonicalised to
0x7FC00000, so the larger official images can be checked without committing megabytes of floats.

Usage (from any directory; needs Python 3.11+, numpy 2 and OpenEXR >= 3.3):

    python build_exr_fixtures.py --official <checkout-of-openexr-images>
    python build_exr_fixtures.py --check      # re-derive the ground truth and compare, writing nothing

The script is deliberately not named gen_*.py: generate.py runs offline with Pillow and NumPy only, and
these fixtures need the OpenEXR bindings and, for the official files, a download.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys

import numpy as np
import OpenEXR

HERE = os.path.dirname(os.path.abspath(__file__))

# Official sample files: (fixture name, path inside openexr-images, SHA-256, what it covers).
OFFICIAL = [
    ("oei_stripes", "TestImages/stripes.exr", "a4c11b79d256fb6945087dc9b244e814264a128aeb2fefacc303c32d08e426a1",
     "PIZ, RGBA half, 100x50"),
    ("oei_gray_ramps_horizontal", "TestImages/GrayRampsHorizontal.exr",
     "7858e712af500e737ebaedbf40730646529e6093371ddb939becc58700b8fc6a", "PXR24, luminance-only (Y) half, 800x800"),
    ("oei_gamma_chart", "TestImages/GammaChart.exr", "bb2819850542afae5e2208ba39724c23fe1a07b80b96f4fd093b3c9a3ca8ac2f",
     "PXR24, RGB half, 800x800"),
    ("oei_display_window_t01", "DisplayWindow/t01.exr", "adc51278b0ceaa3475f58a9abc5cb5dec79c4590b9db773009e6ffcfdfd81643",
     "PIZ, data window equals display window"),
    ("oei_display_window_t08", "DisplayWindow/t08.exr", "fd92b685cdcd946aceb39bc033bf89abf53973b767a910907c5aabbe41abbb61",
     "PIZ, data window (30,40)-(429,339) inside display window (0,0)-(500,400)"),
    ("oei_display_window_t09", "DisplayWindow/t09.exr", "4b57630afbe015efd01998857f033dd26236be60a042827faba7afa2c853390c",
     "PIZ, data window and display window disjoint"),
    ("oei_color_coded_levels", "MultiResolution/ColorCodedLevels.exr",
     "ba836f7d37ea268ef9e96d7f0e28d035f4f197be12356133676282c578ada643",
     "tiled 64x64, MIPMAP_LEVELS, PXR24, RGBA half; the decoder reads the full-resolution level"),
    ("oei_all_half_values", "TestImages/AllHalfValues.exr",
     "eede573a0b59b79f21de15ee9d3b7649d58d8f2a8e7787ea34f192db3b3c84a4",
     "PIZ, every 16-bit half value including NaNs, infinities and denormals"),
]

WIDTH, HEIGHT = 23, 37


def canonical_sha(rgba: np.ndarray) -> str:
    bits = np.ascontiguousarray(rgba, dtype="<f4").view("<u4").copy()
    bits[np.isnan(rgba)] = 0x7FC00000
    return hashlib.sha256(bits.tobytes()).hexdigest()


def reference_rgba(path: str) -> tuple[np.ndarray, dict]:
    """Decodes ``path`` with the reference library and maps the default layer to RGBA float32."""
    with OpenEXR.File(path, separate_channels=True) as f:
        header = dict(f.header())
        channels = {name: np.asarray(c.pixels) for name, c in f.channels().items()}
    any_channel = next(iter(channels.values()))
    h, w = any_channel.shape[:2]

    def plane(name: str, default: float) -> np.ndarray:
        if name in channels:
            return channels[name].astype(np.float32)
        return np.full((h, w), default, dtype=np.float32)

    if not any(n in channels for n in ("R", "G", "B")) and "Y" in channels:
        y = plane("Y", 0.0)
        rgba = np.stack([y, y, y, plane("A", 1.0)], axis=-1)
    else:
        rgba = np.stack([plane("R", 0.0), plane("G", 0.0), plane("B", 0.0), plane("A", 1.0)], axis=-1)
    return np.ascontiguousarray(rgba, dtype=np.float32), header


def window(header: dict, key: str) -> list[int]:
    lo, hi = header[key]
    return [int(lo[0]), int(lo[1]), int(hi[0]), int(hi[1])]


def pattern(seed: int, kind: str) -> np.ndarray:
    """A deterministic, compressible test pattern: smooth ramps scaled by a per-block power of two (so values
    span several stops above and below 1), with a few negative values and exact zeros at seeded positions."""
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:HEIGHT, 0:WIDTH]
    # Ramps quantised to 1/1024 have at most ten significant bits, so they are exact in half and float alike
    # and leave the low bits of a float sample zero, which is what lets PIZ and ZIP find structure.
    base = np.round((xx + 1) / WIDTH * ((yy + 4) / HEIGHT) * 1024) / 1024
    # The PIZ variants keep to three stops: its range bitmap grows with the spread of values present.
    stops = 1 if kind.endswith("+") else 5
    exponents = rng.integers(-stops, stops + 1, size=((HEIGHT + 7) // 8, (WIDTH + 7) // 8))
    hdr = base * np.exp2(exponents[yy // 8, xx // 8])
    if not kind.endswith("+"):
        # Negative half and float values spread PIZ's range bitmap over most of the 16-bit space, which defeats
        # it on an image this small, so the PIZ fixtures use the non-negative variants ("half+", "float+");
        # the official AllHalfValues image covers PIZ with negative, non-finite and denormal values.
        hdr[rng.random(base.shape) < 0.03] *= -1
    hdr[rng.random(base.shape) < 0.03] = 0
    if kind.startswith("half"):
        return hdr.astype(np.float16)
    if kind.startswith("float"):
        return hdr.astype(np.float32)
    # Object-ID-like data: constant 4x4 blocks, as UINT channels usually hold, plus a few very large values.
    values = ((xx // 4) * 1000 + (yy // 4) * 7 + seed).astype(np.uint32)
    if not kind.endswith("+"):
        values[rng.random(base.shape) < 0.02] = np.uint32(4_000_000_000)
    return values


def raw_chunk_count(path: str) -> tuple[int, int]:
    """Returns (chunks stored uncompressed, total chunks) of a single-part file, read straight from its bytes."""
    import struct
    with open(path, "rb") as handle:
        data = handle.read()
    pos, attrs = 8, {}
    while data[pos] != 0:
        name_end = data.index(b"\0", pos)
        type_end = data.index(b"\0", name_end + 1)
        size = struct.unpack_from("<i", data, type_end + 1)[0]
        attrs[data[pos:name_end].decode()] = data[type_end + 5:type_end + 5 + size]
        pos = type_end + 5 + size
    pos += 1
    channels, cpos = [], 0
    chlist = attrs["channels"]
    while chlist[cpos] != 0:
        end = chlist.index(b"\0", cpos)
        ptype = struct.unpack_from("<i", chlist, end + 1)[0]
        channels.append(2 if ptype == 1 else 4)
        cpos = end + 17
    x0, y0, x1, y1 = struct.unpack("<4i", attrs["dataWindow"])
    width, height, compression = x1 - x0 + 1, y1 - y0 + 1, attrs["compression"][0]
    pixel = sum(channels)
    if "tiles" in attrs:
        tw, th = struct.unpack_from("<II", attrs["tiles"])
        nx, ny = -(-width // tw), -(-height // th)
        sizes = []
        for i in range(nx * ny):
            offset = struct.unpack_from("<Q", data, pos + 8 * i)[0]
            tx, ty = i % nx, i // nx
            w = min(tw, width - tx * tw)
            h = min(th, height - ty * th)
            sizes.append((struct.unpack_from("<i", data, offset + 16)[0], w * h * pixel))
    else:
        lines = {0: 1, 1: 1, 2: 1, 3: 16, 4: 32, 5: 16}.get(compression, 32)
        count = -(-height // lines)
        sizes = []
        for i in range(count):
            offset = struct.unpack_from("<Q", data, pos + 8 * i)[0]
            n = min(lines, height - i * lines)
            sizes.append((struct.unpack_from("<i", data, offset + 4)[0], n * width * pixel))
    return sum(1 for packed, raw in sizes if packed >= raw), len(sizes)


def tiles(x: int, y: int) -> OpenEXR.TileDescription:
    t = OpenEXR.TileDescription()
    t.xSize, t.ySize, t.mode, t.roundingMode = x, y, OpenEXR.ONE_LEVEL, OpenEXR.ROUND_DOWN
    return t


C = OpenEXR  # brevity in the table below

# Generated fixtures: (name, header overrides, channel spec, notes, expected exception or None).
# A channel spec maps channel names to (pattern kind, seed).
GENERATED = [
    ("ref_scan_none_rgba_half", {"compression": C.NO_COMPRESSION}, {"R": ("half", 1), "G": ("half", 2), "B": ("half", 3), "A": ("half", 4)}, "NONE, RGBA half", None),
    ("ref_scan_rle_rgba_half", {"compression": C.RLE_COMPRESSION}, {"R": ("half", 1), "G": ("half", 2), "B": ("half", 3), "A": ("half", 4)}, "RLE, RGBA half", None),
    ("ref_scan_zips_rgba_half", {"compression": C.ZIPS_COMPRESSION}, {"R": ("half", 1), "G": ("half", 2), "B": ("half", 3), "A": ("half", 4)}, "ZIPS, RGBA half", None),
    ("ref_scan_zip_rgba_half", {"compression": C.ZIP_COMPRESSION}, {"R": ("half", 1), "G": ("half", 2), "B": ("half", 3), "A": ("half", 4)}, "ZIP (16-line chunks), RGBA half", None),
    ("ref_scan_piz_rgba_half", {"compression": C.PIZ_COMPRESSION}, {"R": ("half+", 1), "G": ("half+", 2), "B": ("half+", 3), "A": ("half+", 4)}, "PIZ (32-line chunks), RGBA half", None),
    ("ref_scan_pxr24_rgba_half", {"compression": C.PXR24_COMPRESSION}, {"R": ("half", 1), "G": ("half", 2), "B": ("half", 3), "A": ("half", 4)}, "PXR24, RGBA half (lossless for half)", None),
    ("ref_scan_zip_rgb_float", {"compression": C.ZIP_COMPRESSION}, {"R": ("float", 5), "G": ("float", 6), "B": ("float", 7)}, "ZIP, RGB float", None),
    ("ref_scan_piz_rgb_float", {"compression": C.PIZ_COMPRESSION}, {"R": ("float+", 5), "G": ("float+", 6), "B": ("float+", 7)}, "PIZ, RGB float (two 16-bit words per sample)", None),
    ("ref_scan_pxr24_rgb_float", {"compression": C.PXR24_COMPRESSION}, {"R": ("float", 5), "G": ("float", 6), "B": ("float", 7)}, "PXR24, RGB float (lossy: 24-bit floats)", None),
    ("ref_scan_rle_rgb_uint", {"compression": C.RLE_COMPRESSION}, {"R": ("uint", 8), "G": ("uint", 9), "B": ("uint", 10)}, "RLE, RGB uint", None),
    ("ref_scan_piz_rgb_uint", {"compression": C.PIZ_COMPRESSION}, {"R": ("uint+", 8), "G": ("uint+", 9), "B": ("uint+", 10)}, "PIZ, RGB uint", None),
    ("ref_scan_pxr24_rgb_uint", {"compression": C.PXR24_COMPRESSION}, {"R": ("uint", 8), "G": ("uint", 9), "B": ("uint", 10)}, "PXR24, RGB uint (lossless for uint)", None),
    ("ref_scan_zip_y_half", {"compression": C.ZIP_COMPRESSION}, {"Y": ("half", 11)}, "ZIP, luminance-only Y half", None),
    ("ref_scan_zips_ya_half", {"compression": C.ZIPS_COMPRESSION}, {"Y": ("half", 11), "A": ("half", 12)}, "ZIPS, luminance and alpha", None),
    ("ref_scan_zip_mixed_types", {"compression": C.ZIP_COMPRESSION},
     {"R": ("half", 13), "G": ("float", 14), "B": ("uint", 15), "Z": ("float", 16), "diffuse.R": ("half", 17)},
     "ZIP, R half / G float / B uint, plus a depth channel and a layered channel the decoder must skip", None),
    ("ref_scan_none_data_window", {"compression": C.NO_COMPRESSION,
                                   "dataWindow": (np.array([-5, 3], np.int32), np.array([-5 + WIDTH - 1, 3 + HEIGHT - 1], np.int32)),
                                   "displayWindow": (np.array([0, 0], np.int32), np.array([63, 47], np.int32))},
     {"R": ("half", 18), "G": ("half", 19), "B": ("half", 20)}, "NONE, data window (-5,3)-(31,43) in display window (0,0)-(63,47)", None),
    ("ref_scan_rle_decreasing_y", {"compression": C.RLE_COMPRESSION, "lineOrder": C.DECREASING_Y},
     {"R": ("half", 21), "G": ("half", 22), "B": ("half", 23)}, "RLE, DECREASING_Y line order", None),
    ("ref_scan_piz_decreasing_y", {"compression": C.PIZ_COMPRESSION, "lineOrder": C.DECREASING_Y},
     {"R": ("half+", 21), "G": ("half+", 22), "B": ("half+", 23)}, "PIZ, DECREASING_Y line order", None),
    ("ref_tiled_none_rgba_half", {"compression": C.NO_COMPRESSION, "type": C.tiledimage, "tiles": tiles(16, 8)},
     {"R": ("half", 24), "G": ("half", 25), "B": ("half", 26), "A": ("half", 27)}, "tiled 16x8 ONE_LEVEL, NONE, partial edge tiles", None),
    ("ref_tiled_rle_rgb_half", {"compression": C.RLE_COMPRESSION, "type": C.tiledimage, "tiles": tiles(8, 8)},
     {"R": ("half", 24), "G": ("half", 25), "B": ("half", 26)}, "tiled 8x8, RLE", None),
    ("ref_tiled_zips_rgb_half", {"compression": C.ZIPS_COMPRESSION, "type": C.tiledimage, "tiles": tiles(12, 10)},
     {"R": ("half", 24), "G": ("half", 25), "B": ("half", 26)}, "tiled 12x10, ZIPS", None),
    ("ref_tiled_zip_rgb_float", {"compression": C.ZIP_COMPRESSION, "type": C.tiledimage, "tiles": tiles(16, 16)},
     {"R": ("float", 28), "G": ("float", 29), "B": ("float", 30)}, "tiled 16x16, ZIP, float", None),
    ("ref_tiled_piz_rgba_half", {"compression": C.PIZ_COMPRESSION, "type": C.tiledimage, "tiles": tiles(32, 32)},
     {"R": ("half+", 24), "G": ("half+", 25), "B": ("half+", 26), "A": ("half+", 27)}, "tiled 32x32 over a 23x37 image (clipped tiles), PIZ", None),
    ("ref_tiled_pxr24_rgb_float", {"compression": C.PXR24_COMPRESSION, "type": C.tiledimage, "tiles": tiles(32, 32)},
     {"R": ("float", 28), "G": ("float", 29), "B": ("float", 30)}, "tiled 32x32, PXR24, float", None),
    ("ref_unsupported_b44", {"compression": C.B44_COMPRESSION}, {"R": ("half", 31)}, "B44 is deliberately not decoded", "NotSupportedException"),
    ("ref_unsupported_b44a", {"compression": C.B44A_COMPRESSION}, {"R": ("half", 31)}, "B44A is deliberately not decoded", "NotSupportedException"),
    ("ref_unsupported_dwaa", {"compression": C.DWAA_COMPRESSION}, {"R": ("half", 31)}, "DWAA is deliberately not decoded", "NotSupportedException"),
    ("ref_unsupported_dwab", {"compression": C.DWAB_COMPRESSION}, {"R": ("half", 31)}, "DWAB is deliberately not decoded", "NotSupportedException"),
    ("ref_unsupported_htj2k256", {"compression": C.HTJ2K256_COMPRESSION}, {"R": ("half", 31)}, "HTJ2K is deliberately not decoded", "NotSupportedException"),
    ("ref_unsupported_zstd", {"compression": C.ZSTD_COMPRESSION}, {"R": ("half", 31)}, "ZSTD is deliberately not decoded", "NotSupportedException"),
    ("ref_unsupported_depth_only", {"compression": C.ZIP_COMPRESSION}, {"Z": ("float", 32)}, "a lone depth channel has no colour to render", "NotSupportedException"),
]


def write_generated(out_dir: str, name: str, header: dict, spec: dict) -> str:
    channels = {ch: pattern(seed, kind) for ch, (kind, seed) in spec.items()}
    path = os.path.join(out_dir, name + ".exr")
    with OpenEXR.File(header, channels) as f:
        f.write(path)
    return path


def write_piz_w16(out_dir: str) -> str:
    """A PIZ file whose first chunk holds more than 2^14 distinct 16-bit words, which makes the reference
    encoder use the 16-bit wavelet. The words are consecutive half bit patterns in raster order, so the wavelet
    coefficients stay small and the chunk still compresses (a raw chunk would not exercise PIZ at all)."""
    width, height = 1024, 40
    bits = np.zeros(width * height, dtype=np.uint16)
    bits[:17000] = np.arange(1, 17001, dtype=np.uint16)
    bits[width * 32:width * 32 + 500] = np.arange(0x3C00, 0x3C00 + 500, dtype=np.uint16)
    y = bits.view(np.float16).reshape(height, width)
    path = os.path.join(out_dir, "ref_scan_piz_w16_half.exr")
    with OpenEXR.File({"compression": C.PIZ_COMPRESSION}, {"Y": y}) as f:
        f.write(path)
    return path


def write_multipart(out_dir: str) -> str:
    path = os.path.join(out_dir, "ref_unsupported_multipart.exr")
    parts = [OpenEXR.Part({"compression": C.ZIP_COMPRESSION}, {"R": pattern(33, "half")}, "left"),
             OpenEXR.Part({"compression": C.ZIP_COMPRESSION}, {"G": pattern(34, "half")}, "right")]
    with OpenEXR.File(parts) as f:
        f.write(path)
    return path


def entry_for(name: str, path: str, notes: str, expect: str | None, source: str, store_f32: bool, out_dir: str) -> dict:
    with open(path, "rb") as handle:
        encoded = handle.read()
    entry = {"name": name, "file": os.path.basename(path), "notes": notes, "source": source,
             "sha256": hashlib.sha256(encoded).hexdigest(), "size": len(encoded)}
    if expect is not None:
        entry["expect"] = expect
        with OpenEXR.File(path) as f:
            h = f.parts[0].header if len(f.parts) > 1 else f.header()
            entry["compression"] = str(h["compression"]).split(".")[-1]
        return entry
    rgba, header = reference_rgba(path)
    data = window(header, "dataWindow")
    raw_chunks, total_chunks = raw_chunk_count(path)
    if entry["source"].startswith("OpenEXR") and header["compression"] != OpenEXR.NO_COMPRESSION and raw_chunks == total_chunks:
        raise SystemExit(f"{name}: every chunk was stored uncompressed, so the fixture would not exercise its codec")
    entry.update({
        "raw_chunks": raw_chunks, "chunks": total_chunks,
        "width": data[2] - data[0] + 1, "height": data[3] - data[1] + 1,
        "data_window": data, "display_window": window(header, "displayWindow"),
        "compression": str(header["compression"]).split(".")[-1],
        "tiled": "tiles" in header,
        "line_order": str(header.get("lineOrder", "INCREASING_Y")).split(".")[-1],
        "sha256_f32": canonical_sha(rgba),
        "samples": sample_pixels(rgba),
    })
    if store_f32:
        rgba.astype("<f4").tofile(os.path.join(out_dir, name + ".f32"))
        entry["f32"] = name + ".f32"
    return entry


def sample_pixels(rgba: np.ndarray) -> list:
    """Nine pixels on a 3x3 grid, as [x, y, r, g, b, a] with non-finite values spelled out, for diagnostics."""
    h, w = rgba.shape[:2]
    out = []
    for y in (0, h // 2, h - 1):
        for x in (0, w // 2, w - 1):
            out.append([x, y] + [v if np.isfinite(v) else repr(v) for v in (float(c) for c in rgba[y, x])])
    return out


def build(official_dir: str | None, out_dir: str) -> list[dict]:
    entries = []
    if official_dir is not None:
        for name, relative, digest, notes in OFFICIAL:
            source = os.path.join(official_dir, *relative.split("/"))
            with open(source, "rb") as handle:
                actual = hashlib.sha256(handle.read()).hexdigest()
            if actual != digest:
                raise SystemExit(f"{relative}: SHA-256 {actual} does not match the pinned {digest}")
            target = os.path.join(out_dir, name + ".exr")
            shutil.copyfile(source, target)
            entries.append(entry_for(name, target, notes, None,
                                     "openexr-images/" + relative + " (BSD-3-Clause)", store_f32=False, out_dir=out_dir))
    for name, header, spec, notes, expect in GENERATED:
        path = write_generated(out_dir, name, header, spec)
        entries.append(entry_for(name, path, notes, expect, f"OpenEXR {OpenEXR.OPENEXR_VERSION} Python bindings",
                                 store_f32=expect is None, out_dir=out_dir))
    path = write_piz_w16(out_dir)
    entries.append(entry_for("ref_scan_piz_w16_half", path,
                             "PIZ with more than 16384 distinct words in a chunk (the 16-bit wavelet), luminance half",
                             None, f"OpenEXR {OpenEXR.OPENEXR_VERSION} Python bindings", False, out_dir))
    path = write_multipart(out_dir)
    entries.append(entry_for("ref_unsupported_multipart", path, "multi-part files are deliberately not decoded",
                             "NotSupportedException", f"OpenEXR {OpenEXR.OPENEXR_VERSION} Python bindings", False, out_dir))
    return entries


def copy_damaged(official_dir: str, out_dir: str) -> list[dict]:
    """Copies the small fuzzer-found files of openexr-images/Damaged (inputs that once crashed or misled the
    reference library) as ``damaged/<name>.bin``. They carry no ground truth: a decoder must either decode them
    or throw its documented exceptions, quickly and without large allocations."""
    source_dir = os.path.join(official_dir, "Damaged")
    target_dir = os.path.join(out_dir, "damaged")
    os.makedirs(target_dir, exist_ok=True)
    entries = []
    for name in sorted(os.listdir(source_dir)):
        source = os.path.join(source_dir, name)
        if name.endswith(".rst") or os.path.getsize(source) > 4096:
            continue
        with open(source, "rb") as handle:
            data = handle.read()
        with open(os.path.join(target_dir, name + ".bin"), "wb") as handle:
            handle.write(data)
        entries.append({"file": name + ".bin", "source": "openexr-images/Damaged/" + name + " (BSD-3-Clause)",
                        "sha256": hashlib.sha256(data).hexdigest(), "size": len(data)})
    with open(os.path.join(target_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(entries, handle, indent=1)
        handle.write("\n")
    return entries


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--official", help="directory holding a checkout (or partial copy) of openexr-images")
    parser.add_argument("--check", action="store_true", help="re-derive ground truth for the files on disk and compare")
    args = parser.parse_args()

    manifest_path = os.path.join(HERE, "manifest.json")
    if args.check:
        with open(manifest_path, encoding="utf-8") as handle:
            manifest = json.load(handle)
        failures = 0
        for entry in manifest:
            if "sha256_f32" not in entry:
                continue
            rgba, _ = reference_rgba(os.path.join(HERE, entry["file"]))
            if canonical_sha(rgba) != entry["sha256_f32"]:
                print("MISMATCH", entry["name"])
                failures += 1
        print(f"checked {len(manifest)} entries, {failures} mismatch(es)")
        return 1 if failures else 0

    if args.official is None:
        print("--official is required to rebuild (the official files are copied, not regenerated)", file=sys.stderr)
        return 2
    entries = build(args.official, HERE)
    if os.path.isdir(os.path.join(args.official, "Damaged")):
        print(f"copied {len(copy_damaged(args.official, HERE))} damaged files")
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(entries, handle, indent=2)
        handle.write("\n")
    print(f"wrote {len(entries)} fixtures to {HERE}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
