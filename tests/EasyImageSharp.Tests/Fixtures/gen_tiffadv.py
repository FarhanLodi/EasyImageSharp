#!/usr/bin/env python
"""Fixtures for the advanced TIFF features: CCITT bilevel coding, JPEG-in-TIFF, planar and tiled
layouts, the wider sample formats and the remaining photometric interpretations.

Run through ``generate.py`` (which calls ``gen_tiffadv(Fixtures/tiffadv)``) or directly:

    python gen_tiffadv.py

Every fixture is either written by Pillow/libtiff or assembled byte by byte here; nothing in this file
uses the library under test. Each entry in ``manifest.json`` carries:

  name, file, width, height, frames   what the decoder must report
  feature                             the feature group the fixture belongs to
  match                               "exact" (default), "tolerance" or "psnr"
  tolerance / psnr                    the bound for the non-exact ones
  notes                               a human description

The ground truth for an entry lives in ``<name>.rgba`` (width*height*4 bytes per frame, RGBA,
row-major, top-left origin) plus ``<name>.expected.png`` for eyeballing.
"""
from __future__ import annotations

import io
import json
import os
import struct

import numpy as np
from PIL import Image, TiffImagePlugin

HERE = os.path.dirname(os.path.abspath(__file__))

# ----------------------------------------------------------------------------------------------------
# A minimal little/big-endian TIFF assembler, used for the layouts Pillow cannot write.
# ----------------------------------------------------------------------------------------------------

BYTE, ASCII, SHORT, LONG, RATIONAL, SBYTE, UNDEFINED = 1, 2, 3, 4, 5, 6, 7
LONG8 = 16                      # BigTIFF's 64-bit offset type (17 and 18 are SLONG8 and IFD8)

TAG_WIDTH = 256
TAG_LENGTH = 257
TAG_BITS = 258
TAG_COMPRESSION = 259
TAG_PHOTOMETRIC = 262
TAG_FILLORDER = 266
TAG_STRIP_OFFSETS = 273
TAG_SAMPLES = 277
TAG_ROWS_PER_STRIP = 278
TAG_STRIP_COUNTS = 279
TAG_PLANAR = 284
TAG_T4 = 292
TAG_T6 = 293
TAG_PREDICTOR = 317
TAG_TILE_WIDTH = 322
TAG_TILE_LENGTH = 323
TAG_TILE_OFFSETS = 324
TAG_TILE_COUNTS = 325
TAG_INKSET = 332
TAG_EXTRA_SAMPLES = 338
TAG_SAMPLE_FORMAT = 339
TAG_JPEG_TABLES = 347
TAG_YCBCR_SUBSAMPLING = 530


def _pack(kind: int, values, big: bool) -> bytes:
    end = ">" if big else "<"
    if kind in (BYTE, ASCII, SBYTE, UNDEFINED):
        return bytes(values)
    if kind == SHORT:
        return struct.pack(f"{end}{len(values)}H", *values)
    if kind == LONG:
        return struct.pack(f"{end}{len(values)}I", *values)
    if kind == LONG8:
        return struct.pack(f"{end}{len(values)}Q", *values)
    if kind == RATIONAL:
        return b"".join(struct.pack(f"{end}II", n, d) for n, d in values)
    raise ValueError(f"unsupported TIFF type {kind}")


def _shape(data: bytes) -> tuple[str, bool, int, int, int, str, str]:
    """The directory geometry of a file: byte order, BigTIFF flag, and the widths that follow from it.

    Returns ``(end, bigtiff, wide, count_size, entry_size, count_fmt, ptr_fmt)`` where ``wide`` is at once the
    value-field width, the pointer width and the largest value that still fits inside an entry.
    """
    end = ">" if data[0] == 0x4D else "<"
    bigtiff = struct.unpack_from(f"{end}H", data, 2)[0] == 43
    wide = 8 if bigtiff else 4
    return (end, bigtiff, wide, 8 if bigtiff else 2, 20 if bigtiff else 12,
            f"{end}{'Q' if bigtiff else 'H'}", f"{end}{'Q' if bigtiff else 'I'}")


def tiff_write(pages: list[dict], big: bool = False, bigtiff: bool = False) -> bytes:
    """Assembles one file from pages of ``{"tags": {tag: (type, values)}, "blocks": [bytes], "tiled": bool}``.

    The strip/tile offset and byte-count tags are filled in from the blocks actually written. ``big`` is the BYTE
    ORDER; ``bigtiff`` is the CONTAINER version and is independent of it - version 43 means a 16-byte header, an
    8-byte entry count, 20-byte entries whose element count and value field are both 8 bytes, an 8-byte
    next-directory pointer, and offsets and byte counts written as LONG8 rather than LONG.
    """
    end = ">" if big else "<"
    wide = 8 if bigtiff else 4
    count_size = 8 if bigtiff else 2
    entry_size = 20 if bigtiff else 12
    count_fmt = f"{end}{'Q' if bigtiff else 'H'}"
    ptr_fmt = f"{end}{'Q' if bigtiff else 'I'}"
    offset_kind = LONG8 if bigtiff else LONG
    if bigtiff:
        out = bytearray(b"MM\x00\x2b" if big else b"II\x2b\x00")
        out += struct.pack(f"{end}HH", 8, 0)     # offset size, reserved
    else:
        out = bytearray(b"MM\x00\x2a" if big else b"II\x2a\x00")
    link = len(out)  # position of the pointer that must be made to point at the next directory
    out += b"\0" * wide

    for page in pages:
        offsets, counts = [], []
        for block in page["blocks"]:
            if len(out) % 2:
                out += b"\0"
            offsets.append(len(out))
            counts.append(len(block))
            out += block

        tags = dict(page["tags"])
        tiled = page.get("tiled", False)
        tags[TAG_TILE_OFFSETS if tiled else TAG_STRIP_OFFSETS] = (offset_kind, offsets)
        tags[TAG_TILE_COUNTS if tiled else TAG_STRIP_COUNTS] = (offset_kind, counts)

        if len(out) % 2:
            out += b"\0"
        ifd = len(out)
        struct.pack_into(ptr_fmt, out, link, ifd)

        items = sorted(tags.items())
        external_base = ifd + count_size + (entry_size * len(items)) + wide
        body = bytearray(struct.pack(count_fmt, len(items)))
        external = bytearray()
        for tag, (kind, values) in items:
            data = _pack(kind, values, big)
            body += struct.pack(f"{end}HH", tag, kind) + struct.pack(ptr_fmt, len(values))
            if len(data) <= wide:
                body += data + (b"\0" * (wide - len(data)))
            else:
                body += struct.pack(ptr_fmt, external_base + len(external))
                external += data
                if len(external) % 2:
                    external += b"\0"
        link = ifd + count_size + (entry_size * len(items))
        body += b"\0" * wide
        out += body + external

    return bytes(out)


