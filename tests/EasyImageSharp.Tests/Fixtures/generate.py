#!/usr/bin/env python
"""Generates the independently-encoded fixture corpus under Fixtures/<format>/.

Requires Python 3.11 + Pillow 11 + numpy. Run from this directory:

    python generate.py

Layout per format folder (png/, bmp/, tiff/, ...):
  <name>.<ext>          the fixture (written by Pillow or hand-assembled byte by byte)
  <name>.rgba           ground truth: width*height*4 bytes of RGBA, row-major, top-left origin;
                        multi-frame fixtures concatenate every frame in order
  <name>.expected.png   Pillow-written 8-bit RGBA rendering of the first frame (for eyeballing)
  manifest.json         list of entries: name, file, width, height, frames, notes, plus per-format
                        header facts; "expect" names the exception type the decoder must throw when
                        the file exercises a feature the library deliberately does not implement

The generator is deterministic: every pattern is derived from fixed seeds so re-running it produces
byte-identical fixtures. Each gen_<format>(out_dir) function is self-contained.
"""
from __future__ import annotations

import io
import json
import os
import struct
import sys
import warnings
import zlib

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))


# ----------------------------------------------------------------------------------------------------
# GIF
# ----------------------------------------------------------------------------------------------------

def _p_image(size: tuple[int, int], palette: list[tuple[int, int, int]], fill: int = 0) -> Image.Image:
    """Creates a palette ("P") image with an exact palette so the encoder cannot alter any color."""
    im = Image.new("P", size, fill)
    flat: list[int] = []
    for r, g, b in palette:
        flat.extend((r, g, b))
    im.putpalette(flat)
    return im


def _fill_rect(im: Image.Image, box: tuple[int, int, int, int], index: int) -> None:
    x0, y0, x1, y1 = box
    px = im.load()
    for y in range(y0, y1):
        for x in range(x0, x1):
            px[x, y] = index


def _gif_static_source() -> Image.Image:
    """64x48 diagonal ramp over a 64-entry palette; entry i = (4i, 255-4i, (37i) % 256), pixel = (x+y) % 64."""
    palette = [(i * 4, 255 - (i * 4), (i * 37) % 256) for i in range(64)]
    im = _p_image((64, 48), palette)
    px = im.load()
    for y in range(48):
        for x in range(64):
            px[x, y] = (x + y) % 64
    return im


def gen_gif(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)

    # 1. static_rgb.gif / static_interlaced.gif: same source, GIF87a, global palette only.
    source = _gif_static_source()
    source.save(os.path.join(out_dir, "static_rgb.gif"), interlace=False)
    source.save(os.path.join(out_dir, "static_interlaced.gif"), interlace=True)

    # 2. transparent.gif: 40x30, left half red, right half green, transparent rectangle (10,8)-(30,22).
    #    Also carries a comment extension so the extension-skipping path is exercised.
    palette = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0)]
    im = _p_image((40, 30), palette, fill=0)
    _fill_rect(im, (20, 0, 40, 30), 1)
    _fill_rect(im, (10, 8, 30, 22), 3)
    im.save(os.path.join(out_dir, "transparent.gif"), transparency=3, interlace=False,
            comment=b"EasyImageSharp GIF fixture")

    # 3. animated_3frames.gif: 32x32, three full frames, disposal 2 (restore to background).
    palette = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255), (255, 255, 0), (0, 0, 0)]
    frames = []
    f0 = _p_image((32, 32), palette, fill=0)          # red
    _fill_rect(f0, (4, 4, 12, 12), 2)                 # blue square
    f1 = _p_image((32, 32), palette, fill=1)          # green
    _fill_rect(f1, (12, 12, 20, 20), 3)               # white square
    f2 = _p_image((32, 32), palette, fill=2)          # blue
    _fill_rect(f2, (20, 20, 28, 28), 4)               # yellow square
    frames = [f0, f1, f2]
    frames[0].save(os.path.join(out_dir, "animated_3frames.gif"), save_all=True, append_images=frames[1:],
                   duration=100, loop=0, disposal=2)

    # 4. animated_disposal_none.gif: 48x32, disposal 1; each frame adds one rectangle, so the encoder
    #    writes partial frames at offsets and the decoder must keep earlier content outside them.
    palette = [(128, 128, 128), (255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 0, 0)]
    f0 = _p_image((48, 32), palette, fill=0)          # gray
    _fill_rect(f0, (0, 0, 16, 16), 1)                 # red rect
    f1 = f0.copy()
    _fill_rect(f1, (16, 8, 32, 24), 2)                # + green rect
    f2 = f1.copy()
    _fill_rect(f2, (32, 16, 48, 32), 3)               # + blue rect
    f0.save(os.path.join(out_dir, "animated_disposal_none.gif"), save_all=True, append_images=[f1, f2],
            duration=100, loop=0, disposal=1)

    # 5. local_palette.gif: 24x16, two frames with different palettes; the second frame gets a local color table.
    palette_a = [(10, 20, 30), (200, 100, 50), (0, 0, 0), (255, 255, 255)]
    palette_b = [(0, 128, 255), (255, 0, 128), (128, 255, 0), (64, 64, 64)]
    f0 = _p_image((24, 16), palette_a, fill=0)
    _fill_rect(f0, (12, 0, 24, 16), 1)
    f1 = _p_image((24, 16), palette_b, fill=0)
    _fill_rect(f1, (12, 0, 24, 16), 1)
    f0.save(os.path.join(out_dir, "local_palette.gif"), save_all=True, append_images=[f1], duration=50)

    _check_gif_fixtures(out_dir)


def _parse_gif(path: str) -> dict:
    """Minimal structural parser used only to verify that the generated files have the intended layout."""
    with open(path, "rb") as f:
        data = f.read()
    pos = 0
    info: dict = {"version": data[3:6].decode(), "size": os.path.getsize(path), "images": [], "extensions": []}
    width, height, flags = struct.unpack_from("<HHB", data, 6)
    pos = 13
    info["screen"] = (width, height)
    if flags & 0x80:
        info["gct_depth"] = (flags & 7) + 1
        pos += 3 * (2 << (flags & 7))
    else:
        info["gct_depth"] = 0
    gce = None
    while pos < len(data):
        block = data[pos]
        pos += 1
        if block == 0x3B:
            break
        if block == 0x21:
            label = data[pos]
            pos += 1
            payload = b""
            while True:
                n = data[pos]
                pos += 1
                if n == 0:
                    break
                payload += data[pos:pos + n]
                pos += n
            info["extensions"].append(label)
            if label == 0xF9:
                packed = payload[0]
                gce = {"disposal": (packed >> 2) & 7, "transparent": payload[3] if packed & 1 else None,
                       "delay": struct.unpack_from("<H", payload, 1)[0]}
            continue
        if block == 0x2C:
            left, top, w, h, iflags = struct.unpack_from("<HHHHB", data, pos)
            pos += 9
            image = {"left": left, "top": top, "width": w, "height": h, "interlaced": bool(iflags & 0x40),
                     "lct_depth": (iflags & 7) + 1 if iflags & 0x80 else 0, "gce": gce}
            gce = None
            if iflags & 0x80:
                pos += 3 * (2 << (iflags & 7))
            image["min_code_size"] = data[pos]
            pos += 1
            while True:
                n = data[pos]
                pos += 1
                if n == 0:
                    break
                pos += n
            info["images"].append(image)
            continue
        raise ValueError(f"{path}: unexpected block 0x{block:02X} at {pos - 1}")
    return info


def _check_gif_fixtures(out_dir: str) -> None:
    def load(name: str) -> dict:
        return _parse_gif(os.path.join(out_dir, name))

    s = load("static_rgb.gif")
    assert s["version"] == "87a" and s["screen"] == (64, 48) and len(s["images"]) == 1, s
    assert not s["images"][0]["interlaced"] and s["images"][0]["lct_depth"] == 0, s
    i = load("static_interlaced.gif")
    assert i["images"][0]["interlaced"] and i["screen"] == (64, 48), i
    t = load("transparent.gif")
    assert t["images"][0]["gce"]["transparent"] is not None and 0xFE in t["extensions"], t
    a = load("animated_3frames.gif")
    assert len(a["images"]) == 3 and all(im["gce"]["disposal"] == 2 for im in a["images"]), a
    assert all((im["left"], im["top"], im["width"], im["height"]) == (0, 0, 32, 32) for im in a["images"]), a
    d = load("animated_disposal_none.gif")
    assert len(d["images"]) == 3 and all(im["gce"]["disposal"] == 1 for im in d["images"]), d
    assert (d["images"][1]["left"], d["images"][1]["top"]) != (0, 0), d
    assert (d["images"][1]["width"], d["images"][1]["height"]) != (48, 32), d
    l = load("local_palette.gif")
    assert len(l["images"]) == 2 and l["images"][1]["lct_depth"] > 0, l
    for name in ("static_rgb.gif", "static_interlaced.gif", "transparent.gif", "animated_3frames.gif",
                 "animated_disposal_none.gif", "local_palette.gif"):
        info = load(name)
        print(f"  {name}: {info['size']} bytes, GIF{info['version']}, screen {info['screen']}, "
              f"gct_depth={info['gct_depth']}, images={[(im['left'], im['top'], im['width'], im['height'], 'I' if im['interlaced'] else '-', im['lct_depth'], im['gce']) for im in info['images']]}")




# ---------------------------------------------------------------------------
# Shared helpers (used by more than one generator)
# ---------------------------------------------------------------------------

def _ensure_dir(path: str) -> None:
    os.makedirs(path, exist_ok=True)


def _write(path: str, data: bytes) -> None:
    with open(path, "wb") as f:
        f.write(data)
    print(f"  {os.path.basename(path):32s} {len(data):7d} bytes")


def _save_png(image: Image.Image, path: str) -> None:
    buffer = io.BytesIO()
    image.save(buffer, "PNG", optimize=True)
    _write(path, buffer.getvalue())


# ---------------------------------------------------------------------------
# JPEG
# ---------------------------------------------------------------------------

def _jpeg_source() -> Image.Image:
    """96x72 RGB test card: smooth gradients plus hard-edged shapes (circle, box, stripe, text-like bars)."""
    w, h = 96, 72
    yy, xx = np.mgrid[0:h, 0:w]
    r = xx * 255 // (w - 1)
    g = yy * 255 // (h - 1)
    b = 255 - (r + g) // 2
    img = np.stack([r, g, b], axis=-1).astype(np.uint8)

    circle = (xx - 30) ** 2 + (yy - 36) ** 2 <= 16 ** 2
    img[circle] = (220, 40, 40)
    img[12:30, 58:90] = (30, 60, 200)
    stripe = np.abs(xx - yy - 40) <= 1
    img[stripe] = (255, 255, 255)
    for i, y0 in enumerate(range(50, 68, 6)):
        img[y0:y0 + 3, 8:88 - i * 12] = (0, 0, 0)
    return Image.fromarray(img)


def _jpeg_parse(data: bytes) -> list[tuple[int, bytes, bytes]]:
    """Splits a single-scan JPEG into (marker, payload, entropy_data) triples; only the SOS entry has entropy data."""
    assert data[:2] == b"\xff\xd8", "not a JPEG"
    pos = 2
    segments: list[tuple[int, bytes, bytes]] = []
    while pos < len(data):
        assert data[pos] == 0xFF, "marker expected"
        marker = data[pos + 1]
        pos += 2
        if marker == 0xD9:
            break
        (length,) = struct.unpack(">H", data[pos:pos + 2])
        payload = data[pos + 2:pos + length]
        pos += length
        if marker == 0xDA:
            end = data.rfind(b"\xff\xd9")
            segments.append((marker, payload, data[pos:end]))
            break
        segments.append((marker, payload, b""))
    return segments


def _jpeg_segment(marker: int, payload: bytes) -> bytes:
    return bytes([0xFF, marker]) + struct.pack(">H", len(payload) + 2) + payload


