#!/usr/bin/env python
"""Generator for the benchmark corpus.

The corpus is generated rather than committed: a 3032x2008 photograph in nine container formats plus a
2480x3508 scanned page and twenty batch JPEGs is far too large to keep in git, and every byte of it is
derived from fixed seeds, so any checkout can rebuild exactly the same inputs.

    python benchmarks/corpus/generate.py            # write anything missing or out of date
    python benchmarks/corpus/generate.py --force    # rewrite everything
    EASYIMAGESHARP_BENCH_SMALL=1 python benchmarks/corpus/generate.py

The small mode divides every dimension by eight. It exists for CI, which only needs the benchmarks to run
end to end under BenchmarkDotNet's Dry job; the numbers such a run produces are meaningless.

What is written into this directory:

    photo.png   3032x2008 RGB: octaves of value noise per channel down to eight pixels per cell, a grain
                term under them, soft shapes, a vignette and a few hard edges. That is the mix of flat
                areas, gradients, fine texture and sensor grain that makes a codec behave the way it
                behaves on a real photograph, and it compresses like one (about 10 MiB as PNG)
    photo.jpeg  the same pixels at quality 90, 4:2:0
    photo.bmp / photo.ppm / photo.tga / photo.tiff / photo.webp / photo.gif / photo.qoi
                the same pixels in the remaining containers the library decodes
    scan.png    2480x3508 8-bit grayscale, an A4 page at 300 DPI: synthetic text blocks under an
                illumination gradient with sparse scanner speckle, which is what "A4 at 300 DPI, L8"
                means in the README table. It compresses about 13:1, the way a real scan does
    batch/NN.jpg
                twenty distinct 1920x1280 photographs at quality 88, the input to the load-resize-save
                pipeline benchmark
    manifest.json
                what Corpus.EnsurePresent reads to decide the corpus is intact

Every file here is written by Pillow, i.e. by libjpeg-turbo, libwebp and zlib - never by EasyImageSharp.
A decode benchmark therefore measures this library against a foreign encoder's output, which is the same
discipline the test fixture corpus follows.

Requires Pillow >= 11 and NumPy >= 2, the toolchain CONTRIBUTING already pins for fixtures. Pillow 11.3
and newer can write QOI; on an older Pillow the spec-derived encoder below is used instead.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
import sys
import time

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))

# Bumped whenever the pixels or the encoder settings change, so a stale corpus is rebuilt rather than
# silently benchmarked.
GENERATOR = 1

PHOTO_SIZE = (3032, 2008)
SCAN_SIZE = (2480, 3508)
BATCH_SIZE = (1920, 1280)
BATCH_COUNT = 20


# ------------------------------------------------------------------------------------------------------
# Pixel sources
# ------------------------------------------------------------------------------------------------------

def _value_noise(shape: tuple[int, int], cells: int, rng: np.random.Generator) -> np.ndarray:
    """One octave of smoothly interpolated value noise in [0, 1]."""
    height, width = shape
    grid = rng.random((cells + 1, cells + 1), dtype=np.float32)
    ys = np.linspace(0.0, cells, height, endpoint=False, dtype=np.float32)
    xs = np.linspace(0.0, cells, width, endpoint=False, dtype=np.float32)
    y0 = np.floor(ys).astype(np.intp)
    x0 = np.floor(xs).astype(np.intp)
    fy = (ys - y0)[:, None]
    fx = (xs - x0)[None, :]

    # Smoothstep keeps the octave free of the visible grid seams a linear blend would leave.
    fy = fy * fy * (3.0 - (2.0 * fy))
    fx = fx * fx * (3.0 - (2.0 * fx))

    top = (grid[np.ix_(y0, x0)] * (1.0 - fx)) + (grid[np.ix_(y0, x0 + 1)] * fx)
    bottom = (grid[np.ix_(y0 + 1, x0)] * (1.0 - fx)) + (grid[np.ix_(y0 + 1, x0 + 1)] * fx)
    return (top * (1.0 - fy)) + (bottom * fy)


def _photo(width: int, height: int, seed: int) -> Image.Image:
    """A photographic stand-in: octaves of value noise, two soft shapes, a vignette and hard edges.

    The recipe is the one gen_vp8enc.py uses for its encoder fixtures, widened to full frame size and
    given a seed so the twenty batch images differ from one another.
    """
    rng = np.random.default_rng(seed)

    # Octaves run down to roughly eight pixels per cell, and a per-pixel grain term sits under them. Both
    # matter: a frame built only from the coarse octaves gen_vp8enc.py uses at 224x160 is, at 3032x2008,
    # smooth enough to compress to about a tenth of what a real photograph of that size does, which would
    # make every lossless codec in the decode benchmark look far better than it is on real input.
    finest = max(3, width // 8)
    octaves = []
    cells = 3
    while cells <= finest:
        octaves.append(cells)
        cells *= 2

    channels = []
    for base in (0.46, 0.42, 0.38):
        field = np.zeros((height, width), dtype=np.float32)
        amplitude = np.float32(0.30)
        for cells in octaves:
            field += amplitude * _value_noise((height, width), cells, rng)
            amplitude *= np.float32(0.62)
        field += rng.normal(0.0, 0.017, (height, width)).astype(np.float32)
        channels.append(base + field - field.mean())

    ys = np.linspace(-1.0, 1.0, height, dtype=np.float32)[:, None]
    xs = np.linspace(-1.0, 1.0, width, dtype=np.float32)[None, :]

    disc = np.exp(-6.0 * (((xs - 0.30) ** 2) + ((ys + 0.20) ** 2)))
    band = np.exp(-40.0 * ((ys - (0.35 * xs) - 0.45) ** 2))
    vignette = 1.0 - (0.35 * ((xs ** 2) + (ys ** 2)))

    channels[0] = (channels[0] + (0.34 * disc) - (0.18 * band)) * vignette
    channels[1] = (channels[1] + (0.26 * disc) - (0.14 * band)) * vignette
    channels[2] = (channels[2] + (0.10 * disc) - (0.06 * band)) * vignette

    rgb = np.clip(np.stack(channels, axis=-1), 0.0, 1.0)
    data = (rgb * 255.0 + 0.5).astype(np.uint8)

    # A handful of hard edges. Smooth noise alone is unrealistically kind to every predictor in every
    # codec; real frames contain foliage, text and specular highlights that no filter predicts well.
    edge = max(2, height // 220)
    for i, y in enumerate(range(height // 6, height, max(1, height // 7))):
        data[y:y + edge, (width // 8):(7 * width) // 8] = (250, 248, 240) if (i % 2 == 0) else (14, 16, 22)
    for i, x in enumerate(range(width // 5, width, max(1, width // 6))):
        data[(height // 4):(3 * height) // 4, x:x + edge] = (18, 22, 30) if (i % 2 == 0) else (236, 230, 210)

    return Image.fromarray(data)


def _scan_page(width: int, height: int, seed: int) -> Image.Image:
    """An A4-at-300-DPI page: blocks of synthetic glyphs under an illumination gradient.

    Glyphs are rectangles rather than a real font: the benchmarks that use this page measure grayscale
    conversion, global and local thresholding and deskew, none of which cares about glyph shape, and a
    rectangle renderer needs no font file and reproduces identically everywhere.
    """
    rng = np.random.default_rng(seed)
    page = np.ones((height, width), dtype=np.float32)

    scale = height / 3508.0
    margin_x = max(2, int(240 * scale))
    margin_y = max(2, int(260 * scale))
    pitch = max(4, int(64 * scale))
    x_height = max(2, int(30 * scale))
    advance = max(2, int(26 * scale))
    ink_width = max(1, advance - max(1, advance // 5))

    y = margin_y
    while y + (3 * pitch) < height - margin_y:
        heading = rng.random() < 0.08
        line_height = int(x_height * (1.6 if heading else 1.0))
        x = margin_x + (int(60 * scale) if (not heading and rng.random() < 0.12) else 0)
        limit = width - margin_x - (int(400 * scale) if rng.random() < 0.18 else 0)
        while x + advance < limit:
            for _ in range(int(rng.integers(2, 11))):
                if x + advance >= limit:
                    break
                top = y + line_height - int(line_height * rng.uniform(0.62, 1.0))
                bottom = y + line_height + (int(line_height * 0.34) if rng.random() < 0.18 else 0)
                page[top:min(bottom, height), x:min(x + ink_width, width)] = np.float32(
                    rng.uniform(0.05, 0.18))
                x += advance
            x += advance  # inter-word space
        y += pitch + (pitch if (heading or rng.random() < 0.06) else 0)

    # Uneven illumination: the reason a document pipeline needs a local threshold rather than a global one.
    ys = np.linspace(-1.0, 1.0, height, dtype=np.float32)[:, None]
    xs = np.linspace(-1.0, 1.0, width, dtype=np.float32)[None, :]
    page = page * (1.0 - (0.13 * (((xs + 0.45) ** 2) + ((ys - 0.55) ** 2))))

    # Scanner speckle: a few levels on one pixel in sixty, rather than Gaussian noise on every pixel.
    # The distinction matters twice over. Sparse speckle is what a real flatbed produces, and it is what a
    # local threshold has to survive; per-pixel Gaussian noise, even at half a percent of full scale, puts
    # two bits of entropy under every pixel and stops the page compressing like a document at all - and
    # InflateBenchmarks uses this file as its highly-compressible case, where that is the whole point.
    speckle = rng.random(page.shape, dtype=np.float32) < 0.017
    page[speckle] += rng.integers(-4, 5, int(np.count_nonzero(speckle))).astype(np.float32) / 255.0

    return Image.fromarray((np.clip(page, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8))


# ------------------------------------------------------------------------------------------------------
# QOI (spec-derived encoder, used only when Pillow is too old to write QOI itself)
# ------------------------------------------------------------------------------------------------------

def qoi_encode(rgb: np.ndarray) -> bytes:
    """Encodes H x W x 3 uint8 pixels exactly like the reference qoi.h encoder, with alpha fixed at 255.

    Copied from tests/EasyImageSharp.Tests/Fixtures/gen_smallformats.py and narrowed to three channels.
    It is duplicated rather than imported because the fixture generators are a self-contained tree, and
    importing across would couple the benchmark corpus to the test corpus.
    """
    height, width = rgb.shape[:2]
    out = bytearray(b"qoif" + struct.pack(">IIBB", width, height, 3, 0))
    index = [(0, 0, 0)] * 64
    prev = (0, 0, 0)
    run = 0
    flat = rgb.reshape(-1, 3).tolist()
    n = len(flat)
    for i in range(n):
        row = flat[i]
        px = (row[0], row[1], row[2])
        if px == prev:
            run += 1
            if run == 62 or i == n - 1:
                out.append(0xC0 | (run - 1))
                run = 0
            continue

        if run > 0:
            out.append(0xC0 | (run - 1))
            run = 0
        r, g, b = px
        pos = ((r * 3) + (g * 5) + (b * 7) + (255 * 11)) % 64
        if index[pos] == px:
            out.append(pos)                                                  # QOI_OP_INDEX
        else:
            index[pos] = px
            vr = ((r - prev[0] + 128) & 0xFF) - 128
            vg = ((g - prev[1] + 128) & 0xFF) - 128
            vb = ((b - prev[2] + 128) & 0xFF) - 128
            vg_r = ((vr - vg + 128) & 0xFF) - 128
            vg_b = ((vb - vg + 128) & 0xFF) - 128
            if -3 < vr < 2 and -3 < vg < 2 and -3 < vb < 2:
                out.append(0x40 | ((vr + 2) << 4) | ((vg + 2) << 2) | (vb + 2))       # QOI_OP_DIFF
            elif -9 < vg_r < 8 and -33 < vg < 32 and -9 < vg_b < 8:
                out.append(0x80 | (vg + 32))                                          # QOI_OP_LUMA
                out.append(((vg_r + 8) << 4) | (vg_b + 8))
            else:
                out += bytes([0xFE, r, g, b])                                         # QOI_OP_RGB
        prev = px

    out += b"\0" * 7 + b"\x01"
    return bytes(out)


# ------------------------------------------------------------------------------------------------------
# Writing and verification
# ------------------------------------------------------------------------------------------------------

def _psnr(a: np.ndarray, b: np.ndarray) -> float:
    diff = a.astype(np.float64) - b.astype(np.float64)
    mse = float(np.mean(diff * diff))
    return float("inf") if mse == 0.0 else 10.0 * float(np.log10((255.0 * 255.0) / mse))


def _sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _write_photo_containers(out_dir: str, image: Image.Image, log) -> None:
    """Writes the same pixels into every container the library decodes."""
    log("photo.png")
    image.save(os.path.join(out_dir, "photo.png"), format="PNG", compress_level=6)
    log("photo.jpeg")
    image.save(os.path.join(out_dir, "photo.jpeg"), format="JPEG", quality=90, subsampling="4:2:0")
    log("photo.bmp")
    image.save(os.path.join(out_dir, "photo.bmp"), format="BMP")
    log("photo.ppm")
    image.save(os.path.join(out_dir, "photo.ppm"), format="PPM")
    log("photo.tga")
    image.save(os.path.join(out_dir, "photo.tga"), format="TGA", compression="tga_rle")
    log("photo.tiff")
    image.save(os.path.join(out_dir, "photo.tiff"), format="TIFF", compression="tiff_deflate")
    log("photo.webp")
    image.save(os.path.join(out_dir, "photo.webp"), format="WEBP", lossless=True, quality=100, method=4)
    log("photo.gif")
    image.convert("P", palette=Image.Palette.ADAPTIVE, colors=256).save(
        os.path.join(out_dir, "photo.gif"), format="GIF")

    qoi_path = os.path.join(out_dir, "photo.qoi")
    try:
        log("photo.qoi")
        image.save(qoi_path, format="QOI")
    except (KeyError, OSError, ValueError):
        log("photo.qoi (spec-derived encoder; this Pillow cannot write QOI)")
        with open(qoi_path, "wb") as handle:
            handle.write(qoi_encode(np.asarray(image)))


def _verify(out_dir: str, photo: np.ndarray, scan: np.ndarray, batch: list[np.ndarray]) -> None:
    """Reopens every written file with Pillow and checks it still carries the pixels it was given."""
    lossless = ("photo.png", "photo.bmp", "photo.ppm", "photo.tga", "photo.tiff", "photo.webp", "photo.qoi")
    for name in lossless:
        with Image.open(os.path.join(out_dir, name)) as handle:
            got = np.asarray(handle.convert("RGB"))
        if not np.array_equal(got, photo):
            raise SystemExit(f"{name} did not survive the round trip: {_psnr(got, photo):.2f} dB")

    with Image.open(os.path.join(out_dir, "photo.jpeg")) as handle:
        jpeg_psnr = _psnr(np.asarray(handle.convert("RGB")), photo)
    if jpeg_psnr < 34.0:
        raise SystemExit(f"photo.jpeg is only {jpeg_psnr:.2f} dB; quality 90 should be far better")

    with Image.open(os.path.join(out_dir, "photo.gif")) as handle:
        gif_psnr = _psnr(np.asarray(handle.convert("RGB")), photo)
    if gif_psnr < 27.0:
        raise SystemExit(f"photo.gif is only {gif_psnr:.2f} dB; a 256-colour palette should do better")

    with Image.open(os.path.join(out_dir, "scan.png")) as handle:
        if not np.array_equal(np.asarray(handle.convert("L")), scan):
            raise SystemExit("scan.png did not survive the round trip")

    worst_batch = float("inf")
    for i, expected in enumerate(batch):
        path = os.path.join(out_dir, "batch", f"{i:02d}.jpg")
        with Image.open(path) as handle:
            worst_batch = min(worst_batch, _psnr(np.asarray(handle.convert("RGB")), expected))
    if worst_batch < 30.0:
        raise SystemExit(f"a batch JPEG is only {worst_batch:.2f} dB; quality 88 should be far better")

    print(f"verified: every lossless container byte-exact, photo.jpeg {jpeg_psnr:.1f} dB, "
          f"photo.gif {gif_psnr:.1f} dB, worst batch JPEG {worst_batch:.1f} dB")


def _manifest_entries(out_dir: str, sizes: dict[str, tuple[int, int]]) -> list[dict[str, object]]:
    entries: list[dict[str, object]] = []
    for name in sorted(sizes):
        path = os.path.join(out_dir, name.replace("/", os.sep))
        width, height = sizes[name]
        entries.append({
            "name": name,
            "width": width,
            "height": height,
            "bytes": os.path.getsize(path),
            "sha256": _sha256(path),
        })
    return entries


def _is_current(out_dir: str, manifest_path: str, small: bool) -> bool:
    """True when the files on disk are exactly the ones the manifest describes.

    The check is against what is on disk, never against what a fresh run would produce, so a Pillow or
    NumPy upgrade that changes a byte somewhere does not force a rebuild on every invocation.
    """
    if not os.path.isfile(manifest_path):
        return False
    try:
        with open(manifest_path, "r", encoding="utf-8") as handle:
            manifest = json.load(handle)
    except (OSError, ValueError):
        return False

    if manifest.get("generator") != GENERATOR or bool(manifest.get("small")) != small:
        return False

    for entry in manifest.get("files", []):
        path = os.path.join(out_dir, str(entry["name"]).replace("/", os.sep))
        if not os.path.isfile(path) or os.path.getsize(path) != entry["bytes"]:
            return False
        if _sha256(path) != entry["sha256"]:
            return False
    return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Generate the EasyImageSharp benchmark corpus.")
    parser.add_argument("--force", action="store_true", help="rewrite every file even if it is up to date")
    parser.add_argument("--quiet", action="store_true", help="print only the final summary")
    args = parser.parse_args(argv)

    out_dir = HERE
    manifest_path = os.path.join(out_dir, "manifest.json")
    small = bool(os.environ.get("EASYIMAGESHARP_BENCH_SMALL"))
    divisor = 8 if small else 1

    if not args.force and _is_current(out_dir, manifest_path, small):
        print(f"corpus is up to date ({'small' if small else 'full'} mode); pass --force to rewrite it")
        return 0

    def log(message: str) -> None:
        if not args.quiet:
            print(f"  writing {message}", flush=True)

    started = time.perf_counter()
    os.makedirs(os.path.join(out_dir, "batch"), exist_ok=True)

    photo_w, photo_h = PHOTO_SIZE[0] // divisor, PHOTO_SIZE[1] // divisor
    scan_w, scan_h = SCAN_SIZE[0] // divisor, SCAN_SIZE[1] // divisor
    batch_w, batch_h = BATCH_SIZE[0] // divisor, BATCH_SIZE[1] // divisor

    print(f"generating the {'small' if small else 'full'} corpus into {out_dir}", flush=True)
    photo = _photo(photo_w, photo_h, 20260902)
    _write_photo_containers(out_dir, photo, log)

    log("scan.png")
    scan = _scan_page(scan_w, scan_h, 20260903)
    scan.save(os.path.join(out_dir, "scan.png"), format="PNG", compress_level=6)

    batch_arrays = []
    for i in range(BATCH_COUNT):
        log(f"batch/{i:02d}.jpg")
        frame = _photo(batch_w, batch_h, 20260904 + (i * 7))
        frame.save(os.path.join(out_dir, "batch", f"{i:02d}.jpg"), format="JPEG", quality=88,
                   subsampling="4:2:0")
        batch_arrays.append(np.asarray(frame))

    _verify(out_dir, np.asarray(photo), np.asarray(scan), batch_arrays)

    sizes: dict[str, tuple[int, int]] = {
        "photo.png": (photo_w, photo_h),
        "photo.jpeg": (photo_w, photo_h),
        "photo.bmp": (photo_w, photo_h),
        "photo.ppm": (photo_w, photo_h),
        "photo.tga": (photo_w, photo_h),
        "photo.tiff": (photo_w, photo_h),
        "photo.webp": (photo_w, photo_h),
        "photo.gif": (photo_w, photo_h),
        "photo.qoi": (photo_w, photo_h),
        "scan.png": (scan_w, scan_h),
    }
    for i in range(BATCH_COUNT):
        sizes[f"batch/{i:02d}.jpg"] = (batch_w, batch_h)

    files = _manifest_entries(out_dir, sizes)
    manifest = {
        "generator": GENERATOR,
        "pillow": Image.__version__,
        "numpy": np.__version__,
        "small": small,
        "files": files,
    }
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(manifest, handle, indent=1)
        handle.write("\n")

    total = sum(int(entry["bytes"]) for entry in files)
    elapsed = time.perf_counter() - started
    print(f"wrote {len(files)} files, {total / (1024 * 1024):.1f} MiB, in {elapsed:.1f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