def tiff_tags(data: bytes) -> dict:
    """Reads the first directory of a TIFF into ``{tag: (type, [values])}`` (offsets resolved)."""
    end, _, wide, count_size, entry_size, count_fmt, ptr_fmt = _shape(data)
    (offset,) = struct.unpack_from(ptr_fmt, data, 8 if wide == 8 else 4)
    (count,) = struct.unpack_from(count_fmt, data, offset)
    sizes = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8, 13: 4, 16: 8, 17: 8, 18: 8}
    tags = {}
    for i in range(count):
        entry = offset + count_size + (entry_size * i)
        tag, kind = struct.unpack_from(f"{end}HH", data, entry)
        (n,) = struct.unpack_from(ptr_fmt, data, entry + 4)
        size = sizes.get(kind, 0) * n
        where = entry + 4 + wide if size <= wide else struct.unpack_from(ptr_fmt, data, entry + 4 + wide)[0]
        if kind in (1, 2, 6, 7):
            values = list(data[where:where + n])
        elif kind in (3, 8):
            values = list(struct.unpack_from(f"{end}{n}H", data, where))
        elif kind in (4, 9, 13):
            values = list(struct.unpack_from(f"{end}{n}I", data, where))
        elif kind in (16, 18):
            values = list(struct.unpack_from(f"{end}{n}Q", data, where))
        elif kind == 17:
            values = list(struct.unpack_from(f"{end}{n}q", data, where))
        else:
            values = []
        tags[tag] = (kind, values)
    return tags


def tiff_segments(data: bytes) -> list[bytes]:
    """Returns the strip (or tile) payloads of the first directory, in order."""
    tags = tiff_tags(data)
    offsets = tags[TAG_TILE_OFFSETS][1] if TAG_TILE_OFFSETS in tags else tags[TAG_STRIP_OFFSETS][1]
    counts = tags[TAG_TILE_COUNTS][1] if TAG_TILE_COUNTS in tags else tags[TAG_STRIP_COUNTS][1]
    return [data[o:o + c] for o, c in zip(offsets, counts)]


def tiff_probe(data: bytes) -> tuple[int, int, int]:
    end, _, wide, count_size, entry_size, count_fmt, ptr_fmt = _shape(data)
    (offset,) = struct.unpack_from(ptr_fmt, data, 8 if wide == 8 else 4)
    width = height = frames = 0
    while offset:
        (count,) = struct.unpack_from(count_fmt, data, offset)
        if frames == 0:
            for i in range(count):
                entry = offset + count_size + (entry_size * i)
                tag, kind = struct.unpack_from(f"{end}HH", data, entry)
                if tag in (TAG_WIDTH, TAG_LENGTH):
                    fmt = {SHORT: f"{end}H", LONG8: f"{end}Q"}.get(kind, f"{end}I")
                    value = struct.unpack_from(fmt, data, entry + 4 + wide)[0]
                    if tag == TAG_WIDTH:
                        width = value
                    else:
                        height = value
        frames += 1
        (offset,) = struct.unpack_from(ptr_fmt, data, offset + count_size + (entry_size * count))
    return width, height, frames


# ----------------------------------------------------------------------------------------------------
# Pixel helpers
# ----------------------------------------------------------------------------------------------------

def rng(seed: int) -> np.random.Generator:
    return np.random.default_rng(seed)


def rgba_from_gray(gray: np.ndarray) -> np.ndarray:
    g = gray.astype(np.uint8)
    return np.dstack([g, g, g, np.full(g.shape, 255, np.uint8)])


def rgba_from_rgb(rgb: np.ndarray) -> np.ndarray:
    rgb = rgb.astype(np.uint8)
    return np.dstack([rgb, np.full(rgb.shape[:2], 255, np.uint8)])


