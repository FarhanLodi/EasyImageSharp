#!/usr/bin/env python
"""Fixture generator for the small codecs: TGA, Netpbm (PBM/PGM/PPM/PAM), QOI and ICO/CUR.

Discovered by generate.py, which calls ``gen_smallformats(<Fixtures>/smallformats)``. Four sub-folders
are produced (``tga/``, ``pbm/``, ``qoi/``, ``ico/``), each with the usual layout:

  <name>.<ext>          the fixture (written by Pillow or assembled byte by byte here; never by the library)
  <name>.rgba           ground truth: width*height*4 bytes of RGBA per frame, row-major, top-left origin
  <name>.expected.png   Pillow-written rendering of the first frame (for eyeballing)
  manifest.json         entries: name, file, width, height, frames, frame_sizes, notes, writer, plus a few
                        header facts; "expect" names the exception type the decoder must throw

QOI is not supported by Pillow, so ``qoi_encode`` below is a tiny reference encoder written directly from
the specification (qoiformat.org). It also serves as the byte-for-byte oracle for the library's encoder:
the encoder must reproduce every ``ref_*.qoi`` file exactly from the accompanying ``.rgba`` data.

Everything is derived from fixed seeds so re-running the script produces byte-identical output.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import struct
import zlib

import numpy as np
from PIL import Image


# --------------------------------------------------------------------------------------------------
# Shared helpers (self-contained so this module does not depend on generate.py internals)
# --------------------------------------------------------------------------------------------------

def _rng(seed: int) -> np.random.Generator:
    return np.random.default_rng(seed)


def _gradient_rgb(w: int, h: int, seed: int) -> np.ndarray:
    x = np.arange(w)[None, :]
    y = np.arange(h)[:, None]
    r = (x * 255) // max(1, w - 1)
    g = (y * 255) // max(1, h - 1)
    b = (x * y + _rng(seed).integers(0, 32, (h, w))) % 256
    return np.stack([np.broadcast_to(r, (h, w)), np.broadcast_to(g, (h, w)), b], axis=-1).astype(np.uint8)


def _gradient_gray(w: int, h: int, levels: int, seed: int) -> np.ndarray:
    x = np.arange(w)[None, :]
    y = np.arange(h)[:, None]
    return ((x * 7 + y * 13 + _rng(seed).integers(0, 5, (h, w))) % levels).astype(np.int64)


def _alpha_ramp(w: int, h: int) -> np.ndarray:
    """H x W uint8 alpha: 0 at the left edge to 255 at the right edge, with a fully transparent row."""
    x = np.arange(w)[None, :]
    a = np.broadcast_to((x * 255) // max(1, w - 1), (h, w)).astype(np.uint8).copy()
    a[h // 2, :] = 0
    return a


def _rgba(rgb: np.ndarray, alpha=255) -> np.ndarray:
    a = np.full(rgb.shape[:2], alpha, np.uint8) if np.isscalar(alpha) else np.asarray(alpha, np.uint8)
    return np.dstack([rgb.astype(np.uint8), a])


def _rgba_gray(gray8: np.ndarray, alpha=255) -> np.ndarray:
    g = gray8.astype(np.uint8)
    return _rgba(np.dstack([g, g, g]), alpha)


def _scale_to_8bit(values: np.ndarray, maxval: int) -> np.ndarray:
    """The library maps a sample v in [0, maxval] to round(v * 255 / maxval) (integer arithmetic)."""
    v = values.astype(np.int64)
    return ((v * 255 + maxval // 2) // maxval).astype(np.uint8)


def _ensure_dir(path: str) -> str:
    os.makedirs(path, exist_ok=True)
    return path


def _write(path: str, data: bytes) -> None:
    with open(path, "wb") as fh:
        fh.write(data)


def _write_expected(out_dir: str, name: str, frames: list[np.ndarray]) -> None:
    for f in frames:
        assert f.dtype == np.uint8 and f.ndim == 3 and f.shape[2] == 4, (name, f.shape, f.dtype)
    with open(os.path.join(out_dir, name + ".rgba"), "wb") as fh:
        for f in frames:
            fh.write(np.ascontiguousarray(f).tobytes())
    Image.fromarray(np.ascontiguousarray(frames[0])).save(os.path.join(out_dir, name + ".expected.png"))


def _write_manifest(out_dir: str, entries: list[dict]) -> None:
    with open(os.path.join(out_dir, "manifest.json"), "w", newline="\n") as fh:
        json.dump(entries, fh, indent=1)
        fh.write("\n")


def _pil_verify(path: str, expected: np.ndarray, what: str, strict: bool = True, atol: int = 0, **open_kw) -> None:
    """Cross-checks a fixture with Pillow (an independent decoder) where Pillow implements the feature."""
    try:
        with Image.open(path) as im:
            for k, v in open_kw.items():
                setattr(im, k, v)
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


class _Recorder:
    """Collects manifest entries for one sub-folder and writes fixture + ground truth files."""

    def __init__(self, out_dir: str) -> None:
        self.out_dir = _ensure_dir(out_dir)
        self.entries: list[dict] = []

    def record(self, name: str, ext: str, data: bytes, frames: list[np.ndarray] | None, notes: str, writer: str,
               *, verify: bool = True, atol: int = 0, expect: str | None = None, pil_kw: dict | None = None,
               **facts) -> str:
        path = os.path.join(self.out_dir, name + "." + ext)
        _write(path, data)
        entry: dict = {"name": name, "file": name + "." + ext, "writer": writer, "notes": notes,
                       "sha256": hashlib.sha256(data).hexdigest()[:16], "size": len(data)}
        if expect is not None:
            entry.update({"width": 0, "height": 0, "frames": 0, "expect": expect})
        else:
            assert frames
            _write_expected(self.out_dir, name, frames)
            entry.update({"width": int(frames[0].shape[1]), "height": int(frames[0].shape[0]), "frames": len(frames)})
            if len(frames) > 1:
                entry["frame_sizes"] = [[int(f.shape[1]), int(f.shape[0])] for f in frames]
            if verify:
                _pil_verify(path, frames[0], f"{name}.{ext}", strict=True, atol=atol, **(pil_kw or {}))
        entry.update(facts)
        self.entries.append(entry)
        return path

    def finish(self, label: str) -> None:
        _write_manifest(self.out_dir, self.entries)
        print(f"  {label}: {len(self.entries)} fixtures")


# --------------------------------------------------------------------------------------------------
# TGA
# --------------------------------------------------------------------------------------------------

TGA_FOOTER = b"\0" * 8 + b"TRUEVISION-XFILE.\0"


def _tga_header(image_type: int, width: int, height: int, depth: int, descriptor: int, *, cmap_type: int = 0,
                cmap_first: int = 0, cmap_len: int = 0, cmap_entry: int = 0, id_len: int = 0,
                x_origin: int = 0, y_origin: int = 0) -> bytes:
    return struct.pack("<BBBHHBHHHHBB", id_len, cmap_type, image_type, cmap_first, cmap_len, cmap_entry,
                       x_origin, y_origin, width, height, depth, descriptor)


def _tga_storage_rows(rows_top_down: list[list[bytes]], descriptor: int) -> list[list[bytes]]:
    """Reorders top-down/left-to-right pixel rows into the file's storage order for the descriptor bits."""
    rows = list(rows_top_down)
    if not descriptor & 0x20:      # bottom-up
        rows = rows[::-1]
    if descriptor & 0x10:          # right-to-left
        rows = [r[::-1] for r in rows]
    return rows