def _jpeg_ycck(cmyk_stored: np.ndarray, quality: int) -> bytes:
    """Builds an Adobe YCCK (APP14 transform=2) baseline JPEG from inverted (Adobe-convention) CMYK samples.

    Pillow cannot write YCCK, so the four planes (Y, Cb, Cr computed from the inverted CMY channels the way
    libjpeg's cmyk_ycck_convert does, plus K unchanged) are each encoded as a grayscale JPEG with identical
    tables and spliced into one 4-component, non-interleaved (four-scan) sequential JPEG.
    """
    c = 255.0 - cmyk_stored[..., 0]
    m = 255.0 - cmyk_stored[..., 1]
    y = 255.0 - cmyk_stored[..., 2]
    luma = 0.299 * c + 0.587 * m + 0.114 * y
    cb = -0.168736 * c - 0.331264 * m + 0.5 * y + 128.0
    cr = 0.5 * c - 0.418688 * m - 0.081312 * y + 128.0
    planes = [np.clip(np.rint(p), 0, 255).astype(np.uint8) for p in (luma, cb, cr)]
    planes.append(cmyk_stored[..., 3].astype(np.uint8))

    encoded = []
    for plane in planes:
        buffer = io.BytesIO()
        Image.fromarray(plane).save(buffer, "JPEG", quality=quality, subsampling=0)
        encoded.append(_jpeg_parse(buffer.getvalue()))

    def tables(segments, marker):
        return [payload for kind, payload, _ in segments if kind == marker]

    dqt = tables(encoded[0], 0xDB)
    dht = tables(encoded[0], 0xC4)
    for other in encoded[1:]:
        assert tables(other, 0xDB) == dqt and tables(other, 0xC4) == dht, "planes must share tables"

    h, w = planes[0].shape
    sof = bytes([8]) + struct.pack(">HHB", h, w, 4)
    for i in range(4):
        sof += bytes([i + 1, 0x11, 0])

    out = bytearray(b"\xff\xd8")
    out += _jpeg_segment(0xEE, b"Adobe" + struct.pack(">HHHB", 100, 0, 0, 2))
    for payload in dqt:
        out += _jpeg_segment(0xDB, payload)
    out += _jpeg_segment(0xC0, sof)
    for payload in dht:
        out += _jpeg_segment(0xC4, payload)
    for i, segments in enumerate(encoded):
        entropy = [data for kind, _, data in segments if kind == 0xDA][0]
        out += _jpeg_segment(0xDA, bytes([1, i + 1, 0x00, 0, 63, 0]))
        out += entropy
    out += b"\xff\xd9"
    return bytes(out)


def gen_jpeg(out_dir: str) -> None:
    """Baseline/progressive, subsampled, restart-interval, CMYK and YCCK JPEGs plus libjpeg reference decodes."""
    _ensure_dir(out_dir)
    src = _jpeg_source()
    _save_png(src, os.path.join(out_dir, "source.png"))
    quality = 85

    def emit(name: str, data: bytes) -> None:
        _write(os.path.join(out_dir, name + ".jpg"), data)
        # Pillow decodes through libjpeg(-turbo) with its defaults: accurate integer IDCT and fancy upsampling.
        with Image.open(io.BytesIO(data)) as decoded:
            decoded.load()
            _save_png(decoded.convert("RGB"), os.path.join(out_dir, name + ".decoded.png"))

    def encode(image: Image.Image, **kwargs) -> bytes:
        buffer = io.BytesIO()
        image.save(buffer, "JPEG", quality=quality, **kwargs)
        return buffer.getvalue()

    emit("baseline_444", encode(src, subsampling=0))
    emit("baseline_422", encode(src, subsampling=1))
    emit("baseline_420", encode(src, subsampling=2))
    emit("baseline_gray", encode(src.convert("L")))
    emit("progressive_444", encode(src, subsampling=0, progressive=True))
    emit("progressive_422", encode(src, subsampling=1, progressive=True))
    emit("progressive_420", encode(src, subsampling=2, progressive=True))
    emit("progressive_gray", encode(src.convert("L"), progressive=True))

    # Restart intervals: every 3 MCUs (baseline) and every MCU row (progressive).
    emit("restart_baseline_420", encode(src, subsampling=2, restart_marker_blocks=3))
    emit("restart_progressive_420", encode(src, subsampling=2, progressive=True, restart_marker_rows=1))

    # 71x53 crop: with 4:2:0 the luma plane is padded to 10x8 blocks but non-interleaved (AC) scans cover
    # only ceil(71/8) x ceil(53/8) = 9x7 of them, so the two block grids differ in both directions.
    odd = src.crop((10, 7, 81, 60))
    emit("baseline_420_odd", encode(odd, subsampling=2))
    emit("progressive_420_odd", encode(odd, subsampling=2, progressive=True))

    # CMYK with a K gradient. Pillow writes the Adobe APP14 marker (transform 0) and stores the samples
    # inverted, exactly like Adobe applications do.
    rgb = np.asarray(src)
    h, w = rgb.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w]
    k = ((xx + yy) * 120 // (w + h - 2)).astype(np.uint8)
    cmyk = np.dstack([255 - rgb[..., 0], 255 - rgb[..., 1], 255 - rgb[..., 2], k]).astype(np.uint8)
    cmyk_image = Image.merge("CMYK", [Image.fromarray(cmyk[..., i]) for i in range(4)])
    cmyk_jpeg = encode(cmyk_image, subsampling=0)
    assert b"Adobe" in cmyk_jpeg, "Pillow is expected to write an Adobe APP14 marker for CMYK"
    emit("cmyk_adobe", cmyk_jpeg)

    # YCCK: same ink values, stored inverted (Adobe convention) and transformed to YCC + K.
    emit("ycck_adobe", _jpeg_ycck(255 - cmyk, quality))


warnings.filterwarnings("ignore", category=DeprecationWarning)

# --------------------------------------------------------------------------------------------------
# Shared helpers
# --------------------------------------------------------------------------------------------------

def rng(seed: int) -> np.random.Generator:
    return np.random.default_rng(seed)


def gradient_rgb(w: int, h: int, seed: int = 1) -> np.ndarray:
    """H x W x 3 uint8: horizontal red ramp, vertical green ramp, blue = (x*y + noise) % 256."""
    x = np.arange(w)[None, :]
    y = np.arange(h)[:, None]
    r = (x * 255) // max(1, w - 1)
    g = (y * 255) // max(1, h - 1)
    b = (x * y + rng(seed).integers(0, 32, (h, w))) % 256
    return np.stack([np.broadcast_to(r, (h, w)), np.broadcast_to(g, (h, w)), b], axis=-1).astype(np.uint8)


def gradient_gray(w: int, h: int, levels: int = 256, seed: int = 2) -> np.ndarray:
    """H x W ints in [0, levels): diagonal ramp plus a little noise so every level occurs."""
    x = np.arange(w)[None, :]
    y = np.arange(h)[:, None]
    v = (x * 7 + y * 13 + rng(seed).integers(0, 5, (h, w))) % levels
    return v.astype(np.int64)


def noise(shape, seed: int, high: int = 256, dtype=np.uint8) -> np.ndarray:
    return rng(seed).integers(0, high, shape, dtype=np.int64).astype(dtype)


def palette_rgb(n: int, seed: int = 3) -> np.ndarray:
    """n x 3 uint8 palette with distinct, deterministic colours (entry 0 is black)."""
    p = rng(seed).integers(0, 256, (n, 3), dtype=np.int64).astype(np.uint8)
    p[0] = 0
    if n > 1:
        p[1] = 255
    return p


def write_expected(out_dir: str, name: str, frames: list[np.ndarray]) -> None:
    """Writes <name>.rgba (all frames concatenated) and <name>.expected.png (first frame)."""
    for f in frames:
        assert f.dtype == np.uint8 and f.ndim == 3 and f.shape[2] == 4, (name, f.shape, f.dtype)
    with open(os.path.join(out_dir, name + ".rgba"), "wb") as fh:
        for f in frames:
            fh.write(np.ascontiguousarray(f).tobytes())
    Image.fromarray(frames[0], "RGBA").save(os.path.join(out_dir, name + ".expected.png"))


def write_manifest(out_dir: str, entries: list[dict]) -> None:
    with open(os.path.join(out_dir, "manifest.json"), "w", newline="\n") as fh:
        json.dump(entries, fh, indent=1)
        fh.write("\n")


def ensure_dir(path: str) -> str:
    os.makedirs(path, exist_ok=True)
    return path


def rgba_from_rgb(rgb: np.ndarray, alpha: int | np.ndarray = 255) -> np.ndarray:
    a = np.full(rgb.shape[:2], alpha, np.uint8) if np.isscalar(alpha) else alpha.astype(np.uint8)
    return np.dstack([rgb.astype(np.uint8), a])


def rgba_from_gray(gray8: np.ndarray, alpha: int | np.ndarray = 255) -> np.ndarray:
    g = gray8.astype(np.uint8)
    return rgba_from_rgb(np.dstack([g, g, g]), alpha)


def pil_verify(path: str, expected: np.ndarray, what: str, strict: bool = True, atol: int = 0) -> None:
    """Cross-checks a fixture by decoding it with Pillow (an independent decoder). atol allows for
    scaling differences (e.g. Pillow replicates bits when widening 5-bit BMP channels; the library rounds)."""
    try:
        with Image.open(path) as im:
            got = np.array(im.convert("RGBA"))
    except Exception as ex:  # noqa: BLE001
        if strict:
            raise
        print(f"  note: Pillow could not verify {what}: {ex}")
        return
    same = got.shape == expected.shape and (
        np.array_equal(got, expected) if atol == 0 else np.abs(got.astype(int) - expected.astype(int)).max() <= atol)
    if not same:
        if strict:
            diff = np.argwhere(got != expected) if got.shape == expected.shape else None
            raise AssertionError(f"Pillow disagrees with expected pixels for {what}: shape {got.shape} vs "
                                 f"{expected.shape}, first diff {diff[:3] if diff is not None else '?'}")
        print(f"  note: Pillow renders {what} differently (expected for this fixture).")


# --------------------------------------------------------------------------------------------------
# PNG
# --------------------------------------------------------------------------------------------------

PNG_SIG = b"\x89PNG\r\n\x1a\n"
ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]


def png_chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def png_pack_row(samples: np.ndarray, depth: int) -> bytes:
    """Packs one scanline's samples (1-D, in order) at the given bit depth, MSB first."""
    if depth == 8:
        return samples.astype(np.uint8).tobytes()
    if depth == 16:
        return samples.astype(">u2").tobytes()
    bits = np.unpackbits(samples.astype(np.uint8)[:, None], axis=1)[:, 8 - depth:].reshape(-1)
    pad = (-len(bits)) % 8
    return np.packbits(np.concatenate([bits, np.zeros(pad, np.uint8)])).tobytes()


def png_filter_row(ftype: int, row: bytes, prev: bytes | None, bpp: int) -> bytes:
    cur = np.frombuffer(row, np.uint8).astype(np.int32)
    up = np.zeros_like(cur) if prev is None else np.frombuffer(prev, np.uint8).astype(np.int32)
    left = np.concatenate([np.zeros(bpp, np.int32), cur[:-bpp]]) if bpp < len(cur) else np.zeros_like(cur)
    upleft = np.concatenate([np.zeros(bpp, np.int32), up[:-bpp]]) if bpp < len(cur) else np.zeros_like(cur)
    if ftype == 0:
        pred = np.zeros_like(cur)
    elif ftype == 1:
        pred = left
    elif ftype == 2:
        pred = up
    elif ftype == 3:
        pred = (left + up) >> 1
    elif ftype == 4:
        p = left + up - upleft
        pa, pb, pc = np.abs(p - left), np.abs(p - up), np.abs(p - upleft)
        pred = np.where((pa <= pb) & (pa <= pc), left, np.where(pb <= pc, up, upleft))
    else:
        raise ValueError(ftype)
    return bytes([ftype]) + ((cur - pred) & 0xFF).astype(np.uint8).tobytes()