def bilevel(width: int, height: int, seed: int) -> np.ndarray:
    """A scan-like bilevel page: horizontal bars, glyph-ish blobs and speckle, as a bool array (True = white)."""
    r = rng(seed)
    page = np.ones((height, width), bool)
    for y in range(0, height, 5):
        page[y, :] = False
    for _ in range(max(4, (width * height) // 60)):
        y0 = int(r.integers(0, height))
        x0 = int(r.integers(0, width))
        h = int(r.integers(1, 4))
        w = int(r.integers(1, 6))
        page[y0:y0 + h, x0:x0 + w] = False
    page[:, 0] = True
    speckle = r.integers(0, 24, (height, width)) == 0
    page[speckle] = ~page[speckle]
    return page


def pack_bits(mask: np.ndarray) -> bytes:
    """Packs a boolean H x W array into MSB-first rows, one bit per pixel (True = 1)."""
    return np.packbits(mask.astype(np.uint8), axis=1).tobytes()


def reverse_bits(data: bytes) -> bytes:
    table = bytes(int(f"{i:08b}"[::-1], 2) for i in range(256))
    return data.translate(table)


def rows_of(planar_bytes: bytes, rows: int) -> list[bytes]:
    step = len(planar_bytes) // rows
    return [planar_bytes[i * step:(i + 1) * step] for i in range(rows)]


# ----------------------------------------------------------------------------------------------------
# Fixture recording
# ----------------------------------------------------------------------------------------------------

class Recorder:
    def __init__(self, out_dir: str):
        self.out_dir = out_dir
        self.entries: list[dict] = []

    def add(self, name: str, data: bytes, frames: list[np.ndarray], feature: str, notes: str, **extra) -> None:
        path = os.path.join(self.out_dir, name + ".tif")
        with open(path, "wb") as fh:
            fh.write(data)
        width, height, count = tiff_probe(data)
        assert len(frames) == count, (name, len(frames), count)
        assert frames[0].shape[1] == width and frames[0].shape[0] == height, (name, frames[0].shape, width, height)
        for frame in frames:
            assert frame.dtype == np.uint8 and frame.ndim == 3 and frame.shape[2] == 4, (name, frame.shape, frame.dtype)
        with open(os.path.join(self.out_dir, name + ".rgba"), "wb") as fh:
            for frame in frames:
                fh.write(np.ascontiguousarray(frame).tobytes())
        Image.fromarray(frames[0], "RGBA").save(os.path.join(self.out_dir, name + ".expected.png"))
        entry = {"name": name, "file": name + ".tif", "width": width, "height": height, "frames": count,
                 "feature": feature, "match": extra.pop("match", "exact"), "notes": notes}
        entry.update(extra)
        self.entries.append(entry)

    def write_manifest(self) -> None:
        with open(os.path.join(self.out_dir, "manifest.json"), "w", newline="\n") as fh:
            json.dump(self.entries, fh, indent=1)
            fh.write("\n")


def pillow_tiff(image: Image.Image, **save_kw) -> bytes:
    buf = io.BytesIO()
    image.save(buf, format="TIFF", **save_kw)
    return buf.getvalue()


def pillow_decodes_to(data: bytes, expected: np.ndarray, name: str) -> None:
    """Asserts Pillow itself reads the fixture back as the ground truth says (bilevel/exact fixtures only)."""
    with Image.open(io.BytesIO(data)) as im:
        got = np.array(im.convert("RGB"))
    assert np.array_equal(got, expected[..., :3]), f"{name}: Pillow disagrees with the ground truth"


def bigtiff_decodes_to(data: bytes, expected: np.ndarray, name: str) -> None:
    """Asserts a fixture really is a version-43 BigTIFF and that an independent reader agrees with its pixels.

    The version word is checked explicitly because Pillow accepts ``big_tiff=True`` on save and then silently
    ignores it for everything it routes through libtiff (LZW, Deflate and PackBits all come back as version 42).
    Pillow 11.3 also detects BigTIFF with ``ifh[2] == 43``, an index that only lands on the version word in the
    little-endian layout, so it cannot open a big-endian BigTIFF at all; tifffile reads both byte orders and is
    used whenever it is installed, though without the optional ``imagecodecs`` package it can parse but not
    decode an LZW or PackBits page. At least one independent reader must confirm the pixels of every fixture.
    """
    end = ">" if data[0] == 0x4D else "<"
    assert struct.unpack_from(f"{end}HHH", data, 2) == (43, 8, 0), f"{name}: this is not a BigTIFF header"
    checked = False
    try:
        import tifffile
    except ImportError:
        print(f"  note: tifffile is not installed; {name} was not cross-checked against a second BigTIFF reader.")
    else:
        with tifffile.TiffFile(io.BytesIO(data)) as handle:
            assert handle.is_bigtiff, f"{name}: tifffile does not read this file as a BigTIFF"
            page = handle.pages[0]
            assert (page.imagelength, page.imagewidth) == expected.shape[:2], f"{name}: tifffile reads other dimensions"
            try:
                got = np.asarray(page.asarray())
            except ValueError as ex:                       # a codec tifffile needs imagecodecs to run
                print(f"  note: tifffile parsed {name} but cannot decode it: {ex}")
            else:
                assert np.array_equal(got, expected[..., :3]), f"{name}: tifffile disagrees with the ground truth"
                checked = True
    if data[0] != 0x4D:
        pillow_decodes_to(data, expected, name)
        checked = True
    assert checked, f"{name}: no independent reader could decode this fixture"


# ----------------------------------------------------------------------------------------------------
# CCITT Group 3 / Group 4 / Modified Huffman
# ----------------------------------------------------------------------------------------------------

def _ccitt(rec: Recorder) -> None:
    # Pillow writes mode "1" pages as PhotometricInterpretation 1 (BlackIsZero), so a set bit is white.
    odd = bilevel(37, 11, seed=4001)
    wide = bilevel(64, 24, seed=4002)

    for name, page, kwargs, notes in (
        ("ccitt_g4_37x11", odd, {"compression": "group4"}, "Group 4 (T.6), width 37 is not a multiple of 8"),
        ("ccitt_g4_64x24", wide, {"compression": "group4"}, "Group 4 (T.6), width 64"),
        ("ccitt_g3_1d", odd, {"compression": "group3"}, "Group 3 one-dimensional, EOL separated"),
        ("ccitt_g3_2d", wide, {"compression": "group3", "tiffinfo": {TAG_T4: 1}}, "Group 3 with T4Options bit 0: 2D coding"),
        ("ccitt_g3_fill", wide, {"compression": "group3", "tiffinfo": {TAG_T4: 4}}, "Group 3 with T4Options bit 2: fill bits before EOL"),
        ("ccitt_g3_2d_fill", wide, {"compression": "group3", "tiffinfo": {TAG_T4: 5}}, "Group 3, 2D coding with fill bits"),
        ("ccitt_mh", odd, {"compression": "tiff_ccitt"}, "Modified Huffman (compression 2), rows padded to byte boundaries"),
    ):
        data = pillow_tiff(Image.fromarray(page), **kwargs)
        expected = rgba_from_gray(page.astype(np.uint8) * 255)
        pillow_decodes_to(data, expected, name)
        rec.add(name, data, [expected], "ccitt", notes)

    # Group 4 split over many strips (libtiff restarts the reference line at every strip).
    previous = TiffImagePlugin.STRIP_SIZE
    TiffImagePlugin.STRIP_SIZE = 16
    try:
        data = pillow_tiff(Image.fromarray(wide), compression="group4")
    finally:
        TiffImagePlugin.STRIP_SIZE = previous
    assert len(tiff_segments(data)) > 4, "expected several strips"
    expected = rgba_from_gray(wide.astype(np.uint8) * 255)
    pillow_decodes_to(data, expected, "ccitt_g4_strips")
    rec.add("ccitt_g4_strips", data, [expected], "ccitt",
            f"Group 4 in {len(tiff_segments(data))} strips of 2 rows")

    # The same coded bytes re-tagged: PhotometricInterpretation 0 makes every coded white run white again,
    # which inverts the page Pillow wrote as BlackIsZero.
    coded = tiff_segments(pillow_tiff(Image.fromarray(odd), compression="group4"))
    height, width = odd.shape
    base = {TAG_WIDTH: (LONG, [width]), TAG_LENGTH: (LONG, [height]), TAG_BITS: (SHORT, [1]),
            TAG_COMPRESSION: (SHORT, [4]), TAG_SAMPLES: (SHORT, [1]), TAG_ROWS_PER_STRIP: (LONG, [height]),
            TAG_PLANAR: (SHORT, [1])}
    rec.add("ccitt_g4_whiteiszero",
            tiff_write([{"tags": {**base, TAG_PHOTOMETRIC: (SHORT, [0])}, "blocks": coded}]),
            [rgba_from_gray((~odd).astype(np.uint8) * 255)], "ccitt",
            "Group 4 with PhotometricInterpretation 0 (WhiteIsZero), the usual fax tagging")

    rec.add("ccitt_g4_fillorder2",
            tiff_write([{"tags": {**base, TAG_PHOTOMETRIC: (SHORT, [1]), TAG_FILLORDER: (SHORT, [2])},
                         "blocks": [reverse_bits(block) for block in coded]}]),
            [rgba_from_gray(odd.astype(np.uint8) * 255)], "ccitt",
            "Group 4 with FillOrder 2: every coded byte holds its bits least significant first")

    rec.add("ccitt_g4_bigendian",
            tiff_write([{"tags": {**base, TAG_PHOTOMETRIC: (SHORT, [1])}, "blocks": coded}], big=True),
            [rgba_from_gray(odd.astype(np.uint8) * 255)], "ccitt",
            "Group 4 in a big-endian (MM) file")

    # Tiles, each coded independently by libtiff: 32x16 tiles over a 40x28 page.
    tile_w, tile_h = 32, 16
    page = bilevel(40, 28, seed=4003)
    blocks = []
    for ty in range(0, page.shape[0], tile_h):
        for tx in range(0, page.shape[1], tile_w):
            tile = np.ones((tile_h, tile_w), bool)
            chunk = page[ty:ty + tile_h, tx:tx + tile_w]
            tile[:chunk.shape[0], :chunk.shape[1]] = chunk
            blocks.append(tiff_segments(pillow_tiff(Image.fromarray(tile), compression="group4"))[0])
    rec.add("ccitt_g4_tiled",
            tiff_write([{"tags": {TAG_WIDTH: (LONG, [40]), TAG_LENGTH: (LONG, [28]), TAG_BITS: (SHORT, [1]),
                                  TAG_COMPRESSION: (SHORT, [4]), TAG_PHOTOMETRIC: (SHORT, [1]),
                                  TAG_SAMPLES: (SHORT, [1]), TAG_PLANAR: (SHORT, [1]),
                                  TAG_TILE_WIDTH: (LONG, [tile_w]), TAG_TILE_LENGTH: (LONG, [tile_h])},
                         "blocks": blocks, "tiled": True}]),
            [rgba_from_gray(page.astype(np.uint8) * 255)], "ccitt",
            f"Group 4 in {tile_w}x{tile_h} tiles over a 40x28 page")


# ----------------------------------------------------------------------------------------------------
# Planar and tiled layouts
# ----------------------------------------------------------------------------------------------------

def _rgb_page(width: int, height: int, seed: int) -> np.ndarray:
    r = rng(seed)
    x = np.arange(width)[None, :]
    y = np.arange(height)[:, None]
    red = np.broadcast_to((x * 6) % 256, (height, width))
    green = np.broadcast_to((y * 9) % 256, (height, width))
    blue = ((x * y) + r.integers(0, 48, (height, width))) % 256
    return np.stack([red, green, blue], axis=-1).astype(np.uint8)


def _strip_blocks(data: bytes, rows: int, rows_per_strip: int, compress=None) -> list[bytes]:
    row_bytes = len(data) // rows
    blocks = []
    for start in range(0, rows, rows_per_strip):
        end = min(rows, start + rows_per_strip)
        block = data[start * row_bytes:end * row_bytes]
        blocks.append(compress(block) if compress else block)
    return blocks


def _tile_blocks(plane: np.ndarray, tile_w: int, tile_h: int, compress=None) -> list[bytes]:
    """Cuts an H x W x S array into row-major tiles, padding the edge tiles with zeros as TIFF requires."""
    height, width = plane.shape[:2]
    samples = plane.shape[2] if plane.ndim == 3 else 1
    blocks = []
    for ty in range(0, height, tile_h):
        for tx in range(0, width, tile_w):
            tile = np.zeros((tile_h, tile_w, samples), plane.dtype)
            chunk = plane.reshape(height, width, samples)[ty:ty + tile_h, tx:tx + tile_w]
            tile[:chunk.shape[0], :chunk.shape[1]] = chunk
            raw = np.ascontiguousarray(tile).tobytes()
            blocks.append(compress(raw) if compress else raw)
    return blocks


def _layouts(rec: Recorder) -> None:
    import zlib

    width, height = 40, 28
    rgb = _rgb_page(width, height, seed=4100)
    expected = rgba_from_rgb(rgb)
    chunky = np.ascontiguousarray(rgb).tobytes()
    base = {TAG_WIDTH: (LONG, [width]), TAG_LENGTH: (LONG, [height]), TAG_BITS: (SHORT, [8, 8, 8]),
            TAG_PHOTOMETRIC: (SHORT, [2]), TAG_SAMPLES: (SHORT, [3])}

    rec.add("layout_chunky_raw",
            tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [1]), TAG_PLANAR: (SHORT, [1]),
                                  TAG_ROWS_PER_STRIP: (LONG, [height])}, "blocks": [chunky]}]),
            [expected], "layout", "8-bit RGB, chunky, one uncompressed strip: the reference for this group")

    for name, tile_w, tile_h, compress, extra, notes in (
        ("layout_tiled_raw", 16, 16, None, {}, "16x16 tiles, uncompressed"),
        ("layout_tiled_deflate", 16, 16, zlib.compress, {TAG_COMPRESSION: (SHORT, [8])}, "16x16 tiles, Deflate"),
        ("layout_tiled_wide", 48, 16, None, {}, "tiles wider than the page (48x16), so every tile is padded"),
    ):
        tags = {**base, TAG_COMPRESSION: (SHORT, [1]), TAG_PLANAR: (SHORT, [1]),
                TAG_TILE_WIDTH: (LONG, [tile_w]), TAG_TILE_LENGTH: (LONG, [tile_h]), **extra}
        rec.add(name, tiff_write([{"tags": tags, "blocks": _tile_blocks(rgb, tile_w, tile_h, compress), "tiled": True}]),
                [expected], "layout", f"8-bit RGB, {notes}")

    def _predict(raw: bytes) -> bytes:
        arr = np.frombuffer(raw, np.uint8).reshape(-1, 16, 3).astype(np.int16)
        diff = arr.copy()
        diff[:, 1:, :] = (arr[:, 1:, :] - arr[:, :-1, :]) % 256
        return zlib.compress(np.ascontiguousarray(diff.astype(np.uint8)).tobytes())

    rec.add("layout_tiled_deflate_predictor",
            tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [8]), TAG_PLANAR: (SHORT, [1]),
                                  TAG_PREDICTOR: (SHORT, [2]), TAG_TILE_WIDTH: (LONG, [16]), TAG_TILE_LENGTH: (LONG, [16])},
                         "blocks": _tile_blocks(rgb, 16, 16, _predict), "tiled": True}]),
            [expected], "layout", "8-bit RGB, 16x16 Deflate tiles with horizontal differencing")

    planes = [np.ascontiguousarray(rgb[..., c]).tobytes() for c in range(3)]
    rec.add("layout_planar_raw",
            tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [1]), TAG_PLANAR: (SHORT, [2]),
                                  TAG_ROWS_PER_STRIP: (LONG, [height])}, "blocks": planes}]),
            [expected], "layout", "8-bit RGB, PlanarConfiguration 2, one strip per plane")

    blocks = [b for plane in planes for b in _strip_blocks(plane, height, 8)]
    rec.add("layout_planar_strips",
            tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [1]), TAG_PLANAR: (SHORT, [2]),
                                  TAG_ROWS_PER_STRIP: (LONG, [8])}, "blocks": blocks}]),
            [expected], "layout", "8-bit RGB, PlanarConfiguration 2, four 8-row strips per plane")

    blocks = [b for plane in planes for b in _strip_blocks(plane, height, 8, zlib.compress)]
    rec.add("layout_planar_deflate",
            tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [8]), TAG_PLANAR: (SHORT, [2]),
                                  TAG_ROWS_PER_STRIP: (LONG, [8])}, "blocks": blocks}]),
            [expected], "layout", "8-bit RGB, PlanarConfiguration 2 with Deflate strips")

    blocks = [b for c in range(3) for b in _tile_blocks(rgb[..., c:c + 1], 16, 16)]
    rec.add("layout_planar_tiled",
            tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [1]), TAG_PLANAR: (SHORT, [2]),
                                  TAG_TILE_WIDTH: (LONG, [16]), TAG_TILE_LENGTH: (LONG, [16])},
                         "blocks": blocks, "tiled": True}]),
            [expected], "layout", "8-bit RGB, PlanarConfiguration 2 in 16x16 tiles")

    rgb16 = (rgb.astype(np.uint16) << 8) | rgb.astype(np.uint16)
    planes16 = [np.ascontiguousarray(rgb16[..., c]).astype(">u2").tobytes() for c in range(3)]
    rec.add("layout_planar_rgb16_mm",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [16, 16, 16]), TAG_COMPRESSION: (SHORT, [1]),
                                  TAG_PLANAR: (SHORT, [2]), TAG_ROWS_PER_STRIP: (LONG, [height])},
                         "blocks": planes16}], big=True),
            [expected], "layout", "16-bit RGB, PlanarConfiguration 2, big-endian")

    alpha = ((np.arange(width)[None, :] * 3 + np.arange(height)[:, None] * 5) % 256).astype(np.uint8)
    alpha = np.broadcast_to(alpha, (height, width)).astype(np.uint8)
    rgba = np.dstack([rgb, alpha])
    rec.add("layout_planar_rgba",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [8, 8, 8, 8]), TAG_SAMPLES: (SHORT, [4]),
                                  TAG_EXTRA_SAMPLES: (SHORT, [2]), TAG_COMPRESSION: (SHORT, [1]),
                                  TAG_PLANAR: (SHORT, [2]), TAG_ROWS_PER_STRIP: (LONG, [height])},
                         "blocks": [np.ascontiguousarray(rgba[..., c]).tobytes() for c in range(4)]}]),
            [rgba], "layout", "8-bit RGBA, PlanarConfiguration 2, unassociated alpha in its own plane")