def _tga_rle(pixels: list[bytes]) -> bytes:
    """Run-length packs a sequence of equally sized pixels (spec: max 128 per packet)."""
    out = bytearray()
    i = 0
    n = len(pixels)
    while i < n:
        run = 1
        while i + run < n and run < 128 and pixels[i + run] == pixels[i]:
            run += 1
        if run >= 2:
            out.append(0x80 | (run - 1))
            out += pixels[i]
            i += run
            continue
        start = i
        i += 1
        while i < n and i - start < 128 and not (i + 1 < n and pixels[i] == pixels[i + 1]):
            i += 1
        out.append(i - start - 1)
        out += b"".join(pixels[start:i])
    return bytes(out)


def _tga_file(image_type: int, width: int, height: int, depth: int, descriptor: int, rows_top_down: list[list[bytes]],
              *, rle: bool = False, rle_cross_rows: bool = False, cmap: bytes = b"", cmap_first: int = 0,
              cmap_len: int = 0, cmap_entry: int = 0, image_id: bytes = b"", footer: bytes | None = TGA_FOOTER,
              x_origin: int = 0, y_origin: int = 0) -> bytes:
    rows = _tga_storage_rows(rows_top_down, descriptor)
    if rle:
        image_type += 8
        if rle_cross_rows:
            body = _tga_rle([p for r in rows for p in r])
        else:
            body = b"".join(_tga_rle(r) for r in rows)
    else:
        body = b"".join(b"".join(r) for r in rows)
    head = _tga_header(image_type, width, height, depth, descriptor, cmap_type=1 if cmap else 0, cmap_first=cmap_first,
                       cmap_len=cmap_len, cmap_entry=cmap_entry, id_len=len(image_id), x_origin=x_origin, y_origin=y_origin)
    return head + image_id + cmap + body + (footer or b"")


def _pack555(rgb: tuple[int, int, int], top_bit: int = 0) -> bytes:
    r, g, b = rgb
    return struct.pack("<H", (top_bit << 15) | ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3))


def _expand5(v: int) -> int:
    """5-bit channel widened to 8 bits with rounding: round(v * 255 / 31)."""
    return (v * 255 + 15) // 31


def _rgb555_expected(rgb: np.ndarray) -> np.ndarray:
    """What decoding a 5-5-5 pixel built from rgb (via _pack555) yields (both Pillow and the library round)."""
    q = (rgb.astype(np.int64) >> 3)
    return np.vectorize(_expand5)(q).astype(np.uint8)