def png_encode(samples: np.ndarray, depth: int, ctype: int, *, palette: np.ndarray | None = None,
               trns: bytes | None = None, interlaced: bool = False, filters="adaptive", idat_split: int = 1,
               before_plte: list[bytes] = (), after_plte: list[bytes] = (), after_idat: list[bytes] = (),
               empty_idat: bool = False, level: int = 9) -> bytes:
    """Hand-assembles a PNG from an H x W x C sample array. filters: int, 'cycle' or 'adaptive'."""
    h, w, c = samples.shape
    bpp = max(1, (depth * c + 7) // 8)
    raw = bytearray()
    passes = ADAM7 if interlaced else [(0, 0, 1, 1)]
    row_index = 0
    for (xs, ys, xstep, ystep) in passes:
        sub = samples[ys::ystep, xs::xstep]
        if sub.shape[0] == 0 or sub.shape[1] == 0:
            continue
        prev = None
        for r in range(sub.shape[0]):
            row = png_pack_row(sub[r].reshape(-1), depth)
            if filters == "adaptive":
                candidates = [png_filter_row(f, row, prev, bpp) for f in range(5)]
                best = min(candidates, key=lambda b: sum(abs(x - 128) for x in b[1:]))
                raw += best
            elif filters == "cycle":
                raw += png_filter_row(row_index % 5, row, prev, bpp)
            else:
                raw += png_filter_row(int(filters), row, prev, bpp)
            prev = row
            row_index += 1

    ihdr = struct.pack(">IIBBBBB", w, h, depth, ctype, 0, 0, 1 if interlaced else 0)
    out = bytearray(PNG_SIG)
    out += png_chunk(b"IHDR", ihdr)
    for ch in before_plte:
        out += ch
    if palette is not None:
        out += png_chunk(b"PLTE", palette.astype(np.uint8).tobytes())
    if trns is not None:
        out += png_chunk(b"tRNS", trns)
    for ch in after_plte:
        out += ch
    comp = zlib.compress(bytes(raw), level)
    if idat_split <= 1:
        parts = [comp]
    else:
        n = idat_split
        cut = [(len(comp) * i) // n for i in range(n + 1)]
        parts = [comp[cut[i]:cut[i + 1]] for i in range(n)]
    for i, part in enumerate(parts):
        if empty_idat and i == 1:
            out += png_chunk(b"IDAT", b"")
        out += png_chunk(b"IDAT", part)
    for ch in after_idat:
        out += ch
    out += png_chunk(b"IEND", b"")
    return bytes(out)


def png_expected(samples: np.ndarray, depth: int, ctype: int, palette=None, trns: bytes | None = None) -> np.ndarray:
    """What the library must produce: 16-bit samples reduced to their high byte, sub-byte gray
    scaled to 0..255, colour-key tRNS compared on the raw sample values before reduction."""
    s = samples.astype(np.int64)
    h, w, _ = s.shape
    a = np.full((h, w), 255, np.uint8)
    if ctype == 0:
        v = s[..., 0]
        v8 = (v >> 8) if depth == 16 else (v * (255 // ((1 << depth) - 1)))
        if trns is not None:
            key = struct.unpack(">H", trns)[0]
            a = np.where(v == key, 0, 255).astype(np.uint8)
        return rgba_from_gray(v8, a)
    if ctype == 2:
        v8 = (s >> 8) if depth == 16 else s
        if trns is not None:
            kr, kg, kb = struct.unpack(">HHH", trns)
            a = np.where((s[..., 0] == kr) & (s[..., 1] == kg) & (s[..., 2] == kb), 0, 255).astype(np.uint8)
        return rgba_from_rgb(v8, a)
    if ctype == 3:
        idx = s[..., 0]
        pal = np.asarray(palette, np.uint8)
        alpha = np.full(len(pal), 255, np.uint8)
        if trns is not None:
            t = np.frombuffer(trns, np.uint8)
            alpha[:len(t)] = t[:len(pal)]
        rgb = pal[idx]
        return rgba_from_rgb(rgb, alpha[idx])
    if ctype == 4:
        v = (s >> 8) if depth == 16 else s
        return rgba_from_gray(v[..., 0], v[..., 1].astype(np.uint8))
    if ctype == 6:
        v = (s >> 8) if depth == 16 else s
        return v.astype(np.uint8)
    raise ValueError(ctype)


def png_ihdr(path: str) -> dict:
    with open(path, "rb") as fh:
        data = fh.read()
    assert data[:8] == PNG_SIG and data[12:16] == b"IHDR"
    w, h, depth, ctype, comp, filt, inter = struct.unpack(">IIBBBBB", data[16:29])
    return {"width": w, "height": h, "bit_depth": depth, "color_type": ctype, "interlaced": bool(inter)}


def gen_png(out_dir: str) -> None:
    out_dir = ensure_dir(out_dir)
    entries: list[dict] = []

    def record(name: str, path: str, expected: np.ndarray, notes: str, writer: str, verify: bool = True):
        info = png_ihdr(path)
        assert info["width"] == expected.shape[1] and info["height"] == expected.shape[0], name
        write_expected(out_dir, name, [expected])
        if verify:
            pil_verify(path, expected, name, strict=True)
        entries.append({"name": name, "file": os.path.basename(path), "width": info["width"],
                        "height": info["height"], "frames": 1, "writer": writer, "notes": notes, **info})

    def hand(name: str, samples: np.ndarray, depth: int, ctype: int, notes: str, palette=None, trns=None,
             verify: bool = True, **kw):
        path = os.path.join(out_dir, name + ".png")
        with open(path, "wb") as fh:
            fh.write(png_encode(samples, depth, ctype, palette=palette, trns=trns, **kw))
        record(name, path, png_expected(samples, depth, ctype, palette, trns), notes, "hand", verify)

    def pillow(name: str, im: Image.Image, expected: np.ndarray, notes: str, verify: bool = True, **save_kw):
        path = os.path.join(out_dir, name + ".png")
        im.save(path, format="PNG", **save_kw)
        record(name, path, expected, notes, "pillow", verify)

    # ---- Pillow-written basics ----
    g = gradient_gray(23, 17)
    pillow("gray8", Image.fromarray(g.astype(np.uint8), "L"), rgba_from_gray(g), "8-bit grayscale, adaptive filters")

    bw = (gradient_gray(21, 13, 2, seed=5) == 1)
    pillow("gray1", Image.fromarray(bw), rgba_from_gray(bw.astype(np.uint8) * 255), "1-bit grayscale (mode '1')")

    g16 = noise((11, 19), seed=6, high=65536, dtype=np.uint16)
    pillow("gray16", Image.fromarray(g16, "I;16"), rgba_from_gray(g16 >> 8), "16-bit grayscale; decoder keeps the high byte",
           verify=False)

    rgb = gradient_rgb(29, 19)
    pillow("rgb8", Image.fromarray(rgb, "RGB"), rgba_from_rgb(rgb), "8-bit truecolor")

    rgb_big = gradient_rgb(97, 61, seed=7)
    pillow("rgb8_97x61", Image.fromarray(rgb_big, "RGB"), rgba_from_rgb(rgb_big), "larger truecolor, adaptive filters")

    rgba = np.dstack([gradient_rgb(17, 23, seed=8), (gradient_gray(17, 23, seed=9)).astype(np.uint8)])
    pillow("rgba8", Image.fromarray(rgba, "RGBA"), rgba, "8-bit truecolor + alpha")

    la = np.dstack([gradient_gray(13, 9, seed=10).astype(np.uint8), noise((9, 13), seed=11)])
    pillow("graya8", Image.fromarray(la, "LA"), rgba_from_gray(la[..., 0], la[..., 1]), "8-bit grayscale + alpha")

    pal256 = palette_rgb(256, seed=12)
    idx8 = noise((15, 21), seed=13)
    im = Image.fromarray(idx8, "P")
    im.putpalette(pal256.reshape(-1).tolist())
    pillow("pal8", im, png_expected(idx8[..., None], 8, 3, pal256), "8-bit palette, 256 entries")

    trns_partial = bytes(rng(14).integers(0, 256, 40, dtype=np.int64).tolist())
    im = Image.fromarray(idx8, "P")
    im.putpalette(pal256.reshape(-1).tolist())
    pillow("pal8_trns", im, png_expected(idx8[..., None], 8, 3, pal256, trns_partial),
           "8-bit palette with a 40-entry tRNS of partial alpha (remaining entries opaque)", transparency=trns_partial)

    for depth in (1, 2, 4):
        n = 1 << depth
        pal = palette_rgb(n, seed=20 + depth)
        idx = gradient_gray(19, 11, n, seed=30 + depth).astype(np.uint8)
        im = Image.fromarray(idx, "P")
        im.putpalette(pal.reshape(-1).tolist())
        pillow(f"pal{depth}", im, png_expected(idx[..., None], depth, 3, pal), f"{depth}-bit palette (Pillow bits={depth})",
               bits=depth)

    thin = gradient_gray(1, 9).astype(np.uint8)
    pillow("gray8_w1", Image.fromarray(thin, "L"), rgba_from_gray(thin), "1 pixel wide")
    wide = gradient_rgb(9, 1)
    pillow("rgb8_h1", Image.fromarray(wide, "RGB"), rgba_from_rgb(wide), "1 pixel high")

    # ---- Hand-written: sub-byte and 16-bit layouts Pillow cannot emit directly ----
    for depth in (2, 4):
        gs = gradient_gray(21, 9, 1 << depth, seed=40 + depth)[..., None]
        hand(f"gray{depth}", gs, depth, 0, f"{depth}-bit grayscale (scaled to 0..255)")

    hand("gray1_hand", (gradient_gray(17, 5, 2, seed=44) == 1).astype(np.uint8)[..., None], 1, 0, "1-bit grayscale, filter 0",
         filters=0)

    rgb16 = noise((9, 13, 3), seed=50, high=65536, dtype=np.uint16)
    hand("rgb16", rgb16, 16, 2, "16-bit truecolor; decoder keeps high bytes", verify=False)

    rgba16 = noise((7, 11, 4), seed=51, high=65536, dtype=np.uint16)
    hand("rgba16", rgba16, 16, 6, "16-bit truecolor + alpha", verify=False)

    la16 = noise((6, 10, 2), seed=52, high=65536, dtype=np.uint16)
    hand("graya16", la16, 16, 4, "16-bit grayscale + alpha", verify=False)

    g16h = noise((8, 8), seed=53, high=65536, dtype=np.uint16)[..., None]
    hand("gray16_hand", g16h, 16, 0, "16-bit grayscale, cycling filters (bpp 2)", filters="cycle", verify=False)

    # ---- Colour-key transparency (tRNS on colour types 0 and 2) ----
    gk = gradient_gray(19, 11).astype(np.uint16)
    gk[2, 3] = 77
    gk[5, 5] = 77
    gk[0, 0] = 77
    hand("gray8_trns_key", gk[..., None], 8, 0, "8-bit gray with tRNS colour key 77; matching pixels are transparent",
         trns=struct.pack(">H", 77))

    gk4 = gradient_gray(15, 7, 16, seed=60).astype(np.uint16)
    gk4[1, 1] = 5
    gk4[3, 9] = 5
    hand("gray4_trns_key", gk4[..., None], 4, 0, "4-bit gray with tRNS colour key 5 (compared against the raw 4-bit sample; "
         "Pillow does not scale sub-byte keys, so it is not used as the oracle here)", trns=struct.pack(">H", 5), verify=False)

    gk16 = noise((9, 9), seed=61, high=65536, dtype=np.uint16)
    gk16[0, 0] = 0x1234   # transparent
    gk16[4, 4] = 0x1234   # transparent
    gk16[2, 2] = 0x1299   # same high byte, different low byte -> must stay opaque
    hand("gray16_trns_key", gk16[..., None], 16, 0,
         "16-bit gray with tRNS key 0x1234; 0x1299 shares the high byte and must stay opaque", trns=struct.pack(">H", 0x1234),
         verify=False)

    rk = gradient_rgb(13, 11, seed=62).astype(np.uint16)
    rk[1, 2] = (10, 20, 30)
    rk[7, 8] = (10, 20, 30)
    rk[9, 9] = (10, 20, 31)   # near miss -> opaque
    hand("rgb8_trns_key", rk, 8, 2, "8-bit RGB with tRNS colour key (10,20,30)", trns=struct.pack(">HHH", 10, 20, 30))

    rk16 = noise((7, 9, 3), seed=63, high=65536, dtype=np.uint16)
    rk16[0, 0] = (0x0102, 0x0304, 0x0506)
    rk16[3, 3] = (0x0102, 0x0304, 0x0506)
    rk16[5, 5] = (0x0102, 0x0304, 0x0507)   # low-byte difference -> opaque
    hand("rgb16_trns_key", rk16, 16, 2, "16-bit RGB with tRNS colour key; low-byte mismatch must stay opaque",
         trns=struct.pack(">HHH", 0x0102, 0x0304, 0x0506), verify=False)

    # ---- Adam7 interlacing (hand-written; filter 0 rows and cycling filters) ----
    hand("gray8_adam7", gradient_gray(20, 15, seed=70)[..., None], 8, 0, "Adam7 interlaced 8-bit gray, filter 0", interlaced=True,
         filters=0)
    hand("rgb8_adam7", gradient_rgb(21, 17, seed=71), 8, 2, "Adam7 interlaced RGB, filter 0", interlaced=True, filters=0)
    hand("rgb8_adam7_filters", gradient_rgb(19, 23, seed=72), 8, 2, "Adam7 interlaced RGB, cycling filters per pass row",
         interlaced=True, filters="cycle")
    rgba7 = np.dstack([gradient_rgb(18, 13, seed=73), noise((13, 18), seed=74)])
    hand("rgba8_adam7", rgba7, 8, 6, "Adam7 interlaced RGBA, adaptive filters", interlaced=True, filters="adaptive")
    pal16 = palette_rgb(16, seed=75)
    hand("pal4_adam7", gradient_gray(22, 14, 16, seed=76).astype(np.uint8)[..., None], 4, 3, "Adam7 interlaced 4-bit palette",
         palette=pal16, interlaced=True, filters=0)
    hand("gray1_adam7", (gradient_gray(21, 19, 2, seed=77) == 1).astype(np.uint8)[..., None], 1, 0,
         "Adam7 interlaced 1-bit gray (sub-byte pass rows)", interlaced=True, filters=0)
    pal4c = palette_rgb(4, seed=78)
    hand("pal2_adam7", gradient_gray(5, 9, 4, seed=79).astype(np.uint8)[..., None], 2, 3, "Adam7 interlaced 2-bit palette 5x9",
         palette=pal4c, interlaced=True, filters="cycle")
    hand("rgb16_adam7", noise((10, 12, 3), seed=80, high=65536, dtype=np.uint16), 16, 2, "Adam7 interlaced 16-bit RGB",
         interlaced=True, filters="cycle", verify=False)
    hand("rgb8_adam7_3x3", gradient_rgb(3, 3, seed=81), 8, 2, "Adam7 with dimensions below 8 (several passes are empty)",
         interlaced=True, filters=0)
    hand("gray8_adam7_1x1", np.array([[[200]]], np.uint8), 8, 0, "Adam7 1x1 (only pass 1 has data)", interlaced=True, filters=0)
    hand("rgba8_adam7_7x5", np.dstack([gradient_rgb(7, 5, seed=82), noise((5, 7), seed=83)]), 8, 6, "Adam7 7x5 RGBA",
         interlaced=True, filters="cycle")
    hand("gray8_adam7_1x9", gradient_gray(1, 9, seed=84)[..., None], 8, 0, "Adam7 1 pixel wide", interlaced=True, filters=0)
    hand("gray8_adam7_9x1", gradient_gray(9, 1, seed=85)[..., None], 8, 0, "Adam7 1 pixel high", interlaced=True, filters=0)

    # ---- Filter types ----
    for f in range(5):
        hand(f"rgb8_filter{f}", gradient_rgb(17, 11, seed=90 + f), 8, 2, f"every row uses filter type {f}", filters=f)
    hand("rgba8_filters_cycle", np.dstack([gradient_rgb(16, 12, seed=95), noise((12, 16), seed=96)]), 8, 6,
         "rows cycle through filter types 0..4 (bpp 4)", filters="cycle")
    hand("pal8_filters_cycle", noise((11, 13), seed=97)[..., None], 8, 3, "palette rows cycle through filters (bpp 1)",
         palette=pal256, filters="cycle")
    hand("gray2_filters_cycle", gradient_gray(29, 8, 4, seed=98)[..., None], 2, 0, "2-bit gray rows cycle through filters",
         filters="cycle")

    # ---- Chunk layout variations ----
    hand("rgb8_idat3", gradient_rgb(24, 20, seed=100), 8, 2, "compressed stream split across 3 IDAT chunks", idat_split=3)
    hand("rgb8_idat_many_empty", gradient_rgb(20, 20, seed=101), 8, 2,
         "compressed stream split across 12 IDAT chunks, one of them zero-length", idat_split=12, empty_idat=True)
    text = png_chunk(b"tEXt", b"Comment\x00EasyImageSharp fixture")
    ztxt = png_chunk(b"zTXt", b"Description\x00\x00" + zlib.compress(b"compressed text chunk"))
    itxt = png_chunk(b"iTXt", b"Title\x00\x00\x00en\x00\x00international text")
    gama = png_chunk(b"gAMA", struct.pack(">I", 45455))
    chrm = png_chunk(b"cHRM", struct.pack(">8I", 31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000))
    srgb = png_chunk(b"sRGB", b"\x00")
    phys = png_chunk(b"pHYs", struct.pack(">IIB", 2835, 2835, 1))
    sbit = png_chunk(b"sBIT", b"\x08\x08\x08")
    bkgd = png_chunk(b"bKGD", struct.pack(">HHH", 255, 255, 255))
    time_ = png_chunk(b"tIME", struct.pack(">HBBBBB", 2024, 1, 2, 3, 4, 5))
    private = png_chunk(b"prVt", b"\x01\x02\x03\x04")
    exif = png_chunk(b"eXIf", b"II*\x00\x08\x00\x00\x00\x00\x00")
    hand("rgb8_ancillary", gradient_rgb(15, 15, seed=102), 8, 2,
         "gAMA/cHRM/sRGB/pHYs/sBIT/tEXt/zTXt/iTXt/bKGD/tIME/eXIf and a private chunk around IDAT; all must be ignored",
         before_plte=[gama, chrm, srgb, phys, sbit, text, exif, private], after_plte=[bkgd, ztxt],
         after_idat=[itxt, time_, text])
    pal_hist = png_chunk(b"hIST", struct.pack(">16H", *range(16)))
    hand("pal4_ancillary", gradient_gray(12, 10, 16, seed=103).astype(np.uint8)[..., None], 4, 3,
         "palette image with hIST/bKGD/tRNS(short) after PLTE and text before it", palette=pal16, trns=bytes([0, 128, 255]),
         before_plte=[text, gama], after_plte=[pal_hist, png_chunk(b"bKGD", b"\x03")], after_idat=[time_])
    hand("pal8_trns_short_hand", noise((9, 9), seed=104, high=32)[..., None], 8, 3, "tRNS with 8 entries for a 32-entry palette",
         palette=palette_rgb(32, seed=105), trns=bytes([0, 32, 64, 96, 128, 160, 192, 224]))
    hand("pal8_trns_full", noise((9, 9), seed=106, high=64)[..., None], 8, 3, "tRNS with one entry per palette entry",
         palette=palette_rgb(64, seed=107), trns=bytes(rng(108).integers(0, 256, 64, dtype=np.int64).tolist()))
    hand("rgb8_1x1", np.array([[[12, 34, 56]]], np.uint8), 8, 2, "single pixel RGB")
    hand("rgb8_suggested_plte", gradient_rgb(11, 7, seed=111), 8, 2, "truecolor image carrying a suggested PLTE (and sPLT); "
         "both must be ignored", palette=palette_rgb(4, seed=112),
         after_plte=[png_chunk(b"sPLT", b"quant\x00\x08" + bytes(range(24)))])
    hand("rgba8_zero_alpha", np.dstack([gradient_rgb(9, 7, seed=109), np.zeros((7, 9), np.uint8)]), 8, 6,
         "fully transparent RGBA; colour must be preserved, not premultiplied away")
    hand("rgb8_level0", gradient_rgb(9, 8, seed=110), 8, 2, "zlib stored blocks (compression level 0)", level=0)

    write_manifest(out_dir, entries)
    print(f"png: {len(entries)} fixtures")


# --------------------------------------------------------------------------------------------------
# BMP
# --------------------------------------------------------------------------------------------------

def bmp_info_header(w: int, h: int, bpp: int, compression: int = 0, size_image: int = 0, colors_used: int = 0,
                    header_size: int = 40, masks: tuple[int, int, int, int] | None = None, important: int = 0) -> bytes:
    hdr = bytearray(struct.pack("<IiiHHIIiiII", header_size, w, h, 1, bpp, compression, size_image, 2835, 2835,
                                colors_used, important))
    if header_size >= 52:
        r, g, b, a = masks or (0, 0, 0, 0)
        hdr += struct.pack("<III", r, g, b)
    if header_size >= 56:
        hdr += struct.pack("<I", (masks or (0, 0, 0, 0))[3])
    if header_size >= 108:
        hdr += b"BGRs"[::-1]                    # bV4CSType = LCS_sRGB ('sRGB' stored little-endian)
        hdr += b"\x00" * 36                     # endpoints
        hdr += struct.pack("<III", 0, 0, 0)     # gamma
    if header_size >= 124:
        hdr += struct.pack("<IIII", 4, 0, 0, 0)  # intent LCS_GM_IMAGES, profile data/size, reserved
    assert len(hdr) == header_size, (len(hdr), header_size)
    return bytes(hdr)


def bmp_file(dib: bytes, palette: bytes, pixels: bytes, *, extra_before_palette: bytes = b"", gap: bytes = b"",
             trailing: bytes = b"") -> bytes:
    offset = 14 + len(dib) + len(extra_before_palette) + len(palette) + len(gap)
    size = offset + len(pixels) + len(trailing)
    return b"BM" + struct.pack("<IHHI", size, 0, 0, offset) + dib + extra_before_palette + palette + gap + pixels + trailing


def bmp_pack_rows(rows: list[bytes], top_down: bool) -> bytes:
    """rows[0] is the top row; pads each to a 4-byte stride and orders bottom-up unless top_down."""
    stride = (len(rows[0]) + 3) & ~3
    padded = [r + b"\x00" * (stride - len(r)) for r in rows]
    if not top_down:
        padded = padded[::-1]
    return b"".join(padded)


def bmp_rows_from_indices(idx: np.ndarray, bpp: int) -> list[bytes]:
    return [png_pack_row(idx[y], bpp) for y in range(idx.shape[0])]


def bmp_palette_bgra(pal: np.ndarray) -> bytes:
    return b"".join(struct.pack("<BBBB", int(b), int(g), int(r), 0) for r, g, b in pal)


def rle8_encode_row(pixels: list[int], use_absolute: bool) -> bytes:
    """Encodes one row with a deterministic mix of encoded runs and absolute-mode segments."""
    out = bytearray()
    i = 0
    n = len(pixels)
    while i < n:
        run = 1
        while i + run < n and pixels[i + run] == pixels[i] and run < 255:
            run += 1
        if run >= 2 or not use_absolute:
            out += bytes([run, pixels[i]])
            i += run
            continue
        # literal segment: extend while no run of 2 starts
        j = i
        while j < n and (j + 1 >= n or pixels[j + 1] != pixels[j]) and j - i < 255:
            j += 1
        seg = pixels[i:j]
        if len(seg) >= 3:
            out += bytes([0, len(seg)]) + bytes(seg)
            if len(seg) % 2:
                out += b"\x00"
        else:
            for p in seg:
                out += bytes([1, p])
        i = j
    return bytes(out)


def rle8_encode(idx: np.ndarray, use_absolute: bool = True) -> bytes:
    """Bottom-up RLE8 stream: rows end with 00 00 and the bitmap with 00 01."""
    out = bytearray()
    for y in range(idx.shape[0] - 1, -1, -1):
        out += rle8_encode_row([int(v) for v in idx[y]], use_absolute)
        out += b"\x00\x00"
    out += b"\x00\x01"
    return bytes(out)


def rle4_encode_row(pixels: list[int], use_absolute: bool) -> bytes:
    out = bytearray()
    i = 0
    n = len(pixels)
    while i < n:
        # a run of alternating pair (a, b, a, b, ...) counts as one encoded run
        a = pixels[i]
        b = pixels[i + 1] if i + 1 < n else a
        run = 1
        while i + run < n and pixels[i + run] == (a if run % 2 == 0 else b) and run < 255:
            run += 1
        if run >= 3 or not use_absolute:
            if run < 2 and use_absolute:
                run = 1
            out += bytes([run, (a << 4) | (b if run > 1 else 0)])
            i += run
            continue
        j = i
        while j < n and j - i < 255:
            # stop the literal segment when a 3-long alternating run begins
            if j + 2 < n and pixels[j + 2] == pixels[j] and (j + 3 >= n or pixels[j + 3] == pixels[j + 1]):
                if j > i:
                    break
            j += 1
        seg = pixels[i:j]
        if len(seg) >= 3:
            packed = png_pack_row(np.array(seg, np.uint8), 4)
            if len(packed) % 2:
                packed += b"\x00"
            out += bytes([0, len(seg)]) + packed
        else:
            for p in seg:
                out += bytes([1, p << 4])
        i = j
    return bytes(out)


def rle4_encode(idx: np.ndarray, use_absolute: bool = True) -> bytes:
    out = bytearray()
    for y in range(idx.shape[0] - 1, -1, -1):
        out += rle4_encode_row([int(v) for v in idx[y]], use_absolute)
        out += b"\x00\x00"
    out += b"\x00\x01"
    return bytes(out)


def gen_bmp(out_dir: str) -> None:
    out_dir = ensure_dir(out_dir)
    entries: list[dict] = []

    def record(name: str, data: bytes, expected: np.ndarray, notes: str, writer: str, verify: bool = True, atol: int = 0,
               **facts):
        path = os.path.join(out_dir, name + ".bmp")
        with open(path, "wb") as fh:
            fh.write(data)
        write_expected(out_dir, name, [expected])
        if verify:
            pil_verify(path, expected, name, strict=True, atol=atol)
        entries.append({"name": name, "file": name + ".bmp", "width": expected.shape[1], "height": expected.shape[0],
                        "frames": 1, "writer": writer, "notes": notes, **facts})

    def pillow(name: str, im: Image.Image, expected: np.ndarray, notes: str, verify: bool = True, **facts):
        buf = io.BytesIO()
        im.save(buf, format="BMP")
        record(name, buf.getvalue(), expected, notes, "pillow", verify, **facts)

    # ---- Pillow-written ----
    rgb = gradient_rgb(23, 17, seed=200)
    pillow("pil_rgb24", Image.fromarray(rgb, "RGB"), rgba_from_rgb(rgb), "24-bit BI_RGB, odd width (row padding)", bpp=24)
    g = gradient_gray(19, 13, seed=201).astype(np.uint8)
    pillow("pil_gray8_pal", Image.fromarray(g, "L"), rgba_from_gray(g), "8-bit with a 256-entry gray palette", bpp=8)
    pal = palette_rgb(256, seed=202)
    idx = noise((14, 21), seed=203)
    im = Image.fromarray(idx, "P")
    im.putpalette(pal.reshape(-1).tolist())
    pillow("pil_pal8", im, rgba_from_rgb(pal[idx]), "8-bit palette", bpp=8)
    bw = gradient_gray(37, 9, 2, seed=204) == 1
    pillow("pil_bw1", Image.fromarray(bw), rgba_from_gray(bw.astype(np.uint8) * 255), "1-bit, 37 pixels wide", bpp=1)
    rgba = np.dstack([gradient_rgb(11, 9, seed=205), noise((9, 11), seed=206)])
    pillow("pil_rgba32_birgb", Image.fromarray(rgba, "RGBA"), rgba_from_rgb(rgba[..., :3]),
           "32-bit BI_RGB written by Pillow with alpha in the reserved byte; per the format the 4th byte is ignored (A=255)",
           verify=False, bpp=32)

    # ---- Hand-written headers/layouts ----
    def rgb_rows(arr: np.ndarray) -> list[bytes]:
        return [arr[y][:, ::-1].astype(np.uint8).tobytes() for y in range(arr.shape[0])]   # BGR

    rgb = gradient_rgb(7, 5, seed=210)
    record("rgb24_topdown", bmp_file(bmp_info_header(7, -5, 24), b"", bmp_pack_rows(rgb_rows(rgb), True)),
           rgba_from_rgb(rgb), "24-bit top-down (negative height), width 7", "hand", bpp=24)
    rgb = gradient_rgb(13, 6, seed=211)
    record("rgb24_gap_and_trailer",
           bmp_file(bmp_info_header(13, 6, 24), b"", bmp_pack_rows(rgb_rows(rgb), False), gap=b"\xEE" * 37, trailing=b"\x11" * 64),
           rgba_from_rgb(rgb), "DataOffset points past 37 junk bytes; 64 trailing bytes after the pixel data", "hand", bpp=24)
    rgb = gradient_rgb(9, 7, seed=212)
    record("rgb24_v5header", bmp_file(bmp_info_header(9, 7, 24, header_size=124), b"", bmp_pack_rows(rgb_rows(rgb), False)),
           rgba_from_rgb(rgb), "24-bit with a 124-byte BITMAPV5HEADER", "hand", bpp=24)

    # 16-bit 555 BI_RGB
    rgb = gradient_rgb(11, 8, seed=213)
    r5, g5, b5 = rgb[..., 0] >> 3, rgb[..., 1] >> 3, rgb[..., 2] >> 3
    px = ((r5.astype(np.uint32) << 10) | (g5.astype(np.uint32) << 5) | b5).astype("<u2")
    rows = [px[y].tobytes() for y in range(8)]
    exp = np.dstack([(c.astype(np.uint32) * 255 + 15) // 31 for c in (r5, g5, b5)]).astype(np.uint8)
    record("rgb16_555", bmp_file(bmp_info_header(11, 8, 16), b"", bmp_pack_rows(rows, False)), rgba_from_rgb(exp),
           "16-bit BI_RGB (implicit 5-5-5 masks); channels scaled to 0..255 with rounding", "hand", atol=2, bpp=16)

    # 16-bit 565 BI_BITFIELDS with masks after the 40-byte header
    r5, g6, b5 = rgb[..., 0] >> 3, rgb[..., 1] >> 2, rgb[..., 2] >> 3
    px = ((r5.astype(np.uint32) << 11) | (g6.astype(np.uint32) << 5) | b5).astype("<u2")
    rows = [px[y].tobytes() for y in range(8)]
    exp = np.dstack([(r5.astype(np.uint32) * 255 + 15) // 31, (g6.astype(np.uint32) * 255 + 31) // 63,
                     (b5.astype(np.uint32) * 255 + 15) // 31]).astype(np.uint8)
    masks = struct.pack("<III", 0xF800, 0x07E0, 0x001F)
    record("rgb16_565_bitfields40", bmp_file(bmp_info_header(11, 8, 16, compression=3), b"", bmp_pack_rows(rows, False),
                                             extra_before_palette=masks),
           rgba_from_rgb(exp), "16-bit BI_BITFIELDS 5-6-5, masks stored after the 40-byte header", "hand", atol=2, bpp=16)

    # 16-bit 4444 with alpha, 56-byte header
    rgba = np.dstack([gradient_rgb(10, 6, seed=214), noise((6, 10), seed=215)])
    q = (rgba >> 4).astype(np.uint32)
    px = ((q[..., 3] << 12) | (q[..., 0] << 8) | (q[..., 1] << 4) | q[..., 2]).astype("<u2")
    rows = [px[y].tobytes() for y in range(6)]
    exp = ((q * 255 + 7) // 15).astype(np.uint8)
    record("rgba16_4444_v3header",
           bmp_file(bmp_info_header(10, 6, 16, compression=3, header_size=56, masks=(0x0F00, 0x00F0, 0x000F, 0xF000)), b"",
                    bmp_pack_rows(rows, False)),
           exp, "16-bit BI_BITFIELDS 4-4-4-4 with alpha mask in a 56-byte header", "hand", verify=False, bpp=16)

    # 32-bit BI_RGB (XRGB)
    rgb = gradient_rgb(6, 9, seed=216)
    px = np.dstack([rgb[..., 2], rgb[..., 1], rgb[..., 0], np.full(rgb.shape[:2], 0x7F, np.uint8)])
    rows = [px[y].tobytes() for y in range(9)]
    record("rgb32_birgb", bmp_file(bmp_info_header(6, 9, 32), b"", bmp_pack_rows(rows, False)), rgba_from_rgb(rgb),
           "32-bit BI_RGB; the 4th byte (0x7F here) is reserved and must be ignored", "hand", verify=False, bpp=32)

    # 32-bit BI_BITFIELDS with alpha, 108-byte V4 header
    rgba = np.dstack([gradient_rgb(9, 9, seed=217), noise((9, 9), seed=218)])
    px = np.dstack([rgba[..., 2], rgba[..., 1], rgba[..., 0], rgba[..., 3]])
    rows = [px[y].tobytes() for y in range(9)]
    record("rgba32_bitfields_v4",
           bmp_file(bmp_info_header(9, 9, 32, compression=3, header_size=108,
                                    masks=(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)), b"", bmp_pack_rows(rows, False)),
           rgba, "32-bit BI_BITFIELDS BGRA with alpha, 108-byte BITMAPV4HEADER", "hand", bpp=32)

    # 32-bit V5 header, RGBA channel order (R in the low byte)
    px = np.dstack([rgba[..., 0], rgba[..., 1], rgba[..., 2], rgba[..., 3]])
    rows = [px[y].tobytes() for y in range(9)]
    record("rgba32_bitfields_v5_rgba_order",
           bmp_file(bmp_info_header(9, 9, 32, compression=3, header_size=124,
                                    masks=(0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000)), b"", bmp_pack_rows(rows, False)),
           rgba, "32-bit BI_BITFIELDS with R in the low byte (non-BGRA mask order), 124-byte header", "hand", bpp=32)

    # 32-bit 10-10-10 bitfields, masks after 40-byte header
    rgb = gradient_rgb(8, 7, seed=219)
    v10 = (rgb.astype(np.uint32) * 1023 + 127) // 255
    px = ((v10[..., 0] << 20) | (v10[..., 1] << 10) | v10[..., 2]).astype("<u4")
    rows = [px[y].tobytes() for y in range(7)]
    exp = ((v10 * 255 + 511) // 1023).astype(np.uint8)
    masks = struct.pack("<III", 0x3FF00000, 0x000FFC00, 0x000003FF)
    record("rgb32_101010_bitfields40", bmp_file(bmp_info_header(8, 7, 32, compression=3), b"", bmp_pack_rows(rows, False),
                                                extra_before_palette=masks),
           rgba_from_rgb(exp), "32-bit BI_BITFIELDS 10-10-10 (no alpha), masks after 40-byte header", "hand", verify=False,
           bpp=32)

    # Palette depths
    pal16 = palette_rgb(16, seed=220)
    idx4 = gradient_gray(13, 7, 16, seed=221).astype(np.uint8)
    record("pal4", bmp_file(bmp_info_header(13, 7, 4), bmp_palette_bgra(pal16), bmp_pack_rows(bmp_rows_from_indices(idx4, 4), False)),
           rgba_from_rgb(pal16[idx4]), "4-bit palette, 16 entries, width 13", "hand", bpp=4)
    pal5 = palette_rgb(5, seed=222)
    idx5 = gradient_gray(9, 6, 5, seed=223).astype(np.uint8)
    record("pal4_colorsused5", bmp_file(bmp_info_header(9, 6, 4, colors_used=5), bmp_palette_bgra(pal5),
                                        bmp_pack_rows(bmp_rows_from_indices(idx5, 4), False)),
           rgba_from_rgb(pal5[idx5]), "4-bit palette with biClrUsed=5 (only 5 palette entries stored)", "hand", bpp=4)
    pal2 = palette_rgb(2, seed=224)
    idx1 = (gradient_gray(29, 5, 2, seed=225) == 1).astype(np.uint8)
    record("pal1_topdown", bmp_file(bmp_info_header(29, -5, 1), bmp_palette_bgra(pal2),
                                    bmp_pack_rows(bmp_rows_from_indices(idx1, 1), True)),
           rgba_from_rgb(pal2[idx1]), "1-bit palette, top-down, width 29", "hand", bpp=1)
    pal256 = palette_rgb(256, seed=226)
    idx8 = noise((7, 10), seed=227)
    record("pal8_topdown_v4", bmp_file(bmp_info_header(10, -7, 8, header_size=108), bmp_palette_bgra(pal256),
                                       bmp_pack_rows(bmp_rows_from_indices(idx8, 8), True)),
           rgba_from_rgb(pal256[idx8]), "8-bit palette, top-down, palette after a 108-byte header", "hand", bpp=8)

    # RLE8
    idx = gradient_gray(21, 9, 12, seed=230).astype(np.uint8)
    idx[2, 3:12] = 7                    # long run
    idx[5, :] = 3
    palr = palette_rgb(256, seed=231)
    record("rle8_runs", bmp_file(bmp_info_header(21, 9, 8, compression=1), bmp_palette_bgra(palr), rle8_encode(idx, False)),
           rgba_from_rgb(palr[idx]), "RLE8 using only encoded runs, end-of-line and end-of-bitmap escapes", "hand", bpp=8,
           compression=1)
    idxa = noise((8, 19), seed=232, high=200)
    idxa[3, 4:9] = 100
    record("rle8_absolute", bmp_file(bmp_info_header(19, 8, 8, compression=1), bmp_palette_bgra(palr), rle8_encode(idxa, True)),
           rgba_from_rgb(palr[idxa]), "RLE8 mixing absolute-mode segments (with word padding) and encoded runs", "hand", bpp=8,
           compression=1)
    # RLE8 with deltas and an early end-of-bitmap: skipped pixels resolve to palette entry 0.
    w, h = 10, 6
    exp_idx = np.zeros((h, w), np.uint8)
    stream = bytearray()
    # bottom row (y=5): 3 pixels of 5, delta (+4, 0), 2 pixels of 6, EOL
    stream += bytes([3, 5, 0, 2, 4, 0, 2, 6, 0, 0])
    exp_idx[5, 0:3] = 5
    exp_idx[5, 7:9] = 6
    # row y=4: absolute 4 pixels [1,2,3,4], then delta (+0,+2) -> jumps to row y=2, x=4
    stream += bytes([0, 4, 1, 2, 3, 4, 0, 2, 0, 2])
    exp_idx[4, 0:4] = [1, 2, 3, 4]
    # now at (x=4, y=2): 5 pixels of 9, EOL -> row y=1
    stream += bytes([5, 9, 0, 0])
    exp_idx[2, 4:9] = 9
    # row y=1: 10 pixels of 8, then end of bitmap (row 0 never written)
    stream += bytes([10, 8, 0, 1])
    exp_idx[1, :] = 8
    record("rle8_delta_eob", bmp_file(bmp_info_header(w, h, 8, compression=1), bmp_palette_bgra(palr), bytes(stream)),
           rgba_from_rgb(palr[exp_idx]), "RLE8 with delta escapes and early end-of-bitmap; unwritten pixels use palette[0]",
           "hand", verify=False, bpp=8, compression=1)

    # RLE4
    idx4 = gradient_gray(15, 7, 16, seed=233).astype(np.uint8)
    idx4[1, 2:11] = 0xA          # long run
    idx4[3, 0:6] = [1, 2, 1, 2, 1, 2]  # alternating pair run
    record("rle4_runs", bmp_file(bmp_info_header(15, 7, 4, compression=2), bmp_palette_bgra(pal16), rle4_encode(idx4, False)),
           rgba_from_rgb(pal16[idx4]), "RLE4 using only encoded runs (alternating nibble pairs)", "hand", bpp=4, compression=2)
    idx4a = noise((6, 17), seed=234, high=16)
    idx4a[2, 3:9] = 0xC
    record("rle4_absolute", bmp_file(bmp_info_header(17, 6, 4, compression=2), bmp_palette_bgra(pal16), rle4_encode(idx4a, True)),
           rgba_from_rgb(pal16[idx4a]), "RLE4 mixing absolute-mode segments (odd counts, word padding) and encoded runs; "
           "not cross-checked with Pillow, whose RLE4 reader drops the last nibble of odd-count absolute runs", "hand",
           verify=False, bpp=4, compression=2)
    w, h = 9, 4
    exp_idx = np.zeros((h, w), np.uint8)
    stream = bytearray()
    stream += bytes([5, 0x34, 0, 2, 2, 1, 0, 0])   # y=3: 5 px 3,4,3,4,3 ; delta (+2,+1) -> y=2 x=7 ; EOL -> y=1
    exp_idx[3, 0:5] = [3, 4, 3, 4, 3]
    stream += bytes([0, 3, 0x12, 0x30, 4, 0x55, 0, 1])   # y=1: absolute 3 px [1,2,3] (padded), 4 px of 5, EOB
    exp_idx[1, 0:3] = [1, 2, 3]
    exp_idx[1, 3:7] = 5
    record("rle4_delta_eob", bmp_file(bmp_info_header(w, h, 4, compression=2), bmp_palette_bgra(pal16), bytes(stream)),
           rgba_from_rgb(pal16[exp_idx]), "RLE4 with a delta escape and early end-of-bitmap; unwritten pixels use palette[0]",
           "hand", verify=False, bpp=4, compression=2)

    # OS/2 BITMAPCOREHEADER (12 bytes, RGB-triple palette, unsigned dimensions)
    def core_header(w, h, bpp):
        return struct.pack("<IHHHH", 12, w, h, 1, bpp)

    rgb = gradient_rgb(7, 6, seed=240)
    record("os2_core24", bmp_file(core_header(7, 6, 24), b"", bmp_pack_rows(rgb_rows(rgb), False)), rgba_from_rgb(rgb),
           "OS/2 BITMAPCOREHEADER 24-bit", "hand", bpp=24, header_size=12)
    pal = palette_rgb(256, seed=241)
    idx = noise((5, 11), seed=242)
    pal_triples = b"".join(struct.pack("<BBB", int(b), int(g), int(r)) for r, g, b in pal)
    record("os2_core8", bmp_file(core_header(11, 5, 8), pal_triples, bmp_pack_rows(bmp_rows_from_indices(idx, 8), False)),
           rgba_from_rgb(pal[idx]), "OS/2 BITMAPCOREHEADER 8-bit with 3-byte palette entries", "hand", bpp=8, header_size=12)
    pal2 = palette_rgb(2, seed=243)
    idx1 = (gradient_gray(19, 4, 2, seed=244) == 1).astype(np.uint8)
    pal_triples = b"".join(struct.pack("<BBB", int(b), int(g), int(r)) for r, g, b in pal2)
    record("os2_core1", bmp_file(core_header(19, 4, 1), pal_triples, bmp_pack_rows(bmp_rows_from_indices(idx1, 1), False)),
           rgba_from_rgb(pal2[idx1]), "OS/2 BITMAPCOREHEADER 1-bit", "hand", bpp=1, header_size=12)
    pal16 = palette_rgb(16, seed=245)
    idx4 = gradient_gray(6, 6, 16, seed=246).astype(np.uint8)
    pal_triples = b"".join(struct.pack("<BBB", int(b), int(g), int(r)) for r, g, b in pal16)
    record("os2_core4", bmp_file(core_header(6, 6, 4), pal_triples, bmp_pack_rows(bmp_rows_from_indices(idx4, 4), False)),
           rgba_from_rgb(pal16[idx4]), "OS/2 BITMAPCOREHEADER 4-bit", "hand", bpp=4, header_size=12)

    # 1x1 and tiny
    record("rgb24_1x1", bmp_file(bmp_info_header(1, 1, 24), b"", bmp_pack_rows([bytes([3, 2, 1])], False)),
           np.array([[[1, 2, 3, 255]]], np.uint8), "single pixel 24-bit (row padded to 4 bytes)", "hand", bpp=24)

    write_manifest(out_dir, entries)
    print(f"bmp: {len(entries)} fixtures")


# --------------------------------------------------------------------------------------------------
# TIFF
# --------------------------------------------------------------------------------------------------

TIFF_TYPE_SIZE = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8}
PREDICTOR_CODECS = (5, 8, 32946)   # libtiff only wires the predictor into LZW/Deflate; PackBits/raw ignore tag 317
TIFF_TYPE_FMT = {1: "B", 3: "H", 4: "I"}


def tiff_lzw_encode(data: bytes) -> bytes:
    """TIFF-flavour LZW (MSB-first, early change), mirroring what libtiff/GDI+ emit."""
    out = bytearray()
    bit_buffer = 0
    bit_count = 0
    code_size = 9
    table: dict[tuple[int, int], int] = {}
    next_code = 258

    def emit(code: int) -> None:
        nonlocal bit_buffer, bit_count
        bit_buffer = (bit_buffer << code_size) | code
        bit_count += code_size
        while bit_count >= 8:
            out.append((bit_buffer >> (bit_count - 8)) & 0xFF)
            bit_count -= 8
        bit_buffer &= (1 << bit_count) - 1

    emit(256)
    prefix = -1
    for b in data:
        if prefix == -1:
            prefix = b
            continue
        key = (prefix, b)
        if key in table:
            prefix = table[key]
            continue
        emit(prefix)
        table[key] = next_code
        next_code += 1
        if next_code == (1 << code_size) and code_size < 12:
            code_size += 1
        if next_code >= 4094:
            emit(256)
            table.clear()
            code_size = 9
            next_code = 258
        prefix = b
    if prefix != -1:
        emit(prefix)
    emit(257)
    if bit_count > 0:
        out.append((bit_buffer << (8 - bit_count)) & 0xFF)
    return bytes(out)


def packbits_encode(data: bytes) -> bytes:
    out = bytearray()
    i = 0
    n = len(data)
    while i < n:
        run = 1
        while i + run < n and data[i + run] == data[i] and run < 128:
            run += 1
        if run >= 2:
            out += bytes([(257 - run) & 0xFF, data[i]])
            i += run
            continue
        j = i
        while j < n and j - i < 128 and (j + 1 >= n or data[j + 1] != data[j]):
            j += 1
        out += bytes([j - i - 1]) + data[i:j]
        i = j
    return bytes(out)


def tiff_predictor2(raw: bytes, width: int, spp: int, bits: int, big_endian: bool) -> bytes:
    """Applies horizontal differencing to interleaved rows of width*spp samples."""
    dt = np.dtype((">u2" if big_endian else "<u2") if bits == 16 else "u1")
    arr = np.frombuffer(raw, dt).reshape(-1, width, spp).astype(np.int64)
    diff = arr.copy()
    diff[:, 1:, :] = arr[:, 1:, :] - arr[:, :-1, :]
    return (diff & ((1 << bits) - 1)).astype(dt).tobytes()


class TiffPage:
    """One IFD: tags plus the strip/tile payloads referenced by it."""

    def __init__(self, width: int, height: int, tags: dict[int, tuple[int, list[int]]], strips: list[bytes] | None = None,
                 tiles: list[bytes] | None = None, ascii_tags: dict[int, bytes] | None = None):
        self.width = width
        self.height = height
        self.tags = dict(tags)
        self.strips = strips or []
        self.tiles = tiles or []
        self.ascii = ascii_tags or {}


def tiff_write(pages: list[TiffPage], big_endian: bool = False, ifd_first: bool = False,
               strip_bytecounts: bool = True) -> bytes:
    e = ">" if big_endian else "<"
    out = bytearray(b"MM\x00\x2A" if big_endian else b"II\x2A\x00")
    out += b"\x00\x00\x00\x00"           # first IFD offset (patched below)
    ifd_offsets = []
    for page in pages:
        payloads = page.strips or page.tiles
        offsets = []
        counts = []
        if not ifd_first:
            for blob in payloads:
                if len(out) % 2:
                    out += b"\x00"
                offsets.append(len(out))
                counts.append(len(blob))
                out += blob
        tags = dict(page.tags)
        tags[256] = (4, [page.width])
        tags[257] = (4, [page.height])
        if page.strips:
            tags[273] = (4, offsets if not ifd_first else [0] * len(payloads))
            if strip_bytecounts:
                tags[279] = (4, counts if not ifd_first else [0] * len(payloads))
        elif page.tiles:
            tags[324] = (4, offsets if not ifd_first else [0] * len(payloads))
            tags[325] = (4, counts if not ifd_first else [0] * len(payloads))
        for tag, text in page.ascii.items():
            tags[tag] = (2, list(text + b"\x00"))
        # IFD
        if len(out) % 2:
            out += b"\x00"
        ifd_offset = len(out)
        ifd_offsets.append(ifd_offset)
        keys = sorted(tags)
        n = len(keys)
        ifd = bytearray(struct.pack(e + "H", n))
        ext = bytearray()
        ext_base = ifd_offset + 2 + n * 12 + 4
        patch = {}
        for tag in keys:
            typ, values = tags[tag]
            size = TIFF_TYPE_SIZE[typ]
            if typ == 5:
                packed = b"".join(struct.pack(e + "II", v[0], v[1]) for v in values)
                count = len(values)
            else:
                packed = struct.pack(e + TIFF_TYPE_FMT[typ] * len(values), *values)
                count = len(values)
            if len(packed) <= 4:
                value_field = packed + b"\x00" * (4 - len(packed))
                if tag in (273, 324) and ifd_first:
                    patch[tag] = ("inline", ifd_offset + 2 + keys.index(tag) * 12 + 8)
            else:
                if len(ext) % 2:
                    ext += b"\x00"
                if tag in (273, 279, 324, 325) and ifd_first:
                    patch[tag] = ("ext", ext_base + len(ext))
                value_field = struct.pack(e + "I", ext_base + len(ext))
                ext += packed
            ifd += struct.pack(e + "HHI", tag, typ, count) + value_field
        ifd += b"\x00\x00\x00\x00"       # next IFD (patched)
        out += ifd + ext
        if ifd_first:
            for blob in payloads:
                if len(out) % 2:
                    out += b"\x00"
                offsets.append(len(out))
                counts.append(len(blob))
                out += blob
            for tag, (where, pos) in patch.items():
                vals = offsets if tag in (273, 324) else counts
                packed = struct.pack(e + "I" * len(vals), *vals)
                out[pos:pos + len(packed)] = packed
    # link IFDs
    struct.pack_into(e + "I", out, 4, ifd_offsets[0])
    for i in range(len(pages) - 1):
        n = struct.unpack_from(e + "H", out, ifd_offsets[i])[0]
        struct.pack_into(e + "I", out, ifd_offsets[i] + 2 + n * 12, ifd_offsets[i + 1])
    return bytes(out)


def tiff_probe(data: bytes) -> tuple[int, int, int]:
    """Minimal IFD walk: (width, height) of the first page and the page count."""
    e = ">" if data[:2] == b"MM" else "<"
    offset = struct.unpack_from(e + "I", data, 4)[0]
    width = height = 0
    pages = 0
    seen = set()
    while offset and offset not in seen and offset + 2 <= len(data):
        seen.add(offset)
        n = struct.unpack_from(e + "H", data, offset)[0]
        if pages == 0:
            for i in range(n):
                tag, typ, count = struct.unpack_from(e + "HHI", data, offset + 2 + i * 12)
                value = struct.unpack_from(e + ("H" if typ == 3 else "I"), data, offset + 2 + i * 12 + 8)[0]
                if tag == 256:
                    width = value
                elif tag == 257:
                    height = value
        pages += 1
        offset = struct.unpack_from(e + "I", data, offset + 2 + n * 12)[0]
    return width, height, pages


def tiff_strips(raw_rows: list[bytes], rows_per_strip: int, compress) -> list[bytes]:
    strips = []
    for s in range(0, len(raw_rows), rows_per_strip):
        strips.append(compress(b"".join(raw_rows[s:s + rows_per_strip])))
    return strips


def gen_tiff(out_dir: str) -> None:
    out_dir = ensure_dir(out_dir)
    entries: list[dict] = []

    def record(name: str, data: bytes, frames: list[np.ndarray] | None, notes: str, writer: str, verify=True,
               expect: str | None = None, **facts):
        path = os.path.join(out_dir, name + ".tif")
        with open(path, "wb") as fh:
            fh.write(data)
        width, height, n_frames = tiff_probe(data)
        entry = {"name": name, "file": name + ".tif", "width": width, "height": height, "frames": n_frames,
                 "writer": writer, "notes": notes, **facts}
        if expect is not None:
            entry["expect"] = expect
        else:
            assert frames is not None
            assert len(frames) == n_frames, (name, len(frames), n_frames)
            assert frames[0].shape[1] == width and frames[0].shape[0] == height, name
            write_expected(out_dir, name, frames)
            entry["frame_sizes"] = [[f.shape[1], f.shape[0]] for f in frames]
            if verify:
                pil_verify(path, frames[0], name, strict=(verify is True))
        entries.append(entry)

    def pillow(name: str, im: Image.Image, frames, notes: str, verify=True, expect=None, **save_kw):
        buf = io.BytesIO()
        im.save(buf, format="TIFF", **save_kw)
        data = buf.getvalue()
        buf.seek(0)
        with Image.open(buf) as check:
            facts = {"compression": check.tag_v2.get(259), "photometric": check.tag_v2.get(262),
                     "bits_per_sample": list(check.tag_v2.get(258, (1,))), "predictor": check.tag_v2.get(317, 1),
                     "rows_per_strip": check.tag_v2.get(278)}
        record(name, data, frames, notes, "pillow", verify, expect, **facts)

    # ---- Pillow (libtiff) written ----
    g = gradient_gray(23, 17, seed=300).astype(np.uint8)
    for comp, label in (("raw", "raw"), ("tiff_lzw", "lzw"), ("packbits", "packbits"), ("tiff_adobe_deflate", "deflate")):
        pillow(f"pil_gray8_{label}", Image.fromarray(g, "L"), [rgba_from_gray(g)], f"8-bit gray, compression {label}",
               compression=comp)
        rgb = gradient_rgb(19, 13, seed=301)
        pillow(f"pil_rgb8_{label}", Image.fromarray(rgb, "RGB"), [rgba_from_rgb(rgb)], f"8-bit RGB, compression {label}",
               compression=comp)
    rgba = np.dstack([gradient_rgb(13, 11, seed=302), noise((11, 13), seed=303)])
    pillow("pil_rgba8_raw", Image.fromarray(rgba, "RGBA"), [rgba], "8-bit RGBA (ExtraSamples unassociated)", compression="raw")
    pillow("pil_rgba8_lzw", Image.fromarray(rgba, "RGBA"), [rgba], "8-bit RGBA, LZW", compression="tiff_lzw")
    pal = palette_rgb(256, seed=304)
    idx = noise((12, 15), seed=305)
    im = Image.fromarray(idx, "P")
    im.putpalette(pal.reshape(-1).tolist())
    pillow("pil_pal8_raw", im, [rgba_from_rgb(pal[idx])], "8-bit palette (ColorMap 16-bit entries)", compression="raw")
    pillow("pil_pal8_lzw", im, [rgba_from_rgb(pal[idx])], "8-bit palette, LZW", compression="tiff_lzw")
    bw = gradient_gray(37, 11, 2, seed=306) == 1
    bwim = Image.fromarray(bw)
    for comp, label in (("raw", "raw"), ("packbits", "packbits"), ("tiff_lzw", "lzw"), ("tiff_adobe_deflate", "deflate")):
        pillow(f"pil_bilevel_{label}", bwim, [rgba_from_gray(bw.astype(np.uint8) * 255)],
               f"1-bit bilevel min-is-black (BitsPerSample tag absent), width 37, compression {label}", compression=comp)
    pillow("pil_bilevel_group4", bwim, [rgba_from_gray(bw.astype(np.uint8) * 255)],
           "1-bit bilevel min-is-black, CCITT Group 4 (ITU-T T.6), width 37", compression="group4")
    pillow("pil_bilevel_group3", bwim, [rgba_from_gray(bw.astype(np.uint8) * 255)],
           "1-bit bilevel min-is-black, CCITT Group 3 (ITU-T T.4) one-dimensional, width 37", compression="group3")
    rgbp = gradient_rgb(21, 14, seed=307)
    pillow("pil_rgb8_lzw_pred2", Image.fromarray(rgbp, "RGB"), [rgba_from_rgb(rgbp)], "RGB LZW with horizontal predictor 2",
           compression="tiff_lzw", tiffinfo={317: 2})
    pillow("pil_gray8_deflate_pred2", Image.fromarray(g, "L"), [rgba_from_gray(g)], "gray deflate with predictor 2",
           compression="tiff_adobe_deflate", tiffinfo={317: 2})
    pillow("pil_gray8_rps4", Image.fromarray(g, "L"), [rgba_from_gray(g)], "RowsPerStrip 4 (5 strips for 17 rows)",
           compression="raw", tiffinfo={278: 4})
    pillow("pil_rgb8_lzw_rps3", Image.fromarray(rgbp, "RGB"), [rgba_from_rgb(rgbp)], "LZW with RowsPerStrip 3",
           compression="tiff_lzw", tiffinfo={278: 3})
    g16 = noise((9, 14), seed=308, high=65536, dtype=np.uint16)
    pillow("pil_gray16_raw", Image.fromarray(g16, "I;16"), [rgba_from_gray(g16 >> 8)], "16-bit gray, high byte kept",
           verify=False, compression="raw")
    pillow("pil_gray16_lzw", Image.fromarray(g16, "I;16"), [rgba_from_gray(g16 >> 8)], "16-bit gray LZW", verify=False,
           compression="tiff_lzw")
    pillow("pil_gray16_lzw_pred2", Image.fromarray(g16, "I;16"), [rgba_from_gray(g16 >> 8)], "16-bit gray LZW predictor 2",
           verify=False, compression="tiff_lzw", tiffinfo={317: 2})
    la = np.dstack([gradient_gray(11, 8, seed=309).astype(np.uint8), noise((8, 11), seed=310)])
    pillow("pil_graya8_raw", Image.fromarray(la, "LA"), [rgba_from_gray(la[..., 0], la[..., 1])], "8-bit gray + alpha (spp 2)",
           compression="raw")
    noise_g = noise((64, 64), seed=311)
    pillow("pil_gray8_lzw_noise64", Image.fromarray(noise_g, "L"), [rgba_from_gray(noise_g)],
           "incompressible 64x64 noise: LZW table fills, widens to 12 bits and resets", compression="tiff_lzw")
    noise_rgb = noise((40, 60, 3), seed=312)
    pillow("pil_rgb8_lzw_noise", Image.fromarray(noise_rgb, "RGB"), [rgba_from_rgb(noise_rgb)],
           "incompressible RGB noise, LZW with predictor 2", compression="tiff_lzw", tiffinfo={317: 2})
    pages = [Image.fromarray(gradient_gray(12, 8, seed=313).astype(np.uint8), "L"),
             Image.fromarray(gradient_rgb(10, 6, seed=314), "RGB"),
             Image.fromarray(gradient_gray(9, 7, 2, seed=315) == 1)]
    exp_pages = [rgba_from_gray(np.array(pages[0])), rgba_from_rgb(np.array(pages[1])),
                 rgba_from_gray(np.array(pages[2]).astype(np.uint8) * 255)]
    pillow("pil_multipage3", pages[0], exp_pages, "3 pages of different sizes and modes (gray8, rgb8, bilevel)",
           save_all=True, append_images=pages[1:], compression="tiff_lzw")
    f32 = rng(317).random((8, 8)).astype(np.float32)
    f32_gray = np.clip(np.rint(f32 * np.float32(255)), 0, 255).astype(np.uint8)
    pillow("pil_float32", Image.fromarray(f32, "F"), [rgba_from_gray(f32_gray)],
           "32-bit float samples (SampleFormat 3) scaled from 0..1", verify="soft", compression="raw")
    i32 = np.arange(64, dtype=np.int32).reshape(8, 8)
    i32_gray = ((i32.astype(np.int64) ^ -0x80000000) >> 24).astype(np.uint8)
    pillow("pil_int32", Image.fromarray(i32, "I"), [rgba_from_gray(i32_gray)],
           "32-bit signed samples (SampleFormat 2) shifted into the unsigned range", verify="soft", compression="raw")

    # ---- Hand-written ----
    def raw_rows(arr: np.ndarray, bits: int, big_endian: bool) -> list[bytes]:
        h = arr.shape[0]
        if bits == 16:
            dt = ">u2" if big_endian else "<u2"
            return [arr[y].reshape(-1).astype(dt).tobytes() for y in range(h)]
        return [png_pack_row(arr[y].reshape(-1), bits) for y in range(h)]

    def base_tags(bits: int, spp: int, photometric: int, compression: int = 1, rows_per_strip: int | None = None,
                  predictor: int = 1, extra: dict | None = None) -> dict:
        tags = {258: (3, [bits] * spp), 259: (3, [compression]), 262: (3, [photometric]), 277: (3, [spp]),
                284: (3, [1])}
        if rows_per_strip is not None:
            tags[278] = (4, [rows_per_strip])
        if predictor != 1:
            tags[317] = (3, [predictor])
        if extra:
            tags.update(extra)
        return tags

    def compressor(compression: int):
        return {1: (lambda b: b), 5: tiff_lzw_encode, 32773: packbits_encode, 8: (lambda b: zlib.compress(b, 9))}[compression]

    def hand(name: str, arr: np.ndarray, bits: int, spp: int, photometric: int, expected, notes: str, *, compression=1,
             rows_per_strip=None, predictor=1, big_endian=False, extra=None, verify=True, expect=None, strip_bytecounts=True,
             ifd_first=False, **facts):
        h, w = arr.shape[:2]
        rows = raw_rows(arr if arr.ndim == 3 else arr[..., None], bits, big_endian)
        raw = b"".join(rows)
        if predictor == 2 and compression in PREDICTOR_CODECS:
            raw = tiff_predictor2(raw, w, spp, bits, big_endian)
            row_len = len(rows[0])
            rows = [raw[i:i + row_len] for i in range(0, len(raw), row_len)]
        rps = rows_per_strip or h
        strips = tiff_strips(rows, rps, compressor(compression))
        page = TiffPage(w, h, base_tags(bits, spp, photometric, compression, rows_per_strip, predictor, extra), strips=strips)
        data = tiff_write([page], big_endian, ifd_first=ifd_first, strip_bytecounts=strip_bytecounts)
        record(name, data, [expected] if expected is not None else None, notes, "hand", verify, expect,
               compression=compression, photometric=photometric, bits_per_sample=[bits] * spp, predictor=predictor,
               byte_order="MM" if big_endian else "II", **facts)

    g = gradient_gray(17, 11, seed=320).astype(np.uint8)
    hand("hand_gray8_mm", g, 8, 1, 1, rgba_from_gray(g), "big-endian (MM) 8-bit gray, uncompressed", big_endian=True)
    rgb = gradient_rgb(15, 12, seed=321)
    hand("hand_rgb8_mm_lzw", rgb, 8, 3, 2, rgba_from_rgb(rgb), "big-endian RGB, LZW, RowsPerStrip 5", compression=5,
         rows_per_strip=5, big_endian=True)
    hand("hand_rgb8_raw_pred2", rgb, 8, 3, 2, rgba_from_rgb(rgb), "uncompressed RGB carrying Predictor=2: like libtiff, the "
         "predictor only applies to LZW/Deflate codecs, so the samples are stored (and decoded) undifferenced", predictor=2)
    hand("hand_rgb8_packbits_pred2", rgb, 8, 3, 2, rgba_from_rgb(rgb), "PackBits carrying Predictor=2: ignored (libtiff semantics)",
         predictor=2, compression=32773)
    hand("hand_rgb8_lzw_pred2", rgb, 8, 3, 2, rgba_from_rgb(rgb), "hand LZW + predictor 2, RowsPerStrip 4", compression=5,
         predictor=2, rows_per_strip=4)
    hand("hand_rgb8_packbits", rgb, 8, 3, 2, rgba_from_rgb(rgb), "hand PackBits RGB, RowsPerStrip 1", compression=32773,
         rows_per_strip=1)
    hand("hand_gray8_deflate_mm", g, 8, 1, 1, rgba_from_gray(g), "big-endian gray with zlib-wrapped deflate (8)", compression=8,
         big_endian=True)
    rows = raw_rows(g[..., None], 8, False)
    page = TiffPage(17, 11, base_tags(8, 1, 1, 32946), strips=[zlib.compress(b"".join(rows), 6)])
    record("hand_gray8_deflate_32946", tiff_write([page]), [rgba_from_gray(g)], "deflate using the legacy compression code 32946",
           "hand", compression=32946, photometric=1, bits_per_sample=[8], predictor=1, byte_order="II")

    g16 = noise((10, 13), seed=322, high=65536, dtype=np.uint16)
    hand("hand_gray16_mm", g16, 16, 1, 1, rgba_from_gray(g16 >> 8), "big-endian 16-bit gray (byte order matters for the "
         "high byte)", big_endian=True, verify=False)
    hand("hand_gray16_ii_lzw_pred2", g16, 16, 1, 1, rgba_from_gray(g16 >> 8), "little-endian 16-bit gray, LZW, predictor 2 "
         "on 16-bit samples", compression=5, predictor=2, verify=False)
    hand("hand_gray16_mm_pred2", g16, 16, 1, 1, rgba_from_gray(g16 >> 8), "big-endian 16-bit gray with predictor 2",
         predictor=2, big_endian=True, verify=False)
    rgb16 = noise((8, 11, 3), seed=323, high=65536, dtype=np.uint16)
    hand("hand_rgb16_raw", rgb16, 16, 3, 2, rgba_from_rgb(rgb16 >> 8), "16-bit RGB uncompressed", verify=False)
    hand("hand_rgb16_mm_lzw_pred2", rgb16, 16, 3, 2, rgba_from_rgb(rgb16 >> 8), "big-endian 16-bit RGB, LZW, predictor 2",
         compression=5, predictor=2, big_endian=True, verify=False, rows_per_strip=3)
    rgba16 = noise((7, 9, 4), seed=324, high=65536, dtype=np.uint16)
    hand("hand_rgba16_raw", rgba16, 16, 4, 2, (rgba16 >> 8).astype(np.uint8), "16-bit RGBA uncompressed",
         extra={338: (3, [2])}, verify=False)
    la16 = noise((6, 8, 2), seed=325, high=65536, dtype=np.uint16)
    hand("hand_graya16_raw", la16, 16, 2, 1, rgba_from_gray(la16[..., 0] >> 8, (la16[..., 1] >> 8).astype(np.uint8)),
         "16-bit gray + alpha", extra={338: (3, [2])}, verify=False)

    ginv = gradient_gray(13, 9, seed=326).astype(np.uint8)
    hand("hand_gray8_minwhite", ginv, 8, 1, 0, rgba_from_gray(255 - ginv), "8-bit WhiteIsZero (photometric 0): inverted")
    bw = (gradient_gray(29, 7, 2, seed=327) == 1).astype(np.uint8)
    hand("hand_bilevel_minwhite", bw, 1, 1, 0, rgba_from_gray((1 - bw) * 255), "1-bit WhiteIsZero: bit 1 is black",
         compression=32773)
    hand("hand_bilevel_minblack_lzw", bw, 1, 1, 1, rgba_from_gray(bw * 255), "1-bit BlackIsZero LZW, RowsPerStrip 2",
         compression=5, rows_per_strip=2)
    hand("hand_gray8_no_stripbytecounts", g, 8, 1, 1, rgba_from_gray(g), "uncompressed with the StripByteCounts tag absent",
         strip_bytecounts=False)
    hand("hand_gray8_ifd_before_data", g, 8, 1, 1, rgba_from_gray(g), "IFD stored before the strip data (offsets point forward)",
         ifd_first=True, compression=5, rows_per_strip=3)
    hand("hand_rgb8_rps1", rgb, 8, 3, 2, rgba_from_rgb(rgb), "one strip per row (12 strips)", rows_per_strip=1)
    hand("hand_rgb8_last_strip_short", rgb, 8, 3, 2, rgba_from_rgb(rgb), "RowsPerStrip 5 over 12 rows: last strip has 2 rows",
         rows_per_strip=5, compression=32773)
    rgba_assoc = np.dstack([gradient_rgb(9, 8, seed=328), noise((8, 9), seed=329)])
    hand("hand_rgba8_assoc_alpha", rgba_assoc, 8, 4, 2, rgba_assoc, "RGBA with ExtraSamples=1 (associated alpha); samples are "
         "passed through unchanged (no un-premultiplication)", extra={338: (3, [1])}, verify=False)
    hand("hand_rgba8_lzw_pred2", rgba_assoc, 8, 4, 2, rgba_assoc, "RGBA LZW with predictor 2 (spp 4)", compression=5,
         predictor=2, extra={338: (3, [2])})
    pal = palette_rgb(256, seed=330)
    idx = noise((9, 12), seed=331)
    colormap = [int(v) * 257 for v in pal[:, 0]] + [int(v) * 257 for v in pal[:, 1]] + [int(v) * 257 for v in pal[:, 2]]
    hand("hand_pal8_mm_packbits", idx, 8, 1, 3, rgba_from_rgb(pal[idx]), "big-endian palette, PackBits", compression=32773,
         big_endian=True, extra={320: (3, colormap)})
    pal16 = palette_rgb(16, seed=332)
    idx4 = gradient_gray(15, 8, 16, seed=333).astype(np.uint8)
    cm16 = [int(v) * 257 for v in pal16[:, 0]] + [int(v) * 257 for v in pal16[:, 1]] + [int(v) * 257 for v in pal16[:, 2]]
    hand("hand_pal4", idx4, 4, 1, 3, rgba_from_rgb(pal16[idx4]), "4-bit palette (16-entry ColorMap)", extra={320: (3, cm16)})
    pal2c = palette_rgb(2, seed=336)
    idx1 = (gradient_gray(21, 5, 2, seed=337) == 1).astype(np.uint8)
    cm2 = [int(v) * 257 for v in pal2c[:, 0]] + [int(v) * 257 for v in pal2c[:, 1]] + [int(v) * 257 for v in pal2c[:, 2]]
    hand("hand_pal1", idx1, 1, 1, 3, rgba_from_rgb(pal2c[idx1]), "1-bit palette (2-entry ColorMap), width 21",
         extra={320: (3, cm2)}, verify=False)
    g4 = gradient_gray(19, 7, 16, seed=334)
    hand("hand_gray4", g4, 4, 1, 1, rgba_from_gray(g4 * 17), "4-bit grayscale BlackIsZero (scaled x17)")
    g2 = gradient_gray(13, 6, 4, seed=335)
    hand("hand_gray2_minwhite", g2, 2, 1, 0, rgba_from_gray(255 - g2 * 85), "2-bit grayscale WhiteIsZero (scaled x85, inverted)",
         verify=False)

    # Tiled layouts
    def tiled(name, arr, bits, spp, photometric, expected, tw, th, compression, notes, big_endian=False, predictor=1,
              verify=True):
        h, w = arr.shape[:2]
        a = arr if arr.ndim == 3 else arr[..., None]
        tiles = []
        for ty in range(0, h, th):
            for tx in range(0, w, tw):
                tile = np.zeros((th, tw, a.shape[2]), a.dtype)
                sub = a[ty:ty + th, tx:tx + tw]
                tile[:sub.shape[0], :sub.shape[1]] = sub
                rows = raw_rows(tile, bits, big_endian)
                raw = b"".join(rows)
                if predictor == 2 and compression in PREDICTOR_CODECS:
                    raw = tiff_predictor2(raw, tw, spp, bits, big_endian)
                tiles.append(compressor(compression)(raw))
        tags = base_tags(bits, spp, photometric, compression, None, predictor, {322: (4, [tw]), 323: (4, [th])})
        page = TiffPage(w, h, tags, tiles=tiles)
        record(name, tiff_write([page], big_endian), [expected], notes, "hand", verify=verify, compression=compression,
               photometric=photometric, bits_per_sample=[bits] * spp, tile_size=[tw, th], predictor=predictor,
               byte_order="MM" if big_endian else "II")

    rgbt = gradient_rgb(20, 12, seed=340)
    tiled("hand_rgb8_tiled_raw", rgbt, 8, 3, 2, rgba_from_rgb(rgbt), 16, 16, 1, "tiled RGB 20x12 with 16x16 tiles (partial tiles)")
    tiled("hand_rgb8_tiled_lzw_pred2", rgbt, 8, 3, 2, rgba_from_rgb(rgbt), 16, 16, 5, "tiled RGB, LZW + predictor 2, MM",
          big_endian=True, predictor=2)
    gt = gradient_gray(37, 21, seed=341).astype(np.uint8)
    tiled("hand_gray8_tiled_packbits", gt, 8, 1, 1, rgba_from_gray(gt), 16, 16, 32773, "tiled gray 37x21, PackBits, 3x2 tiles")
    tiled("hand_gray16_tiled_deflate", g16, 16, 1, 1, rgba_from_gray(g16 >> 8), 16, 16, 8, "tiled 16-bit gray, deflate",
          verify=False)
    tiled("hand_bilevel_tiled_raw", bw, 1, 1, 1, rgba_from_gray(bw * 255), 16, 16, 1, "tiled 1-bit 29x7 (row bytes per tile)")

    # Planar configuration 2: one strip per sample plane
    rows = [rgb[..., c].astype(np.uint8).tobytes() for c in range(3)]
    page = TiffPage(15, 12, {**base_tags(8, 3, 2, 1), 284: (3, [2])}, strips=rows)
    record("hand_planar2_rgb8", tiff_write([page]), [rgba_from_rgb(rgb)],
           "PlanarConfiguration 2: each sample in its own strip", "hand",
           verify="soft", compression=1, photometric=2, planar=2)

    # Multi-page hand-written with different sizes, byte order MM
    p1 = gradient_gray(8, 6, seed=350).astype(np.uint8)
    p2 = gradient_rgb(7, 5, seed=351)
    p3 = (gradient_gray(9, 4, 2, seed=352) == 1).astype(np.uint8)
    pages = [TiffPage(8, 6, base_tags(8, 1, 1, 1), strips=[b"".join(raw_rows(p1[..., None], 8, True))]),
             TiffPage(7, 5, base_tags(8, 3, 2, 5, rows_per_strip=2), strips=tiff_strips(raw_rows(p2, 8, True), 2, tiff_lzw_encode)),
             TiffPage(9, 4, base_tags(1, 1, 0, 32773), strips=[packbits_encode(b"".join(raw_rows(p3[..., None], 1, True)))])]
    record("hand_multipage_mm", tiff_write(pages, big_endian=True),
           [rgba_from_gray(p1), rgba_from_rgb(p2), rgba_from_gray((1 - p3) * 255)],
           "3 big-endian pages: gray8 raw 8x6, rgb8 lzw 7x5, bilevel min-is-white packbits 9x4", "hand", byte_order="MM")

    write_manifest(out_dir, entries)
    print(f"tiff: {len(entries)} fixtures")


# ---------------------------------------------------------------------------
# Entry point: run every gen_<format>(Fixtures/<format>) in this module.
# ---------------------------------------------------------------------------

def main() -> None:
    """Runs every gen_<format>(Fixtures/<format>) defined here or in a sibling ``gen_<format>.py`` module."""
    import importlib.util

    generators: dict[str, object] = {
        name: fn for name, fn in globals().items() if name.startswith("gen_") and callable(fn)
    }
    for module_path in sorted(os.listdir(HERE)):
        if module_path.startswith("gen_") and module_path.endswith(".py"):
            spec = importlib.util.spec_from_file_location(module_path[:-3], os.path.join(HERE, module_path))
            module = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
            spec.loader.exec_module(module)  # type: ignore[union-attr]
            for name, fn in vars(module).items():
                if name.startswith("gen_") and callable(fn):
                    generators[name] = fn
    for name in sorted(generators):
        fmt = name[len("gen_"):]
        print(f"{fmt}/")
        generators[name](os.path.join(HERE, fmt))


if __name__ == "__main__":
    main()