# ----------------------------------------------------------------------------------------------------
# Sample formats
# ----------------------------------------------------------------------------------------------------

def _samples(rec: Recorder) -> None:
    width, height = 17, 11
    r = rng(4200)
    level = r.integers(0, 256, (height, width)).astype(np.uint8)
    grey = rgba_from_gray(level)
    base = {TAG_WIDTH: (LONG, [width]), TAG_LENGTH: (LONG, [height]), TAG_PHOTOMETRIC: (SHORT, [1]),
            TAG_SAMPLES: (SHORT, [1]), TAG_COMPRESSION: (SHORT, [1]), TAG_PLANAR: (SHORT, [1]),
            TAG_ROWS_PER_STRIP: (LONG, [height])}

    noise = r.integers(0, 1 << 24, (height, width)).astype(np.uint32)
    data = ((level.astype(np.uint32) << 24) | noise).astype("<u4").tobytes()
    rec.add("sample_gray32_uint",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [32]), TAG_SAMPLE_FORMAT: (SHORT, [1])}, "blocks": [data]}]),
            [grey], "samples", "32-bit unsigned samples reduced to their most significant byte")

    signed = ((level.astype(np.int64) - 128) << 24).astype(np.int32)
    rec.add("sample_gray32_int", pillow_tiff(Image.fromarray(signed, "I")), [grey], "samples",
            "32-bit signed samples (Pillow mode I) shifted into the unsigned range")

    rec.add("sample_gray32_float", pillow_tiff(Image.fromarray((level / 255.0).astype(np.float32), "F")), [grey],
            "samples", "32-bit floating-point samples (Pillow mode F) scaled from 0..1")

    signed16 = ((level.astype(np.int64) - 128) << 8).astype("<i2").tobytes()
    rec.add("sample_gray16_int",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [16]), TAG_SAMPLE_FORMAT: (SHORT, [2])}, "blocks": [signed16]}]),
            [grey], "samples", "16-bit signed samples shifted into the unsigned range")

    half = (level / 255.0).astype(np.float16).astype("<f2").tobytes()
    rec.add("sample_gray16_half",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [16]), TAG_SAMPLE_FORMAT: (SHORT, [3])}, "blocks": [half]}]),
            [grey], "samples", "16-bit half-precision floating-point samples scaled from 0..1")

    rgb = _rgb_page(width, height, seed=4201)
    floats = (rgb / 255.0).astype("<f4").tobytes()
    rec.add("sample_rgb32_float",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [32, 32, 32]), TAG_SAMPLES: (SHORT, [3]),
                                  TAG_PHOTOMETRIC: (SHORT, [2]), TAG_SAMPLE_FORMAT: (SHORT, [3, 3, 3])},
                         "blocks": [floats]}]),
            [rgba_from_rgb(rgb)], "samples", "32-bit floating-point RGB")

    planes = [np.ascontiguousarray(rgb[..., c] / 255.0).astype(">f4").tobytes() for c in range(3)]
    rec.add("sample_rgb32_float_planar_mm",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [32, 32, 32]), TAG_SAMPLES: (SHORT, [3]),
                                  TAG_PHOTOMETRIC: (SHORT, [2]), TAG_SAMPLE_FORMAT: (SHORT, [3, 3, 3]),
                                  TAG_PLANAR: (SHORT, [2])}, "blocks": planes}], big=True),
            [rgba_from_rgb(rgb)], "samples", "32-bit floating-point RGB, planar, big-endian")