def gen_tga(out_dir: str) -> None:
    rec = _Recorder(out_dir)

    def pillow(name: str, im: Image.Image, expected: np.ndarray, notes: str, verify: bool = True, **save_kw) -> None:
        buf = io.BytesIO()
        im.save(buf, format="TGA", **save_kw)
        data = buf.getvalue()
        rec.record(name, "tga", data, [expected], notes, "pillow", verify=verify,
                   image_type=data[2], depth=data[16], descriptor=data[17])

    def hand(name: str, data: bytes, expected: np.ndarray | None, notes: str, verify: bool = True, expect=None,
             atol: int = 0, **facts) -> None:
        rec.record(name, "tga", data, None if expected is None else [expected], notes, "hand", verify=verify, atol=atol,
                   expect=expect, image_type=data[2], depth=data[16], descriptor=data[17], **facts)

    # ----- Pillow-written: 8/24/32-bit, raw and RLE, both vertical orientations -----
    rgb = _gradient_rgb(23, 17, seed=101)
    pillow("pil_rgb24_raw_bl", Image.fromarray(rgb), _rgba(rgb), "24-bit truecolor, uncompressed, bottom-left origin",
           orientation=-1)
    pillow("pil_rgb24_rle_tl", Image.fromarray(rgb), _rgba(rgb), "24-bit truecolor, RLE, top-left origin",
           orientation=1, rle=True)
    rgb2 = _gradient_rgb(9, 31, seed=102)
    rgb2[3:6, :] = (10, 200, 30)   # runs of identical pixels
    rgb2[:, 4] = (250, 5, 5)
    pillow("pil_rgb24_rle_bl", Image.fromarray(rgb2), _rgba(rgb2), "24-bit truecolor RLE with long runs, bottom-left",
           orientation=-1, rle=True)

    alpha = _alpha_ramp(23, 17)
    rgba = _rgba(rgb, alpha)
    pillow("pil_rgba32_raw_tl", Image.fromarray(rgba), rgba, "32-bit BGRA, uncompressed, top-left, 8 alpha bits",
           orientation=1)
    pillow("pil_rgba32_rle_bl", Image.fromarray(rgba), rgba, "32-bit BGRA, RLE, bottom-left", orientation=-1, rle=True)

    gray = _gradient_gray(29, 11, 256, seed=103).astype(np.uint8)
    pillow("pil_gray8_raw_bl", Image.fromarray(gray), _rgba_gray(gray), "8-bit grayscale (type 3), uncompressed, bottom-left")
    pillow("pil_gray8_rle_tl", Image.fromarray(gray), _rgba_gray(gray), "8-bit grayscale (type 11), RLE, top-left",
           orientation=1, rle=True)

    la = np.dstack([gray, _alpha_ramp(29, 11)])
    pillow("pil_la16_rle", Image.fromarray(la), _rgba_gray(gray, la[..., 1]), "16-bit grayscale+alpha (type 11), RLE",
           rle=True)

    pal = _rng(104).integers(0, 256, (256, 3), dtype=np.int64).astype(np.uint8)
    pal[0] = 0
    idx = _gradient_gray(19, 13, 256, seed=105).astype(np.uint8)
    pim = Image.fromarray(idx)
    pim.putpalette(pal.astype(np.uint8).tobytes())  # attaching a palette turns the L image into P
    pillow("pil_pal8_raw_bl", pim, _rgba(pal[idx]), "8-bit colour-mapped (type 1), 24-bit map, uncompressed")
    pillow("pil_pal8_rle_tl", pim, _rgba(pal[idx]), "8-bit colour-mapped (type 9), 24-bit map, RLE, top-left",
           orientation=1, rle=True)

    # ----- Hand-assembled: 15/16-bit pixels, colour maps of every entry size, origins, footer/extension -----
    w, h = 21, 9
    rgb16 = _gradient_rgb(w, h, seed=110)
    px = [[_pack555(tuple(int(c) for c in rgb16[y, x])) for x in range(w)] for y in range(h)]
    exp16 = _rgba(_rgb555_expected(rgb16))
    # Pillow floors when widening 5-bit channels while the library rounds, hence atol=1 for every 5-5-5 fixture.
    hand("hand_rgb16_raw_bl", _tga_file(2, w, h, 16, 0x00, px), exp16, "16-bit 5-5-5 truecolor, alpha bits 0 (opaque), uncompressed",
         atol=1)
    hand("hand_rgb16_rle_tl", _tga_file(2, w, h, 16, 0x20, px, rle=True), exp16, "16-bit 5-5-5 truecolor, RLE, top-left", atol=1)
    hand("hand_rgb15_raw_tl", _tga_file(2, w, h, 15, 0x20, px), exp16, "15-bit 5-5-5 truecolor (depth 15), top-left", verify=False)

    # 16-bit with one attribute (alpha) bit declared: bit 15 set = opaque, clear = transparent.
    top = ((np.arange(w)[None, :] + np.arange(h)[:, None]) % 3 != 0).astype(int)
    px_a = [[_pack555(tuple(int(c) for c in rgb16[y, x]), int(top[y, x])) for x in range(w)] for y in range(h)]
    exp16a = _rgba(_rgb555_expected(rgb16), top * 255)
    hand("hand_rgb16_alpha1_rle", _tga_file(2, w, h, 16, 0x01, px_a, rle=True), exp16a,
         "16-bit truecolor with 1 attribute bit: bit 15 set = opaque, clear = transparent (Pillow uses the opposite convention)",
         verify=False)

    # Colour maps: 16-bit entries with a non-zero first-entry index, right-to-left + top origin.
    pal16 = _gradient_rgb(1, 40, seed=111)[:, 0, :]          # 40 entries
    cmap16 = b"".join(_pack555(tuple(int(c) for c in e)) for e in pal16)
    idx16 = _gradient_gray(17, 12, 40, seed=112)
    px = [[bytes([int(idx16[y, x]) + 4]) for x in range(17)] for y in range(12)]
    exp = _rgba(_rgb555_expected(pal16)[idx16])
    hand("hand_pal8_map16_first4_rle_tr", _tga_file(1, 17, 12, 8, 0x30, px, rle=True, cmap=cmap16, cmap_first=4, cmap_len=40,
                                                    cmap_entry=16), exp,
         "8-bit indices into a 40-entry 16-bit map, first entry index 4, RLE, top-right origin (rows stored right-to-left)",
         atol=1, cmap_first=4, cmap_entry=16)

    # 32-bit map entries with alpha, image ID field, footer with extension + developer areas (both skipped).
    pal32 = _gradient_rgb(1, 16, seed=113)[:, 0, :]
    pal32a = (np.arange(16) * 17).astype(np.uint8)
    cmap32 = b"".join(bytes([int(e[2]), int(e[1]), int(e[0]), int(a)]) for e, a in zip(pal32, pal32a))
    idx32 = _gradient_gray(13, 10, 16, seed=114)
    px = [[bytes([int(idx32[y, x])]) for x in range(13)] for y in range(10)]
    exp = _rgba(pal32[idx32], pal32a[idx32])
    ext_area = struct.pack("<H", 495) + b"\0" * 493                     # a minimal 495-byte extension area
    dev_area = b"\x00\x00"                                              # empty developer directory
    body = _tga_file(1, 13, 10, 8, 0x00, px, cmap=cmap32, cmap_len=16, cmap_entry=32, image_id=b"EasyImageSharp fixture",
                     footer=None)
    footer = struct.pack("<II", len(body), len(body) + len(ext_area)) + b"TRUEVISION-XFILE.\0"
    hand("hand_pal8_map32_id_extension", body + ext_area + dev_area + footer, exp,
         "8-bit indices, 32-bit BGRA map entries with alpha, 22-byte image ID, footer pointing at extension + developer areas",
         verify=False, cmap_entry=32, image_id_length=22)

    # 24-bit map entries with 16-bit indices and more than 256 entries.
    pal24 = _gradient_rgb(1, 300, seed=115)[:, 0, :]
    cmap24 = b"".join(bytes([int(e[2]), int(e[1]), int(e[0])]) for e in pal24)
    idx24 = _gradient_gray(11, 14, 300, seed=116)
    px = [[struct.pack("<H", int(idx24[y, x])) for x in range(11)] for y in range(14)]
    exp = _rgba(pal24[idx24])
    hand("hand_pal16idx_map24_300", _tga_file(1, 11, 14, 16, 0x00, px, cmap=cmap24, cmap_len=300, cmap_entry=24), exp,
         "16-bit indices into a 300-entry 24-bit map, uncompressed, bottom-left", verify=False, cmap_entry=24)

    # 32-bit truecolor: descriptor says 0 alpha bits but the data carries alpha; alpha is honoured. Also x/y origin fields set.
    a32 = _alpha_ramp(15, 7)
    rgb32 = _gradient_rgb(15, 7, seed=117)
    px = [[bytes([int(rgb32[y, x, 2]), int(rgb32[y, x, 1]), int(rgb32[y, x, 0]), int(a32[y, x])]) for x in range(15)] for y in range(7)]
    hand("hand_rgba32_alpha0bits_origin", _tga_file(2, 15, 7, 32, 0x00, px, x_origin=5, y_origin=3), _rgba(rgb32, a32),
         "32-bit truecolor with descriptor alpha bits 0 (alpha channel still honoured), non-zero x/y origin, no footer")

    # RLE packets that cross row boundaries (tolerated), 24-bit, top-left.
    rgbx = _gradient_rgb(10, 6, seed=118)
    rgbx[1:4, :] = (7, 7, 7)
    px = [[bytes([int(rgbx[y, x, 2]), int(rgbx[y, x, 1]), int(rgbx[y, x, 0])]) for x in range(10)] for y in range(6)]
    hand("hand_rgb24_rle_crossrow", _tga_file(2, 10, 6, 24, 0x20, px, rle=True, rle_cross_rows=True), _rgba(rgbx),
         "24-bit RLE whose packets span row boundaries (decoded as one pixel stream; Pillow rejects such files)", verify=False)

    # Grayscale, top-right origin (both flip bits), no footer.
    g = _gradient_gray(12, 8, 256, seed=119).astype(np.uint8)
    px = [[bytes([int(g[y, x])]) for x in range(12)] for y in range(8)]
    hand("hand_gray8_topright_nofooter", _tga_file(3, 12, 8, 8, 0x30, px, footer=None), _rgba_gray(g),
         "8-bit grayscale, descriptor 0x30 (top-right origin), no footer: detected purely from the header")

    # 1x1 and a single-row / single-column edge case.
    hand("hand_rgb24_1x1", _tga_file(2, 1, 1, 24, 0x00, [[b"\x10\x20\x30"]]), _rgba(np.array([[[0x30, 0x20, 0x10]]], np.uint8)),
         "1x1 24-bit")

    # ----- Unsupported / malformed -----
    hand("hand_type32_huffman", _tga_header(32, 8, 8, 8, 0) + b"\0" * 64 + TGA_FOOTER, None,
         "image type 32 (Huffman/Delta/RLE colour-mapped) is not supported", expect="NotSupportedException")
    hand("hand_truncated_raw_footer", _tga_header(2, 16, 16, 24, 0) + b"\x11" * 100 + TGA_FOOTER, None,
         "24-bit uncompressed 16x16 needs 768 pixel bytes but only 100 are present (footer makes it detectable)",
         expect="InvalidImageContentException")
    hand("hand_pal8_index_out_of_range", _tga_file(1, 4, 1, 8, 0, [[b"\x00", b"\x01", b"\x09", b"\x02"]], cmap=b"\0" * 12, cmap_len=4,
                                                    cmap_entry=24), None,
         "colour-map index 9 in a 4-entry map", expect="InvalidImageContentException")
    hand("hand_rle_truncated", _tga_header(10, 8, 8, 24, 0x20) + b"\x87\x01\x02\x03" + b"\x05\x00\x00", None,
         "RLE stream ends after 8 of 64 pixels", expect="InvalidImageContentException")

    rec.finish("tga")