# ----------------------------------------------------------------------------------------------------
# The remaining photometric interpretations
# ----------------------------------------------------------------------------------------------------

def cmyk_to_rgb(cmyk: np.ndarray) -> np.ndarray:
    """The multiplicative combination libtiff's RGBA reader uses."""
    c, m, y, k = (cmyk[..., i].astype(np.int64) for i in range(4))
    kk = 255 - k
    return np.stack([(kk * (255 - c)) // 255, (kk * (255 - m)) // 255, (kk * (255 - y)) // 255], axis=-1).astype(np.uint8)


def ycbcr_to_rgb(ycc: np.ndarray) -> np.ndarray:
    y = ycc[..., 0].astype(np.float64)
    u = ycc[..., 1].astype(np.float64) - 128.0
    v = ycc[..., 2].astype(np.float64) - 128.0
    out = np.stack([y + 1.402 * v, y - 0.344136 * u - 0.714136 * v, y + 1.772 * u], axis=-1)
    out = np.sign(out) * np.floor(np.abs(out) + 0.5)
    return np.clip(out, 0, 255).astype(np.uint8)


def lab_to_rgb(lab: np.ndarray, icc: bool) -> np.ndarray:
    lightness = lab[..., 0].astype(np.float64) * 100.0 / 255.0
    if icc:
        a = lab[..., 1].astype(np.float64) - 128.0
        b = lab[..., 2].astype(np.float64) - 128.0
    else:
        a = lab[..., 1].astype(np.int8).astype(np.float64)
        b = lab[..., 2].astype(np.int8).astype(np.float64)

    fy = (lightness + 16.0) / 116.0
    fx = fy + a / 500.0
    fz = fy - b / 200.0
    delta = 6.0 / 29.0

    def inv(t):
        return np.where(t > delta, t ** 3, 3.0 * delta * delta * (t - 4.0 / 29.0))

    x = 0.96422 * inv(fx)
    y = inv(fy)
    z = 0.82521 * inv(fz)
    linear = np.stack([3.1338561 * x - 1.6168667 * y - 0.4906146 * z,
                       -0.9787684 * x + 1.9161415 * y + 0.0334540 * z,
                       0.0719453 * x - 0.2289914 * y + 1.4052427 * z], axis=-1)
    linear = np.clip(linear, 0.0, 1.0)
    encoded = np.where(linear <= 0.0031308, 12.92 * linear, 1.055 * np.power(linear, 1.0 / 2.4) - 0.055)
    return np.clip(np.floor(encoded * 255.0 + 0.5), 0, 255).astype(np.uint8)


def _photometrics(rec: Recorder) -> None:
    width, height = 15, 9
    r = rng(4300)
    base = {TAG_WIDTH: (LONG, [width]), TAG_LENGTH: (LONG, [height]), TAG_COMPRESSION: (SHORT, [1]),
            TAG_PLANAR: (SHORT, [1]), TAG_ROWS_PER_STRIP: (LONG, [height])}

    cmyk = r.integers(0, 256, (height, width, 4)).astype(np.uint8)
    expected_cmyk = rgba_from_rgb(cmyk_to_rgb(cmyk))
    rec.add("photometric_cmyk8", pillow_tiff(Image.fromarray(cmyk, "CMYK")), [expected_cmyk], "photometric",
            "Separated (CMYK), 8-bit, uncompressed")
    rec.add("photometric_cmyk8_lzw", pillow_tiff(Image.fromarray(cmyk, "CMYK"), compression="tiff_lzw"),
            [expected_cmyk], "photometric", "Separated (CMYK), 8-bit, LZW")

    alpha = r.integers(0, 256, (height, width)).astype(np.uint8)
    cmyka = np.dstack([cmyk, alpha])
    rec.add("photometric_cmyk8_alpha",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [8] * 5), TAG_PHOTOMETRIC: (SHORT, [5]),
                                  TAG_SAMPLES: (SHORT, [5]), TAG_INKSET: (SHORT, [1]), TAG_EXTRA_SAMPLES: (SHORT, [2])},
                         "blocks": [np.ascontiguousarray(cmyka).tobytes()]}]),
            [np.dstack([cmyk_to_rgb(cmyk), alpha])], "photometric",
            "Separated (CMYK) with a fifth unassociated alpha sample")

    cmyk16 = (cmyk.astype(np.uint16) << 8) | cmyk.astype(np.uint16)
    rec.add("photometric_cmyk16",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [16] * 4), TAG_PHOTOMETRIC: (SHORT, [5]),
                                  TAG_SAMPLES: (SHORT, [4]), TAG_INKSET: (SHORT, [1])},
                         "blocks": [np.ascontiguousarray(cmyk16).astype("<u2").tobytes()]}]),
            [expected_cmyk], "photometric", "Separated (CMYK), 16-bit")

    ycc = r.integers(0, 256, (height, width, 3)).astype(np.uint8)
    rec.add("photometric_ycbcr444",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [8, 8, 8]), TAG_PHOTOMETRIC: (SHORT, [6]),
                                  TAG_SAMPLES: (SHORT, [3]), TAG_YCBCR_SUBSAMPLING: (SHORT, [1, 1])},
                         "blocks": [np.ascontiguousarray(ycc).tobytes()]}]),
            [rgba_from_rgb(ycbcr_to_rgb(ycc))], "photometric",
            "YCbCr with 1x1 subsampling, full-range BT.601", match="tolerance", tolerance=1)

    lab = r.integers(0, 256, (height, width, 3)).astype(np.uint8)
    rec.add("photometric_cielab8",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [8, 8, 8]), TAG_PHOTOMETRIC: (SHORT, [8]),
                                  TAG_SAMPLES: (SHORT, [3])}, "blocks": [np.ascontiguousarray(lab).tobytes()]}]),
            [rgba_from_rgb(lab_to_rgb(lab, icc=False))], "photometric",
            "CIE L*a*b* with signed a and b samples, D50 white point", match="tolerance", tolerance=2)

    rec.add("photometric_icclab8",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [8, 8, 8]), TAG_PHOTOMETRIC: (SHORT, [9]),
                                  TAG_SAMPLES: (SHORT, [3])}, "blocks": [np.ascontiguousarray(lab).tobytes()]}]),
            [rgba_from_rgb(lab_to_rgb(lab, icc=True))], "photometric",
            "ICC L*a*b*, whose a and b samples carry an offset of 128", match="tolerance", tolerance=2)

    mask = bilevel(width, height, seed=4301)
    coded = tiff_segments(pillow_tiff(Image.fromarray(mask), compression="group4"))
    rec.add("photometric_mask_g4",
            tiff_write([{"tags": {**base, TAG_BITS: (SHORT, [1]), TAG_PHOTOMETRIC: (SHORT, [4]),
                                  TAG_SAMPLES: (SHORT, [1]), TAG_COMPRESSION: (SHORT, [4])}, "blocks": coded}]),
            [rgba_from_gray((~mask).astype(np.uint8) * 255)], "photometric",
            "Transparency mask (photometric 4), Group 4 coded, imaged like WhiteIsZero")