# --------------------------------------------------------------------------------------------------
# Netpbm
# --------------------------------------------------------------------------------------------------

def _pbm_pack_bits(bits: np.ndarray) -> bytes:
    """Rows of 0/1 (1 = black) packed MSB first, each row padded to a byte boundary (P4 raster)."""
    return b"".join(np.packbits(row.astype(np.uint8), bitorder="big").tobytes() for row in bits)


def _pbm_plain(magic: str, w: int, h: int, maxval: int | None, tokens: list[str], header_comment: str = "",
               per_line: int = 12) -> bytes:
    head = f"{magic}\n"
    if header_comment:
        head += header_comment
    head += f"{w} {h}\n"
    if maxval is not None:
        head += f"{maxval}\n"
    body = ""
    for i in range(0, len(tokens), per_line):
        body += " ".join(tokens[i:i + per_line]) + "\n"
    return (head + body).encode("ascii")


def _pam(width: int, height: int, depth: int, maxval: int, tupltype: str | None, raster: bytes, extra: str = "") -> bytes:
    head = f"P7\n{extra}WIDTH {width}\nHEIGHT {height}\nDEPTH {depth}\nMAXVAL {maxval}\n"
    if tupltype:
        head += f"TUPLTYPE {tupltype}\n"
    head += "ENDHDR\n"
    return head.encode("ascii") + raster