# ----------------------------------------------------------------------------------------------------
# JPEG-in-TIFF (compression 7)
# ----------------------------------------------------------------------------------------------------

def pillow_rgba(data: bytes) -> np.ndarray:
    """Pillow's own decode of a fixture, used as the reference for the lossy JPEG entries."""
    with Image.open(io.BytesIO(data)) as im:
        return np.dstack([np.array(im.convert("RGB")), np.full(im.size[::-1], 255, np.uint8)])


def _jpeg(rec: Recorder) -> None:
    width, height = 48, 32
    rgb = _rgb_page(width, height, seed=4400)
    grey = (np.arange(height)[:, None] * 5 + np.arange(width)[None, :] * 3) % 256
    grey = grey.astype(np.uint8)

    data = pillow_tiff(Image.fromarray(rgb, "RGB"), compression="jpeg")
    assert TAG_JPEG_TABLES in tiff_tags(data), "expected libtiff to hoist the tables into tag 347"
    rec.add("jpeg_rgb", data, [pillow_rgba(data)], "jpeg",
            "RGB stored as YCbCr JPEG (compression 7) with shared JPEGTables", match="psnr", psnr=40)

    data = pillow_tiff(Image.fromarray(grey, "L"), compression="jpeg")
    rec.add("jpeg_gray", data, [pillow_rgba(data)], "jpeg",
            "Greyscale JPEG-in-TIFF with shared JPEGTables", match="psnr", psnr=40)

    previous = TiffImagePlugin.STRIP_SIZE
    TiffImagePlugin.STRIP_SIZE = 256
    try:
        data = pillow_tiff(Image.fromarray(rgb, "RGB"), compression="jpeg")
    finally:
        TiffImagePlugin.STRIP_SIZE = previous
    strips = len(tiff_segments(data))
    rec.add("jpeg_strips", data, [pillow_rgba(data)], "jpeg",
            f"RGB JPEG-in-TIFF split over {strips} strip(s), each its own JPEG stream", match="psnr", psnr=40)

    # A self-contained segment: a complete JPEG file with its own tables and no JPEGTables tag.
    buf = io.BytesIO()
    Image.fromarray(rgb, "RGB").save(buf, format="JPEG", quality=90, subsampling=0)
    whole = buf.getvalue()
    data = tiff_write([{"tags": {TAG_WIDTH: (LONG, [width]), TAG_LENGTH: (LONG, [height]),
                                 TAG_BITS: (SHORT, [8, 8, 8]), TAG_COMPRESSION: (SHORT, [7]),
                                 TAG_PHOTOMETRIC: (SHORT, [6]), TAG_SAMPLES: (SHORT, [3]),
                                 TAG_PLANAR: (SHORT, [1]), TAG_ROWS_PER_STRIP: (LONG, [height]),
                                 TAG_YCBCR_SUBSAMPLING: (SHORT, [1, 1])},
                        "blocks": [whole]}])
    rec.add("jpeg_selfcontained", data, [pillow_rgba(data)], "jpeg",
            "One strip holding a complete JPEG stream, no JPEGTables tag", match="psnr", psnr=40)

    # Tiles, each coded by libtiff as its own abbreviated stream sharing one JPEGTables block.
    tile_w, tile_h = 16, 16
    tables = None
    blocks = []
    for ty in range(0, height, tile_h):
        for tx in range(0, width, tile_w):
            tile = np.zeros((tile_h, tile_w, 3), np.uint8)
            chunk = rgb[ty:ty + tile_h, tx:tx + tile_w]
            tile[:chunk.shape[0], :chunk.shape[1]] = chunk
            coded = pillow_tiff(Image.fromarray(tile, "RGB"), compression="jpeg")
            found = bytes(tiff_tags(coded)[TAG_JPEG_TABLES][1])
            assert tables is None or tables == found, "tiles must share one table stream"
            tables = found
            blocks.append(tiff_segments(coded)[0])
    data = tiff_write([{"tags": {TAG_WIDTH: (LONG, [width]), TAG_LENGTH: (LONG, [height]),
                                 TAG_BITS: (SHORT, [8, 8, 8]), TAG_COMPRESSION: (SHORT, [7]),
                                 TAG_PHOTOMETRIC: (SHORT, [2]), TAG_SAMPLES: (SHORT, [3]),
                                 TAG_PLANAR: (SHORT, [1]), TAG_JPEG_TABLES: (UNDEFINED, tables),
                                 TAG_TILE_WIDTH: (LONG, [tile_w]), TAG_TILE_LENGTH: (LONG, [tile_h])},
                        "blocks": blocks, "tiled": True}])
    rec.add("jpeg_tiled", data, [pillow_rgba(data)], "jpeg",
            f"RGB JPEG-in-TIFF in {tile_w}x{tile_h} tiles sharing one JPEGTables block", match="psnr", psnr=40)


# ----------------------------------------------------------------------------------------------------
# BigTIFF containers
# ----------------------------------------------------------------------------------------------------

def _bigtiff(rec: Recorder) -> None:
    """The same page as ``layout_chunky_raw`` re-containered as BigTIFF, once per compression this corpus uses.

    Because the raster is the one the layout group already decodes correctly, a decoder that misreads the 64-bit
    directory disagrees with a file of its own rather than only with this generator. The codestreams are produced
    independently of the container: raw and Deflate strips are plain bytes, while the LZW and PackBits strips are
    lifted out of a classic TIFF written by Pillow/libtiff and re-wrapped, since Pillow cannot be made to write
    those as BigTIFF at all (``big_tiff=True`` is ignored on the libtiff path).
    """
    import zlib

    width, height = 40, 28
    rows_per_strip = 7
    rgb = _rgb_page(width, height, seed=4100)
    expected = rgba_from_rgb(rgb)
    chunky = np.ascontiguousarray(rgb).tobytes()
    base = {TAG_WIDTH: (LONG, [width]), TAG_LENGTH: (LONG, [height]), TAG_BITS: (SHORT, [8, 8, 8]),
            TAG_PHOTOMETRIC: (SHORT, [2]), TAG_SAMPLES: (SHORT, [3]), TAG_PLANAR: (SHORT, [1])}

    def libtiff_strips(compression: str) -> tuple[int, list[bytes]]:
        """Compresses the page with Pillow/libtiff into a classic TIFF and hands back its compression code and strips."""
        data = pillow_tiff(Image.fromarray(rgb, "RGB"), compression=compression,
                           tiffinfo={TAG_ROWS_PER_STRIP: rows_per_strip})
        tags = tiff_tags(data)
        assert tags[TAG_ROWS_PER_STRIP][1] == [rows_per_strip], (compression, tags[TAG_ROWS_PER_STRIP])
        return tags[TAG_COMPRESSION][1][0], tiff_segments(data)

    cases = [("bigtiff_none", 1, _strip_blocks(chunky, height, rows_per_strip),
              "uncompressed strips, hand-assembled"),
             ("bigtiff_deflate", 8, _strip_blocks(chunky, height, rows_per_strip, zlib.compress),
              "Deflate strips, hand-assembled")]
    for name, pillow_name, label in (("bigtiff_lzw", "tiff_lzw", "LZW"), ("bigtiff_packbits", "packbits", "PackBits")):
        code, blocks = libtiff_strips(pillow_name)
        cases.append((name, code, blocks, f"{label} strips written by libtiff and re-containered here"))

    for name, code, blocks, notes in cases:
        data = tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [code]),
                                     TAG_ROWS_PER_STRIP: (LONG, [rows_per_strip])}, "blocks": blocks}], bigtiff=True)
        bigtiff_decodes_to(data, expected, name)
        rec.add(name, data, [expected], "bigtiff",
                f"BigTIFF (version 43), little-endian, {rows_per_strip}-row {notes}; StripOffsets and "
                f"StripByteCounts are LONG8 (type 16)")

    data = tiff_write([{"tags": {**base, TAG_COMPRESSION: (SHORT, [8]),
                                 TAG_TILE_WIDTH: (LONG, [16]), TAG_TILE_LENGTH: (LONG, [16])},
                        "blocks": _tile_blocks(rgb, 16, 16, zlib.compress), "tiled": True}], big=True, bigtiff=True)
    bigtiff_decodes_to(data, expected, "bigtiff_mm_tiled")
    rec.add("bigtiff_mm_tiled", data, [expected], "bigtiff",
            "BigTIFF (version 43) in big-endian (MM) byte order: 16x16 Deflate tiles whose TileOffsets and "
            "TileByteCounts are LONG8. Pillow cannot open this arm at all, so tifffile verifies it")


# ----------------------------------------------------------------------------------------------------

SECTIONS = (_ccitt, _layouts, _samples, _photometrics, _jpeg, _bigtiff)


def gen_tiffadv(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    rec = Recorder(out_dir)
    for section in SECTIONS:
        section(rec)
    rec.write_manifest()
    print(f"tiffadv: {len(rec.entries)} fixtures")


if __name__ == "__main__":
    gen_tiffadv(os.path.join(HERE, "tiffadv"))