def gen_pbm(out_dir: str) -> None:
    rec = _Recorder(out_dir)

    def pillow(name: str, ext: str, im: Image.Image, expected: np.ndarray, notes: str, verify: bool = True, atol: int = 0) -> None:
        buf = io.BytesIO()
        im.save(buf, format="PPM")
        data = buf.getvalue()
        rec.record(name, ext, data, [expected], notes, "pillow", verify=verify, atol=atol, magic=data[:2].decode())

    def hand(name: str, ext: str, data: bytes, frames: list[np.ndarray] | None, notes: str, verify: bool = True, atol: int = 0,
             expect=None, **facts) -> None:
        rec.record(name, ext, data, frames, notes, "hand", verify=verify, atol=atol, expect=expect,
                   magic=data[:2].decode("ascii", "replace"), **facts)

    # ----- Pillow-written binary formats -----
    rgb = _gradient_rgb(23, 15, seed=201)
    pillow("pil_p6_rgb", "ppm", Image.fromarray(rgb), _rgba(rgb), "P6 binary RGB, maxval 255")
    gray = _gradient_gray(31, 9, 256, seed=202).astype(np.uint8)
    pillow("pil_p5_gray", "pgm", Image.fromarray(gray), _rgba_gray(gray), "P5 binary grayscale, maxval 255")
    bits = (_gradient_gray(37, 7, 2, seed=203) == 1)
    pillow("pil_p4_bilevel", "pbm", Image.fromarray(np.where(bits, 0, 255).astype(np.uint8)).convert("1"),
           _rgba_gray(np.where(bits, 0, 255)), "P4 packed bilevel 37x7 (rows padded to a byte), 1 = black")
    g16 = (_gradient_gray(19, 11, 65536, seed=204) * 977 % 65536).astype(np.uint16)
    pillow("pil_p5_gray16", "pgm", Image.fromarray(g16), _rgba_gray(_scale_to_8bit(g16, 65535)),
           "P5 16-bit grayscale (maxval 65535, big-endian samples), reduced with rounding", verify=False)

    # ----- Hand-written plain (ASCII) formats with comments everywhere -----
    bw = _gradient_gray(13, 6, 2, seed=210)
    tokens = ["".join(str(int(v)) for v in row) for row in bw]         # digits without separators, one row per line
    data = ("P1\n# a comment right after the magic\n13 # comment between width and height\n6\n" + "\n".join(tokens) + "\n").encode()
    hand("hand_p1_bilevel_comments", "pbm", data, [_rgba_gray(np.where(bw == 1, 0, 255))],
         "P1 plain bilevel with comments inside the header and unseparated digits (1 = black)")

    g4 = _gradient_gray(17, 5, 16, seed=211)
    hand("hand_p2_gray_maxval15", "pgm", _pbm_plain("P2", 17, 5, 15, [str(int(v)) for v in g4.ravel()], "# maxval 15\n"),
         [_rgba_gray(_scale_to_8bit(g4, 15))], "P2 plain grayscale with maxval 15 (values scaled to 8 bits)")

    rgb3 = _gradient_rgb(9, 7, seed=212)
    toks = [str(int(v)) for v in rgb3.ravel()]
    data = _pbm_plain("P3", 9, 7, 255, toks, "# comment\n# another\n").replace(b"255\n", b"255 # trailing comment\n", 1)
    hand("hand_p3_rgb_comments", "ppm", data, [_rgba(rgb3)],
         "P3 plain RGB, comments before the size and after maxval, tabs/newlines as separators")

    rgb10 = (_gradient_rgb(8, 5, seed=213).astype(np.int64) * 4 + 1) % 1024
    hand("hand_p3_rgb_maxval1023", "ppm", _pbm_plain("P3", 8, 5, 1023, [str(int(v)) for v in rgb10.ravel()]),
         [_rgba(_scale_to_8bit(rgb10, 1023))], "P3 plain RGB with maxval 1023 (two-byte range in plain form)", verify=False)

    # ----- Hand-written binary formats -----
    rgb16 = (_gradient_rgb(11, 9, seed=220).astype(np.int64) * 257 + _rng(221).integers(0, 200, (9, 11, 3))) % 65536
    raster = rgb16.astype(">u2").tobytes()
    hand("hand_p6_rgb16", "ppm", f"P6\n11 9\n65535\n".encode() + raster, [_rgba(_scale_to_8bit(rgb16, 65535))],
         "P6 binary RGB with maxval 65535 (16-bit big-endian samples)", verify=False)

    g2 = _gradient_gray(15, 6, 5, seed=222)
    hand("hand_p5_gray_maxval4", "pgm", b"P5 15 6 4 " + g2.astype(np.uint8).tobytes(), [_rgba_gray(_scale_to_8bit(g2, 4))],
         "P5 with maxval 4 and single spaces as the only header whitespace")

    rgbc = _gradient_rgb(6, 4, seed=223)
    data = b"P6\n#comment before width\n6\n#between\n4 # after height\n255\n" + rgbc.tobytes()
    hand("hand_p6_comments", "ppm", data, [_rgba(rgbc)], "P6 with comments between every header token")

    bits = _gradient_gray(9, 5, 2, seed=224)
    hand("hand_p4_9wide", "pbm", b"P4\n9 5\n" + _pbm_pack_bits(bits), [_rgba_gray(np.where(bits == 1, 0, 255))],
         "P4 packed bits, width 9 (7 padding bits per row)")

    # Two images in one stream (allowed by the Netpbm spec); the second has a different size.
    a = _gradient_rgb(5, 4, seed=225)
    b = _gradient_rgb(7, 3, seed=226)
    hand("hand_p6_two_images", "ppm", b"P6\n5 4\n255\n" + a.tobytes() + b"P6\n7 3\n255\n" + b.tobytes(), [_rgba(a), _rgba(b)],
         "two concatenated P6 images (5x4 then 7x3) decode as two frames", verify=False)

    # ----- PAM (P7) -----
    rgb7 = _gradient_rgb(10, 8, seed=230)
    hand("hand_p7_rgb", "pam", _pam(10, 8, 3, 255, "RGB", rgb7.tobytes()), [_rgba(rgb7)], "P7 PAM RGB depth 3", verify=False)
    a7 = _alpha_ramp(10, 8)
    hand("hand_p7_rgb_alpha", "pam", _pam(10, 8, 4, 255, "RGB_ALPHA", _rgba(rgb7, a7).tobytes()), [_rgba(rgb7, a7)],
         "P7 PAM RGB_ALPHA depth 4", verify=False)
    g7 = _gradient_gray(12, 7, 256, seed=231).astype(np.uint8)
    hand("hand_p7_gray_alpha", "pam", _pam(12, 7, 2, 255, "GRAYSCALE_ALPHA", np.dstack([g7, _alpha_ramp(12, 7)]).tobytes()),
         [_rgba_gray(g7, _alpha_ramp(12, 7))], "P7 PAM GRAYSCALE_ALPHA depth 2", verify=False)
    g16 = (_gradient_gray(9, 6, 65536, seed=232) * 4099 % 65536).astype(np.uint16)
    hand("hand_p7_gray16_comment", "pam", _pam(9, 6, 1, 65535, "GRAYSCALE", g16.astype(">u2").tobytes(), extra="# comment line\n"),
         [_rgba_gray(_scale_to_8bit(g16, 65535))], "P7 PAM GRAYSCALE with maxval 65535 and a comment in the header", verify=False)
    bw7 = _gradient_gray(11, 4, 2, seed=233)
    hand("hand_p7_blackandwhite", "pam", _pam(11, 4, 1, 1, "BLACKANDWHITE", bw7.astype(np.uint8).tobytes()),
         [_rgba_gray(bw7 * 255)], "P7 PAM BLACKANDWHITE (maxval 1; unlike PBM, 1 = white)", verify=False)
    hand("hand_p7_notupltype_depth3", "pam", _pam(10, 8, 3, 255, None, rgb7.tobytes()), [_rgba(rgb7)],
         "P7 PAM without TUPLTYPE: depth 3 implies RGB", verify=False)

    # ----- Unsupported / malformed -----
    hand("hand_p7_depth5", "pam", _pam(4, 4, 5, 255, "CMYK_ALPHA", b"\0" * 80), None, "P7 with DEPTH 5 (arbitrary tuples) is not supported",
         expect="NotSupportedException")
    hand("hand_p6_truncated", "ppm", b"P6\n10 10\n255\n" + b"\x80" * 100, None, "P6 raster shorter than width*height*3",
         expect="InvalidImageContentException")
    hand("hand_p2_maxval0", "pgm", b"P2\n3 3\n0\n0 0 0 0 0 0 0 0 0\n", None, "maxval 0 is invalid", expect="InvalidImageContentException")
    hand("hand_p3_value_over_maxval", "ppm", b"P3\n2 1\n100\n1 2 3 200 5 6\n", None, "sample 200 exceeds maxval 100",
         expect="InvalidImageContentException")
    hand("hand_p5_maxval70000", "pgm", b"P5\n2 2\n70000\n" + b"\0" * 8, None, "maxval above 65535 is invalid",
         expect="InvalidImageContentException")

    rec.finish("pbm")


# --------------------------------------------------------------------------------------------------
# QOI (reference encoder written from the specification at qoiformat.org)
# --------------------------------------------------------------------------------------------------

def qoi_encode(rgba: np.ndarray, channels: int, colorspace: int = 0) -> bytes:
    """Encodes H x W x 4 uint8 pixels exactly like the reference qoi.h encoder.

    With channels == 3 the alpha channel is ignored (treated as 255 throughout, so QOI_OP_RGBA never occurs).
    """
    h, w = rgba.shape[:2]
    out = bytearray(b"qoif" + struct.pack(">IIBB", w, h, channels, colorspace))
    index = [(0, 0, 0, 0)] * 64
    prev = (0, 0, 0, 255)
    run = 0
    flat = rgba.reshape(-1, 4)
    n = flat.shape[0]
    for i in range(n):
        r, g, b, a = (int(v) for v in flat[i])
        if channels == 3:
            a = 255
        px = (r, g, b, a)
        if px == prev:
            run += 1
            if run == 62 or i == n - 1:
                out.append(0xC0 | (run - 1))
                run = 0
        else:
            if run > 0:
                out.append(0xC0 | (run - 1))
                run = 0
            pos = (r * 3 + g * 5 + b * 7 + a * 11) % 64
            if index[pos] == px:
                out.append(pos)                                    # QOI_OP_INDEX
            else:
                index[pos] = px
                if a == prev[3]:
                    def s8(v: int) -> int:                         # wrap to signed 8-bit like a C signed char
                        v &= 0xFF
                        return v - 256 if v > 127 else v
                    vr, vg, vb = s8(r - prev[0]), s8(g - prev[1]), s8(b - prev[2])
                    vg_r, vg_b = s8(vr - vg), s8(vb - vg)
                    if -3 < vr < 2 and -3 < vg < 2 and -3 < vb < 2:
                        out.append(0x40 | ((vr + 2) << 4) | ((vg + 2) << 2) | (vb + 2))          # QOI_OP_DIFF
                    elif -9 < vg_r < 8 and -33 < vg < 32 and -9 < vg_b < 8:
                        out.append(0x80 | (vg + 32))                                              # QOI_OP_LUMA
                        out.append(((vg_r + 8) << 4) | (vg_b + 8))
                    else:
                        out += bytes([0xFE, r, g, b])                                             # QOI_OP_RGB
                else:
                    out += bytes([0xFF, r, g, b, a])                                              # QOI_OP_RGBA
        prev = px
    out += b"\0" * 7 + b"\x01"
    return bytes(out)


def qoi_decode(data: bytes) -> tuple[np.ndarray, int, int]:
    """Independent reference decoder (used only to self-check the encoder above)."""
    assert data[:4] == b"qoif"
    w, h, channels, colorspace = struct.unpack(">IIBB", data[4:14])
    out = np.zeros((h * w, 4), np.uint8)
    index = [(0, 0, 0, 0)] * 64
    px = (0, 0, 0, 255)
    p = 14
    run = 0
    for i in range(h * w):
        if run > 0:
            run -= 1
        else:
            b1 = data[p]
            p += 1
            if b1 == 0xFE:
                px = (data[p], data[p + 1], data[p + 2], px[3]); p += 3
            elif b1 == 0xFF:
                px = (data[p], data[p + 1], data[p + 2], data[p + 3]); p += 4
            elif b1 >> 6 == 0:
                px = index[b1]
            elif b1 >> 6 == 1:
                px = ((px[0] + ((b1 >> 4) & 3) - 2) & 255, (px[1] + ((b1 >> 2) & 3) - 2) & 255, (px[2] + (b1 & 3) - 2) & 255, px[3])
            elif b1 >> 6 == 2:
                b2 = data[p]; p += 1
                vg = (b1 & 0x3F) - 32
                px = ((px[0] + vg - 8 + ((b2 >> 4) & 0x0F)) & 255, (px[1] + vg) & 255, (px[2] + vg - 8 + (b2 & 0x0F)) & 255, px[3])
            else:
                run = b1 & 0x3F
            index[(px[0] * 3 + px[1] * 5 + px[2] * 7 + px[3] * 11) % 64] = px
        out[i] = px
    assert data[p:p + 8] == b"\0" * 7 + b"\x01", "end marker"
    return out.reshape(h, w, 4), channels, colorspace


def gen_qoi(out_dir: str) -> None:
    rec = _Recorder(out_dir)

    def ref(name: str, rgba: np.ndarray, channels: int, notes: str, colorspace: int = 0) -> None:
        data = qoi_encode(rgba, channels, colorspace)
        expected = rgba.copy()
        if channels == 3:
            expected[..., 3] = 255
        decoded, ch, cs = qoi_decode(data)
        assert np.array_equal(decoded, expected) and ch == channels and cs == colorspace, name
        rec.record(name, "qoi", data, [expected], notes, "reference-encoder", verify=False, channels=channels,
                   colorspace=colorspace)

    def hand(name: str, data: bytes, notes: str, expect: str) -> None:
        rec.record(name, "qoi", data, None, notes, "hand", verify=False, expect=expect)

    rgb = _gradient_rgb(29, 21, seed=301)
    ref("ref_rgb_gradient", _rgba(rgb), 3, "3-channel gradient: DIFF/LUMA/RGB heavy")
    ref("ref_rgb_gradient_as4", _rgba(rgb), 4, "same gradient declared with 4 channels (opaque alpha never emits RGBA ops)")
    ref("ref_rgba_alpha", _rgba(rgb, _alpha_ramp(29, 21)), 4, "4-channel with an alpha ramp: RGBA ops when alpha changes")

    flat = np.zeros((11, 200, 4), np.uint8)
    flat[..., 3] = 255
    flat[3:6, :] = (30, 60, 90, 255)
    flat[8, 150:] = (200, 10, 10, 128)
    ref("ref_runs", flat, 4, "long runs (>62 pixels; run split at 62), run at end of image, alpha change mid-row")

    pal = _rng(302).integers(0, 256, (7, 4), dtype=np.int64).astype(np.uint8)
    pal[:, 3] = 255
    pal[2, 3] = 40
    idx = _rng(303).integers(0, 7, (13, 17))
    ref("ref_index_palette", pal[idx], 4, "7 distinct colours in random order: INDEX ops dominate; includes a translucent colour")

    ref("ref_1x1", np.array([[[12, 34, 56, 78]]], np.uint8), 4, "single RGBA pixel")
    ref("ref_1x1_rgb", np.array([[[0, 0, 0, 255]]], np.uint8), 3, "single pixel equal to the initial state (a run of 1)")

    noise = _rng(304).integers(0, 256, (7, 9, 4), dtype=np.int64).astype(np.uint8)
    ref("ref_noise_linear", noise, 4, "random RGBA noise, colourspace 1 (all channels linear)", colorspace=1)

    wide = np.zeros((1, 300, 4), np.uint8)
    wide[..., :3] = 77
    wide[..., 3] = 255
    wide[0, 100:103] = (78, 77, 76, 255)
    ref("ref_wide_1row", wide, 3, "1x300: runs of 100/197 pixels around three tiny DIFF pixels")

    mix = _gradient_rgb(23, 19, seed=305).astype(np.int64)
    mix = np.clip(mix + _rng(306).integers(-40, 40, mix.shape), 0, 255).astype(np.uint8)
    mix[5:9, 3:20] = (250, 5, 60)
    ref("ref_mixed_ops", _rgba(mix, np.broadcast_to(np.where(np.arange(19)[:, None] % 4 == 0, 90, 255), (19, 23))), 4,
        "every op type appears")

    hand("hand_truncated", qoi_encode(_rgba(rgb), 3)[:-40], "chunk stream ends before every pixel is decoded",
         expect="InvalidImageContentException")
    hand("hand_bad_channels", b"qoif" + struct.pack(">IIBB", 2, 2, 5, 0) + b"\0" * 20, "channels byte 5 is invalid",
         expect="InvalidImageContentException")
    hand("hand_missing_end_marker", qoi_encode(_rgba(rgb), 3)[:-8], "no end marker after the last pixel",
         expect="InvalidImageContentException")
    hand("hand_zero_width", b"qoif" + struct.pack(">IIBB", 0, 4, 3, 0) + b"\0" * 7 + b"\x01", "width 0 is invalid",
         expect="InvalidImageContentException")

    rec.finish("qoi")


# --------------------------------------------------------------------------------------------------
# ICO / CUR
# --------------------------------------------------------------------------------------------------

def _pad4(row: bytes) -> bytes:
    return row + b"\0" * ((4 - len(row) % 4) % 4)


def _ico_dib(width: int, height: int, bpp: int, xor_rows_top_down: list[bytes], and_bits_top_down: np.ndarray | None,
             palette_bgra: bytes = b"", colors_used: int = 0, compression: int = 0, height_doubled: bool = True) -> bytes:
    xor = b"".join(_pad4(r) for r in xor_rows_top_down[::-1])
    if and_bits_top_down is None:
        and_data = b""
    else:
        and_data = b"".join(_pad4(np.packbits(row.astype(np.uint8), bitorder="big").tobytes()) for row in and_bits_top_down[::-1])
    header = struct.pack("<IiiHHIIiiII", 40, width, height * (2 if height_doubled else 1), 1, bpp, compression,
                         len(xor) + len(and_data), 0, 0, colors_used, 0)
    return header + palette_bgra + xor + and_data


def _ico_file(entries: list[tuple[int, int, int, int, int, bytes]], cursor: bool = False) -> bytes:
    """entries: (dir_width, dir_height, colour_count, planes|hotspot_x, bpp|hotspot_y, image_bytes)."""
    count = len(entries)
    offset = 6 + 16 * count
    head = struct.pack("<HHH", 0, 2 if cursor else 1, count)
    directory = b""
    blobs = b""
    for (w, h, colors, a, b, data) in entries:
        directory += struct.pack("<BBBBHHII", w, h, colors, 0, a, b, len(data), offset + len(blobs))
        blobs += data
    return head + directory + blobs


def _png_bytes(rgba: np.ndarray) -> bytes:
    buf = io.BytesIO()
    Image.fromarray(np.ascontiguousarray(rgba)).save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def _bgr_rows(rgb: np.ndarray) -> list[bytes]:
    return [row[:, ::-1].astype(np.uint8).tobytes() for row in rgb]


def _bgra_rows(rgba: np.ndarray) -> list[bytes]:
    return [np.dstack([row[:, 2], row[:, 1], row[:, 0], row[:, 3]]).astype(np.uint8).tobytes() for row in rgba]


def _index_rows(idx: np.ndarray, bpp: int) -> list[bytes]:
    rows = []
    for row in idx:
        if bpp == 8:
            rows.append(row.astype(np.uint8).tobytes())
        elif bpp == 4:
            r = list(int(v) for v in row) + [0] * (len(row) % 2)
            rows.append(bytes((r[i] << 4) | r[i + 1] for i in range(0, len(r), 2)))
        else:
            rows.append(np.packbits(row.astype(np.uint8), bitorder="big").tobytes())
    return rows


def _palette_bgra(pal: np.ndarray) -> bytes:
    return b"".join(bytes([int(e[2]), int(e[1]), int(e[0]), 0]) for e in pal)


def gen_ico(out_dir: str) -> None:
    rec = _Recorder(out_dir)

    def pillow_ico(name: str, im: Image.Image, notes: str, sizes: list[tuple[int, int]], bitmap_format: str | None) -> None:
        buf = io.BytesIO()
        kw = {"sizes": sizes}
        if bitmap_format:
            kw["bitmap_format"] = bitmap_format
        im.save(buf, format="ICO", **kw)
        data = buf.getvalue()
        # Ground truth = Pillow's own reading of every entry, in directory order.
        frames = []
        with Image.open(io.BytesIO(data)) as ico:
            for (w, h) in _ico_dir_sizes(data):
                ico.size = (w, h)
                ico.load()
                frames.append(np.array(ico.convert("RGBA")))
        rec.record(name, "ico", data, frames, notes, "pillow", verify=False, entry_format=bitmap_format or "png",
                   entries=len(frames))

    def hand(name: str, ext: str, data: bytes, frames: list[np.ndarray] | None, notes: str, verify: bool = True,
             expect=None, atol: int = 0, **facts) -> None:
        rec.record(name, ext, data, frames, notes, "hand", verify=verify, expect=expect, atol=atol, **facts)

    # ----- Pillow-written -----
    rgb = _gradient_rgb(32, 32, seed=401)
    rgba = _rgba(rgb, _alpha_ramp(32, 32))
    src = Image.fromarray(rgba)
    pillow_ico("pil_bmp32_single", src, "single 32x32 entry stored as a 32-bit BMP DIB with alpha + (empty) AND mask",
               [(32, 32)], "bmp")
    pillow_ico("pil_png_multi", src, "three PNG entries (16, 24, 32 px) downscaled by Pillow", [(16, 16), (24, 24), (32, 32)], None)
    pillow_ico("pil_bmp_multi", src, "three 32-bit BMP entries (16, 24, 32 px)", [(16, 16), (24, 24), (32, 32)], "bmp")

    # ----- Hand-assembled BMP entries with meaningful AND masks -----
    w, h = 20, 13
    rgb24 = _gradient_rgb(w, h, seed=410)
    mask = (_gradient_gray(w, h, 4, seed=411) == 0)          # True = transparent
    exp = _rgba(rgb24, np.where(mask, 0, 255))
    dib = _ico_dib(w, h, 24, _bgr_rows(rgb24), mask)
    hand("hand_bmp24_andmask", "ico", _ico_file([(w, h, 0, 1, 24, dib)]), [exp],
         "24-bit DIB 20x13 (padded rows) with an AND mask marking transparent pixels", bpp=24)

    pal8 = _rng(412).integers(0, 256, (256, 3), dtype=np.int64).astype(np.uint8)
    idx8 = _gradient_gray(w, h, 256, seed=413)
    dib = _ico_dib(w, h, 8, _index_rows(idx8, 8), mask, _palette_bgra(pal8))
    hand("hand_bmp8_pal_andmask", "ico", _ico_file([(w, h, 0, 1, 8, dib)]), [_rgba(pal8[idx8], np.where(mask, 0, 255))],
         "8-bit palette DIB (256 entries) with AND mask", bpp=8)

    pal4 = _rng(414).integers(0, 256, (16, 3), dtype=np.int64).astype(np.uint8)
    idx4 = _gradient_gray(w, h, 16, seed=415)
    dib = _ico_dib(w, h, 4, _index_rows(idx4, 4), mask, _palette_bgra(pal4))
    hand("hand_bmp4_pal_andmask", "ico", _ico_file([(w, h, 16, 1, 4, dib)]), [_rgba(pal4[idx4], np.where(mask, 0, 255))],
         "4-bit palette DIB (odd width nibble packing) with AND mask", bpp=4)

    pal1 = np.array([[20, 40, 60], [250, 240, 230]], np.uint8)
    idx1 = _gradient_gray(w, h, 2, seed=416)
    dib = _ico_dib(w, h, 1, _index_rows(idx1, 1), mask, _palette_bgra(pal1), colors_used=2)
    hand("hand_bmp1_pal_andmask", "ico", _ico_file([(w, h, 2, 1, 1, dib)]), [_rgba(pal1[idx1], np.where(mask, 0, 255))],
         "1-bit palette DIB (biClrUsed 2) with AND mask", bpp=1)

    pal8s = _rng(417).integers(0, 256, (10, 3), dtype=np.int64).astype(np.uint8)
    idx8s = _gradient_gray(9, 7, 10, seed=418)
    dib = _ico_dib(9, 7, 8, _index_rows(idx8s, 8), np.zeros((7, 9), bool), _palette_bgra(pal8s), colors_used=10)
    hand("hand_bmp8_clrused10", "ico", _ico_file([(9, 7, 10, 1, 8, dib)]), [_rgba(pal8s[idx8s])],
         "8-bit DIB with biClrUsed 10 (short palette), all-opaque AND mask", bpp=8)

    # 16-bit 5-5-5 DIB.
    rgb16 = _gradient_rgb(11, 6, seed=419)
    rows = [b"".join(_pack555(tuple(int(c) for c in rgb16[y, x])) for x in range(11)) for y in range(6)]
    m16 = np.zeros((6, 11), bool)
    m16[0, :] = True
    dib = _ico_dib(11, 6, 16, rows, m16)
    hand("hand_bmp16_555", "ico", _ico_file([(11, 6, 0, 1, 16, dib)]), [_rgba(_rgb555_expected(rgb16), np.where(m16, 0, 255))],
         "16-bit 5-5-5 DIB with the top row masked out", atol=1, bpp=16)

    # 32-bit with real alpha (AND mask ignored) and 32-bit with all-zero alpha (AND mask applied).
    rgb32 = _gradient_rgb(w, h, seed=420)
    a32 = _alpha_ramp(w, h)
    dib = _ico_dib(w, h, 32, _bgra_rows(_rgba(rgb32, a32)), np.ones((h, w), bool))
    hand("hand_bmp32_alpha_maskignored", "ico", _ico_file([(w, h, 0, 1, 32, dib)]), [_rgba(rgb32, a32)],
         "32-bit DIB with a real alpha channel: the (all-set) AND mask is ignored", bpp=32)
    dib = _ico_dib(w, h, 32, _bgra_rows(_rgba(rgb32, 0)), mask)
    hand("hand_bmp32_zeroalpha_andmask", "ico", _ico_file([(w, h, 0, 1, 32, dib)]), [_rgba(rgb32, np.where(mask, 0, 255))],
         "32-bit DIB whose alpha bytes are all zero: treated as opaque and the AND mask decides transparency (Pillow reports alpha 0)",
         verify=False, bpp=32)

    # Directory says 16x16 but the DIB is 20x13: the DIB wins. Also a PNG entry that the directory calls 0x0 (=256).
    dib = _ico_dib(w, h, 24, _bgr_rows(rgb24), mask)
    hand("hand_dir_size_mismatch", "ico", _ico_file([(16, 16, 0, 1, 24, dib)]), [exp],
         "directory entry claims 16x16 while the DIB is 20x13; the embedded bitmap's size is used", verify=False, bpp=24)

    # PNG entry hand-embedded, and a mixed PNG + BMP file.
    png_rgba = _rgba(_gradient_rgb(24, 24, seed=421), _alpha_ramp(24, 24))
    png = _png_bytes(png_rgba)
    hand("hand_png_entry", "ico", _ico_file([(24, 24, 0, 1, 32, png)]), [png_rgba], "single PNG entry (24x24 RGBA)", entry_format="png")
    hand("hand_mixed_png_bmp", "ico", _ico_file([(24, 24, 0, 1, 32, png), (w, h, 0, 1, 24, _ico_dib(w, h, 24, _bgr_rows(rgb24), mask))]),
         [png_rgba, exp], "PNG entry followed by a 24-bit BMP entry; frames in directory order", verify=False,
         entry_format="mixed")

    # A DIB whose height is not doubled (no AND mask at all): tolerated, all opaque.
    dib = _ico_dib(9, 7, 24, _bgr_rows(_gradient_rgb(9, 7, seed=422)), None, height_doubled=False)
    hand("hand_bmp24_nomask_singleheight", "ico", _ico_file([(9, 7, 0, 1, 24, dib)]), [_rgba(_gradient_rgb(9, 7, seed=422))],
         "24-bit DIB with a single (non-doubled) height and no AND mask", verify=False, bpp=24)

    # CUR with a hotspot; entry is a 24-bit DIB with mask.
    hand("hand_cur_hotspot", "cur", _ico_file([(w, h, 0, 5, 7, _ico_dib(w, h, 24, _bgr_rows(rgb24), mask))], cursor=True), [exp],
         "CUR (type 2) with hotspot (5,7); hotspots are not surfaced by the decoder yet", verify=False, hotspot=[5, 7])
    hand("hand_cur_png_hotspot", "cur", _ico_file([(24, 24, 0, 3, 4, png)], cursor=True), [png_rgba],
         "CUR with a PNG entry and hotspot (3,4)", verify=False, hotspot=[3, 4], entry_format="png")

    # ----- Unsupported / malformed -----
    hand("hand_bitfields_unsupported", "ico", _ico_file([(8, 8, 0, 1, 32, _ico_dib(8, 8, 32, _bgra_rows(np.zeros((8, 8, 4), np.uint8)),
                                                                                    None, compression=3))]), None,
         "DIB with BI_BITFIELDS compression is not supported", expect="NotSupportedException")
    hand("hand_truncated_dib", "ico", _ico_file([(w, h, 0, 1, 24, _ico_dib(w, h, 24, _bgr_rows(rgb24), mask)[:200])]), None,
         "24-bit DIB truncated in the XOR data", expect="InvalidImageContentException")
    hand("hand_offset_out_of_range", "ico", struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 8, 8, 0, 0, 1, 24, 400, 5000), None,
         "entry offset beyond the end of the file", expect="InvalidImageContentException")
    hand("hand_png_entry_corrupt", "ico", _ico_file([(24, 24, 0, 1, 32, png[:60])]), None, "PNG entry truncated after IHDR",
         expect="InvalidImageContentException")

    rec.finish("ico")


def _ico_dir_sizes(data: bytes) -> list[tuple[int, int]]:
    count = struct.unpack_from("<H", data, 4)[0]
    sizes = []
    for i in range(count):
        w, h = struct.unpack_from("<BB", data, 6 + 16 * i)
        sizes.append((w or 256, h or 256))
    return sizes


# --------------------------------------------------------------------------------------------------
# Entry point used by generate.py
# --------------------------------------------------------------------------------------------------

def gen_smallformats(out_dir: str) -> None:
    _ensure_dir(out_dir)
    gen_tga(os.path.join(out_dir, "tga"))
    gen_pbm(os.path.join(out_dir, "pbm"))
    gen_qoi(os.path.join(out_dir, "qoi"))
    gen_ico(os.path.join(out_dir, "ico"))


if __name__ == "__main__":
    gen_smallformats(os.path.join(os.path.dirname(os.path.abspath(__file__)), "smallformats"))
