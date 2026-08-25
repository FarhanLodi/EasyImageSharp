#!/usr/bin/env python
"""Fixture generator for the WebP codec.

Discovered by generate.py, which calls ``gen_webp(<Fixtures>/webp)``. Layout, as for the other formats:

  <name>.webp           the fixture (encoded by Pillow/libwebp, or muxed here from Pillow-encoded
                        sub-frames; never by this library)
  <name>.rgba           ground truth: Pillow's own decode, width*height*4 bytes of RGBA per frame,
                        row-major, top-left origin, all frames of an animation concatenated
  <name>.expected.png   Pillow-written rendering of the first frame (for eyeballing)
  manifest.json         entries: name, file, width, height, frames, lossless, has_alpha, tolerance,
                        notes, writer, sha256, size; "expect" names the exception type the decoder
                        must throw for the fixtures that are deliberately broken

``tolerance`` is the maximum absolute per-channel difference the decoder may show against the reference
decode: 0 for every lossless fixture (they must be byte-identical) and a small number for the lossy ones,
where the reference decoder's SIMD paths are allowed to round a shade differently from ours.

Pillow's WebP animation encoder only ever writes full-canvas, non-blended, non-disposed frames, so the
frame offsets, alpha blending and dispose-to-background paths are exercised by animations muxed here from
individually Pillow-encoded sub-frames. libwebp still decodes those files, so Pillow remains the oracle.

Everything is derived from fixed constants, so re-running the script produces byte-identical output.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import struct

import numpy as np
from PIL import Image


# --------------------------------------------------------------------------------------------------
# Sources
# --------------------------------------------------------------------------------------------------

def _rgba(rgb: np.ndarray, alpha=255) -> np.ndarray:
    a = np.full(rgb.shape[:2], alpha, np.uint8) if np.isscalar(alpha) else np.asarray(alpha, np.uint8)
    return np.dstack([rgb.astype(np.uint8), a])


def _test_card(w: int = 96, h: int = 72) -> np.ndarray:
    """A 96x72 card: saturated colour bars, smooth ramps, a checkerboard and a disc.

    The bars give the intra predictors hard vertical edges, the ramps give them flat gradients, and the
    checkerboard forces 4x4 luma modes, so a single fixture covers most of the reconstruction paths.
    """
    x = np.arange(w)[None, :]
    y = np.arange(h)[:, None]
    rgb = np.zeros((h, w, 3), np.int64)

    bars = [(255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0),
            (255, 0, 255), (255, 0, 0), (0, 0, 255), (16, 16, 16)]
    third = h // 3
    band = max(1, w // len(bars))
    for i, colour in enumerate(bars):
        rgb[0:third, i * band:(i + 1) * band] = colour

    ramp_x = (x * 255) // max(1, w - 1)
    ramp_y = ((y - third) * 255) // max(1, third - 1)
    rgb[third:2 * third, :, 0] = np.broadcast_to(ramp_x, (h, w))[third:2 * third]
    rgb[third:2 * third, :, 1] = np.clip(np.broadcast_to(ramp_y, (h, w))[third:2 * third], 0, 255)
    rgb[third:2 * third, :, 2] = 255 - np.broadcast_to(ramp_x, (h, w))[third:2 * third]

    check = (((x // 6) + (y // 6)) % 2).astype(np.int64)
    rgb[2 * third:, :, 0] = np.broadcast_to(check * 220 + 20, (h, w))[2 * third:]
    rgb[2 * third:, :, 1] = np.broadcast_to((1 - check) * 200 + 30, (h, w))[2 * third:]
    rgb[2 * third:, :, 2] = np.broadcast_to(((x * 3 + y * 5) % 256), (h, w))[2 * third:]

    cx, cy, r = w * 3 // 4, h * 3 // 4, min(w, h) // 6
    disc = ((x - cx) ** 2 + (y - cy) ** 2) <= r * r
    rgb[disc] = (250, 180, 40)
    return rgb.astype(np.uint8)


def _gradient(w: int, h: int) -> np.ndarray:
    x = np.arange(w)[None, :]
    y = np.arange(h)[:, None]
    r = np.broadcast_to((x * 255) // max(1, w - 1), (h, w))
    g = np.broadcast_to((y * 255) // max(1, h - 1), (h, w))
    b = np.broadcast_to(((x + y) * 7) % 256, (h, w))
    return np.stack([r, g, b], axis=-1).astype(np.uint8)


def _alpha_ramp(w: int, h: int) -> np.ndarray:
    x = np.arange(w)[None, :]
    a = np.broadcast_to((x * 255) // max(1, w - 1), (h, w)).astype(np.uint8).copy()
    a[h // 2, :] = 0
    a[:, 0] = 255
    return a


def _mixed(w: int = 96, h: int = 72) -> np.ndarray:
    """Four very different quadrants, so the encoder needs several prefix-code groups and predictors."""
    x = np.arange(w)[None, :]
    y = np.arange(h)[:, None]
    out = np.zeros((h, w, 3), np.uint8)
    hw, hh = w // 2, h // 2
    out[:hh, :hw] = _noise(hw, hh, 90125)                                  # noise
    out[:hh, hw:, 0] = np.broadcast_to((x * 255) // (w - 1), (h, w))[:hh, hw:]   # horizontal ramp
    out[:hh, hw:, 1] = 40
    out[:hh, hw:, 2] = 200
    out[hh:, :hw, 0] = 12
    out[hh:, :hw, 1] = np.broadcast_to((y * 255) // (h - 1), (h, w))[hh:, :hw]   # vertical ramp
    out[hh:, :hw, 2] = np.broadcast_to((y * 255) // (h - 1), (h, w))[hh:, :hw]
    stripes = (((x - y) // 3) % 2).astype(np.uint8)
    out[hh:, hw:, 0] = np.broadcast_to(stripes * 200 + 30, (h, w))[hh:, hw:]     # diagonal stripes
    out[hh:, hw:, 1] = np.broadcast_to(((x + y) * 2) % 256, (h, w))[hh:, hw:]
    out[hh:, hw:, 2] = np.broadcast_to(stripes * 90 + 100, (h, w))[hh:, hw:]
    out[hh + 4:hh + 20, hw + 4:hw + 20] = (250, 250, 250)                  # flat patch
    return out


def _palette_image(w: int, h: int, colours: list[tuple[int, int, int, int]]) -> np.ndarray:
    """A picture built from exactly ``len(colours)`` colours, which makes libwebp use colour indexing."""
    out = np.zeros((h, w, 4), np.uint8)
    n = len(colours)
    for yy in range(h):
        for xx in range(w):
            out[yy, xx] = colours[((xx // 3) + (yy // 2) * 5 + (xx * yy) // 17) % n]
    return out


def _noise(w: int, h: int, seed: int) -> np.ndarray:
    rng = np.random.default_rng(seed)
    return rng.integers(0, 256, (h, w, 3), dtype=np.uint8)


# --------------------------------------------------------------------------------------------------
# Container helpers (used to mux animations and metadata chunks Pillow will not write)
# --------------------------------------------------------------------------------------------------

def _chunk(fourcc: bytes, payload: bytes) -> bytes:
    out = fourcc + struct.pack("<I", len(payload)) + payload
    return out + b"\0" if len(payload) & 1 else out


def _le24(value: int) -> bytes:
    return struct.pack("<I", value)[:3]


def _riff(body: bytes) -> bytes:
    return b"RIFF" + struct.pack("<I", 4 + len(body)) + b"WEBP" + body


def _split_chunks(data: bytes) -> list[tuple[bytes, int, int]]:
    assert data[:4] == b"RIFF" and data[8:12] == b"WEBP"
    end = min(len(data), 8 + struct.unpack("<I", data[4:8])[0])
    pos, out = 12, []
    while pos + 8 <= end:
        fourcc = data[pos:pos + 4]
        size = struct.unpack("<I", data[pos + 4:pos + 8])[0]
        out.append((fourcc, pos + 8, size))
        pos += 8 + size + (size & 1)
    return out


def _encode(image: np.ndarray, **options) -> bytes:
    buf = io.BytesIO()
    Image.fromarray(image).save(buf, format="WEBP", **options)
    return buf.getvalue()


def _image_chunks(image: np.ndarray, **options) -> bytes:
    """Encodes one image and returns its ALPH (if any) plus its VP8/VP8L chunk, ready to embed in an ANMF."""
    data = _encode(image, **options)
    out = b""
    for fourcc, off, size in _split_chunks(data):
        if fourcc in (b"ALPH", b"VP8 ", b"VP8L"):
            out += _chunk(fourcc, data[off:off + size])
    assert out, "no bitstream chunk produced"
    return out


def _filter_alpha(alpha: np.ndarray, method: int) -> np.ndarray:
    """Applies the forward spatial filter of RFC 9649 section 2.4 (the inverse of what the decoder undoes)."""
    h, w = alpha.shape
    out = np.zeros_like(alpha)
    for y in range(h):
        for x in range(w):
            value = int(alpha[y, x])
            left = int(alpha[y, x - 1]) if x > 0 else None
            top = int(alpha[y - 1, x]) if y > 0 else None
            if method == 0:
                pred = 0
            elif method == 1:                       # horizontal
                pred = left if left is not None else (top if top is not None else 0)
            elif method == 2:                       # vertical
                if y == 0:
                    pred = left if left is not None else 0
                else:
                    pred = top
            else:                                   # gradient
                if y == 0:
                    pred = left if left is not None else 0
                elif x == 0:
                    pred = top
                else:
                    g = left + top - int(alpha[y - 1, x - 1])
                    pred = 0 if g < 0 else 255 if g > 255 else g
            out[y, x] = (value - pred) & 0xff
    return out


def _alph_chunk(alpha: np.ndarray, compression: int, filter_method: int) -> bytes:
    """Builds an ALPH chunk payload: the 1-byte header plus the raw or VP8L-compressed alpha plane."""
    filtered = _filter_alpha(alpha, filter_method)
    header = bytes([(compression & 3) | ((filter_method & 3) << 2)])
    if compression == 0:
        return header + np.ascontiguousarray(filtered).tobytes()

    # Lossless alpha is the green channel of a header-less VP8L image stream. Pillow writes a complete
    # VP8L chunk, whose 5-byte signature/dimension header is a whole number of bytes, so dropping it
    # leaves the entropy-coded remainder byte-aligned exactly as an ALPH chunk needs it.
    h, w = alpha.shape
    image = np.zeros((h, w, 4), np.uint8)
    image[..., 1] = filtered
    image[..., 3] = 255
    data = _encode(image, lossless=True, quality=100, method=4, exact=True)
    vp8l = next(data[off:off + size] for fourcc, off, size in _split_chunks(data) if fourcc == b"VP8L")
    return header + vp8l[5:]


def _mux_lossy_with_alpha(rgb: np.ndarray, alpha: np.ndarray, compression: int, filter_method: int,
                          quality: int = 80) -> bytes:
    """Muxes a Pillow-encoded VP8 key frame together with an ALPH chunk built here."""
    h, w = rgb.shape[:2]
    lossy = _encode(rgb, lossless=False, quality=quality, method=4, exact=True)
    vp8 = next(lossy[off:off + size] for fourcc, off, size in _split_chunks(lossy) if fourcc == b"VP8 ")
    body = _chunk(b"VP8X", bytes([0x10]) + bytes(3) + _le24(w - 1) + _le24(h - 1))
    body += _chunk(b"ALPH", _alph_chunk(alpha, compression, filter_method))
    body += _chunk(b"VP8 ", vp8)
    return _riff(body)


def _mux_animation(canvas: tuple[int, int], frames: list[dict], loop: int = 0, background: int = 0) -> bytes:
    """Builds an animated WebP from explicit frame rectangles, blend and dispose flags."""
    width, height = canvas
    body = _chunk(b"VP8X", bytes([0x12]) + b"\0\0\0" + _le24(width - 1) + _le24(height - 1))
    body += _chunk(b"ANIM", struct.pack("<I", background) + struct.pack("<H", loop))
    for frame in frames:
        image = frame["image"]
        h, w = image.shape[:2]
        flags = (0 if frame.get("blend", True) else 2) | (1 if frame.get("dispose", False) else 0)
        payload = (_le24(frame["x"] // 2) + _le24(frame["y"] // 2) + _le24(w - 1) + _le24(h - 1)
                   + _le24(frame["duration"]) + bytes([flags]))
        payload += _image_chunks(image, **frame["options"])
        body += _chunk(b"ANMF", payload)
    return _riff(body)


# --------------------------------------------------------------------------------------------------
# Recording
# --------------------------------------------------------------------------------------------------

class _Recorder:
    def __init__(self, out_dir: str) -> None:
        self.out_dir = out_dir
        os.makedirs(out_dir, exist_ok=True)
        self.entries: list[dict] = []

    def record(self, name: str, data: bytes, notes: str, writer: str, *, tolerance: int = 0,
               expect: str | None = None, **facts) -> None:
        path = os.path.join(self.out_dir, name + ".webp")
        with open(path, "wb") as fh:
            fh.write(data)
        entry: dict = {"name": name, "file": name + ".webp", "writer": writer, "notes": notes,
                       "sha256": hashlib.sha256(data).hexdigest()[:16], "size": len(data)}
        if expect is not None:
            entry["expect"] = expect
            entry.update(facts)
            self.entries.append(entry)
            return

        _check_container_facts(data, name, facts)
        frames = _decode_frames(path)
        entry["width"] = frames[0].shape[1]
        entry["height"] = frames[0].shape[0]
        entry["frames"] = len(frames)
        entry["tolerance"] = tolerance
        entry.update(facts)
        for f in frames:
            assert f.shape == frames[0].shape, (name, f.shape, frames[0].shape)
        with open(os.path.join(self.out_dir, name + ".rgba"), "wb") as fh:
            for f in frames:
                fh.write(np.ascontiguousarray(f).tobytes())
        Image.fromarray(np.ascontiguousarray(frames[0])).save(os.path.join(self.out_dir, name + ".expected.png"))
        self.entries.append(entry)

    def write_manifest(self) -> None:
        with open(os.path.join(self.out_dir, "manifest.json"), "w", newline="\n") as fh:
            json.dump(self.entries, fh, indent=1)
            fh.write("\n")


def _check_container_facts(data: bytes, name: str, facts: dict) -> None:
    """Cross-checks the declared ``lossless``/``has_alpha`` against what the RIFF container actually says."""
    lossless = None
    has_alpha = False
    for fourcc, off, size in _split_chunks(data):
        if fourcc == b"VP8X":
            has_alpha = (data[off] & 0x10) != 0
        elif fourcc == b"ALPH":
            has_alpha = True
        elif fourcc in (b"VP8 ", b"VP8L") and lossless is None:
            lossless = fourcc == b"VP8L"
            if lossless and not has_alpha:
                bits = int.from_bytes(data[off + 1:off + 5], "little")
                has_alpha = ((bits >> 28) & 1) != 0
        elif fourcc == b"ANMF":
            pos = off + 16
            while pos + 8 <= off + size:
                sub = data[pos:pos + 4]
                sub_size = struct.unpack("<I", data[pos + 4:pos + 8])[0]
                if sub == b"ALPH":
                    has_alpha = True
                elif sub in (b"VP8 ", b"VP8L") and lossless is None:
                    lossless = sub == b"VP8L"
                pos += 8 + sub_size + (sub_size & 1)
    if "lossless" in facts:
        assert facts["lossless"] == bool(lossless), f"{name}: lossless={facts['lossless']} but the container says {lossless}"
    if "has_alpha" in facts:
        assert facts["has_alpha"] == has_alpha, f"{name}: has_alpha={facts['has_alpha']} but the container says {has_alpha}"


def _decode_frames(path: str) -> list[np.ndarray]:
    """Pillow's (libwebp's) own decode of every frame, as composited RGBA."""
    out = []
    with Image.open(path) as im:
        count = getattr(im, "n_frames", 1)
        for i in range(count):
            im.seek(i)
            out.append(np.array(im.convert("RGBA")))
    return out


def _check_lossless(path: str, expected: np.ndarray, what: str) -> None:
    got = _decode_frames(path)[0]
    if not np.array_equal(got, expected):
        raise AssertionError(f"lossless round trip changed pixels for {what}")


# --------------------------------------------------------------------------------------------------
# Fixtures
# --------------------------------------------------------------------------------------------------

def gen_webp(out_dir: str) -> None:
    rec = _Recorder(out_dir)
    card = _test_card()

    # ---- Lossless stills ----------------------------------------------------------------------
    lossless_defaults = dict(lossless=True, exact=True)

    rec.record("ll_testcard_m6", _encode(_rgba(card), quality=100, method=6, **lossless_defaults),
               "96x72 test card, VP8L at method 6 (best compression: predictor + cross-colour transforms)",
               "Pillow", lossless=True, has_alpha=False)
    _check_lossless(os.path.join(out_dir, "ll_testcard_m6.webp"), _rgba(card), "ll_testcard_m6")

    rec.record("ll_testcard_m0", _encode(_rgba(card), quality=0, method=0, **lossless_defaults),
               "96x72 test card, VP8L at method 0 / quality 0 (fewer transforms, more literals)",
               "Pillow", lossless=True, has_alpha=False)

    rec.record("ll_mixed_m6", _encode(_rgba(_mixed()), quality=100, method=6, **lossless_defaults),
               "96x72 of four unrelated quadrants: forces a meta prefix-code image with several groups "
               "and a wide spread of predictor modes",
               "Pillow", lossless=True, has_alpha=False)

    rec.record("ll_mixed_m4", _encode(_rgba(_mixed()), quality=100, method=4, **lossless_defaults),
               "the same four quadrants at method 4, where the encoder keeps a meta prefix-code image "
               "with several Huffman groups instead of merging them into one",
               "Pillow", lossless=True, has_alpha=False)

    rec.record("ll_gradient_rgb", _encode(_rgba(_gradient(64, 48)), quality=90, method=4, **lossless_defaults),
               "64x48 smooth RGB gradient, VP8L; exercises the predictor transform on flat data",
               "Pillow", lossless=True, has_alpha=False)

    alpha = _alpha_ramp(48, 32)
    rec.record("ll_alpha_ramp", _encode(_rgba(_gradient(48, 32), alpha), quality=100, method=4, **lossless_defaults),
               "48x32 RGBA with a horizontal alpha ramp and one fully transparent row",
               "Pillow", lossless=True, has_alpha=True)

    palette16 = [(16 * i, 250 - 15 * i, (37 * i) % 256, 255 if i % 5 else 96) for i in range(16)]
    rec.record("ll_palette16", _encode(_palette_image(40, 30, palette16), quality=100, method=6, **lossless_defaults),
               "40x30 built from 16 colours: colour-indexing transform with two pixels bundled per byte",
               "Pillow", lossless=True, has_alpha=True)

    palette2 = [(0, 0, 0, 255), (255, 255, 255, 255)]
    rec.record("ll_palette2", _encode(_palette_image(37, 21, palette2), quality=100, method=6, **lossless_defaults),
               "37x21 two-colour image: colour indexing with eight pixels bundled per byte, odd width",
               "Pillow", lossless=True, has_alpha=False)

    rec.record("ll_noise", _encode(_rgba(_noise(48, 36, 20260822)), quality=100, method=3, **lossless_defaults),
               "48x36 white noise: almost pure literals, so the prefix codes carry the whole alphabet",
               "Pillow", lossless=True, has_alpha=False)

    flat = np.zeros((64, 64, 3), np.uint8)
    flat[:, :] = (30, 144, 255)
    flat[16:48, 16:48] = (255, 215, 0)
    rec.record("ll_flat_blocks", _encode(_rgba(flat), quality=100, method=6, **lossless_defaults),
               "64x64 two flat rectangles: long backward references with large distances",
               "Pillow", lossless=True, has_alpha=False)

    for w, h in ((1, 1), (3, 2), (17, 9)):
        image = _rgba(_gradient(w, h))
        rec.record(f"ll_{w}x{h}", _encode(image, quality=100, method=4, **lossless_defaults),
                   f"{w}x{h} lossless: smallest and odd sizes", "Pillow", lossless=True, has_alpha=False)
        _check_lossless(os.path.join(out_dir, f"ll_{w}x{h}.webp"), image, f"ll_{w}x{h}")

    # ---- Lossy stills -------------------------------------------------------------------------
    for quality in (50, 80, 95):
        rec.record(f"lossy_testcard_q{quality}",
                   _encode(card, lossless=False, quality=quality, method=4, exact=True),
                   f"96x72 test card, VP8 at quality {quality}: intra 16x16 and 4x4 modes plus the loop filter",
                   "Pillow", tolerance=3, lossless=False, has_alpha=False)

    rec.record("lossy_gradient_q75", _encode(_gradient(64, 48), lossless=False, quality=75, method=4, exact=True),
               "64x48 smooth gradient, VP8 at quality 75: mostly DC/TM prediction with a strong filter",
               "Pillow", tolerance=3, lossless=False, has_alpha=False)

    rec.record("lossy_alpha_q80",
               _encode(_rgba(_gradient(64, 48), _alpha_ramp(64, 48)), lossless=False, quality=80,
                       alpha_quality=100, method=4, exact=True),
               "64x48 lossy RGB with a losslessly compressed ALPH plane (alpha_quality 100, no dithering)",
               "Pillow", tolerance=3, lossless=False, has_alpha=True)

    rec.record("lossy_noise_q60", _encode(_noise(48, 36, 4242), lossless=False, quality=60, method=4, exact=True),
               "48x36 noise, VP8 at quality 60: dense coefficient tokens including the large-value categories",
               "Pillow", tolerance=3, lossless=False, has_alpha=False)

    for w, h in ((1, 1), (3, 2), (17, 9)):
        rec.record(f"lossy_{w}x{h}", _encode(_gradient(w, h), lossless=False, quality=90, method=4, exact=True),
                   f"{w}x{h} lossy: partial macroblocks and single-row/column chroma upsampling",
                   "Pillow", tolerance=3, lossless=False, has_alpha=False)

    # ---- Animations written by Pillow ---------------------------------------------------------
    anim = []
    for k in range(4):
        frame = np.zeros((32, 32, 4), np.uint8)
        frame[..., 0] = 30 + 50 * k
        frame[..., 1] = 200 - 40 * k
        frame[..., 2] = 90 + 20 * k
        frame[..., 3] = 255
        frame[4 + 6 * k:14 + 6 * k, 4:28] = (250, 250, 250, 255)
        anim.append(Image.fromarray(frame))

    buf = io.BytesIO()
    anim[0].save(buf, format="WEBP", save_all=True, append_images=anim[1:], lossless=True, quality=100,
                 method=4, exact=True, duration=[80, 120, 160, 200], loop=0)
    rec.record("anim_lossless", buf.getvalue(),
               "32x32 four-frame lossless animation written by Pillow (full-canvas frames, no blending)",
               "Pillow", lossless=True, has_alpha=True)

    buf = io.BytesIO()
    anim[0].save(buf, format="WEBP", save_all=True, append_images=anim[1:], lossless=False, quality=85,
                 alpha_quality=100, method=4, exact=True, duration=100, loop=3)
    rec.record("anim_lossy", buf.getvalue(),
               "32x32 four-frame lossy animation written by Pillow, loop count 3",
               "Pillow", tolerance=3, lossless=False, has_alpha=False)

    # ---- Animations muxed here (offsets, blending, disposal) -----------------------------------
    lossless_frame = dict(lossless=True, quality=100, method=4, exact=True)
    background = np.zeros((24, 32, 4), np.uint8)
    background[..., :3] = _gradient(32, 24)
    background[..., 3] = 255

    patch = np.zeros((8, 8, 4), np.uint8)
    for yy in range(8):
        for xx in range(8):
            patch[yy, xx] = (20 + 30 * xx, 240 - 20 * yy, 128, (yy * 36) % 256)

    opaque_patch = patch.copy()
    opaque_patch[..., 3] = 255

    muxed = _mux_animation((32, 24), [
        {"image": background, "x": 0, "y": 0, "duration": 100, "blend": False, "dispose": False,
         "options": lossless_frame},
        {"image": opaque_patch, "x": 4, "y": 4, "duration": 100, "blend": False, "dispose": False,
         "options": lossless_frame},
        {"image": opaque_patch, "x": 20, "y": 12, "duration": 100, "blend": False, "dispose": True,
         "options": lossless_frame},
        {"image": background, "x": 0, "y": 0, "duration": 100, "blend": False, "dispose": False,
         "options": lossless_frame},
    ], loop=0)
    rec.record("anim_offsets_dispose", muxed,
               "32x24 canvas, sub-frames at offsets; frame 3 disposes its rectangle to transparent black",
               "muxed here from Pillow-encoded VP8L sub-frames", lossless=True, has_alpha=True)

    muxed = _mux_animation((32, 24), [
        {"image": background, "x": 0, "y": 0, "duration": 60, "blend": False, "dispose": False,
         "options": lossless_frame},
        {"image": patch, "x": 4, "y": 4, "duration": 60, "blend": True, "dispose": False,
         "options": lossless_frame},
        {"image": patch, "x": 6, "y": 6, "duration": 60, "blend": True, "dispose": False,
         "options": lossless_frame},
        {"image": patch, "x": 0, "y": 0, "duration": 60, "blend": True, "dispose": True,
         "options": lossless_frame},
        {"image": patch, "x": 0, "y": 0, "duration": 60, "blend": True, "dispose": False,
         "options": lossless_frame},
    ], loop=5)
    rec.record("anim_blend", muxed,
               "32x24 canvas, translucent sub-frames alpha-blended over the canvas, then a dispose",
               "muxed here from Pillow-encoded VP8L sub-frames", lossless=True, has_alpha=True)

    # ---- ALPH chunk variants: both compression modes and all four filtering methods ---------------
    alph_rgb = _gradient(32, 24)
    alph_alpha = _alpha_ramp(32, 24)
    for compression, filter_method in ((0, 0), (0, 1), (0, 2), (0, 3), (1, 2)):
        kind = "raw" if compression == 0 else "lossless"
        name = f"alph_{kind}_f{filter_method}"
        rec.record(name, _mux_lossy_with_alpha(alph_rgb, alph_alpha, compression, filter_method),
                   f"32x24 lossy RGB with a {kind} ALPH plane pre-filtered with method {filter_method}",
                   "muxed here from a Pillow-encoded VP8 key frame", tolerance=3, lossless=False, has_alpha=True)

    # ---- Extended container with metadata chunks that must be skipped --------------------------
    still = _encode(_rgba(_gradient(40, 24)), quality=100, method=4, **lossless_defaults)
    vp8l = next(still[off:off + size] for fourcc, off, size in _split_chunks(still) if fourcc == b"VP8L")
    body = _chunk(b"VP8X", bytes([0x2c]) + b"\0\0\0" + _le24(39) + _le24(23))
    body += _chunk(b"ICCP", b"\0" * 63)
    body += _chunk(b"ZZZZ", b"an unknown chunk the decoder must skip")
    body += _chunk(b"VP8L", vp8l)
    body += _chunk(b"EXIF", b"II\x2a\x00\x08\x00\x00\x00\x00\x00")
    body += _chunk(b"XMP ", b"<x:xmpmeta xmlns:x='adobe:ns:meta/'></x:xmpmeta>")
    rec.record("vp8x_metadata_skipped", _riff(body),
               "VP8X still with ICCP, EXIF, XMP and an unknown chunk around the bitstream: all skipped",
               "muxed here from a Pillow-encoded VP8L chunk", lossless=True, has_alpha=False)

    # ---- Deliberately broken files ------------------------------------------------------------
    rec.record("bad_truncated_vp8l", still[:len(still) - 12],
               "VP8L bitstream cut short: the entropy decoder runs out of bits",
               "truncated by hand", expect="InvalidImageContentException", lossless=True)

    lossy = _encode(_gradient(48, 32), lossless=False, quality=80, method=4, exact=True)
    rec.record("bad_truncated_vp8", lossy[:len(lossy) - 24],
               "VP8 token partition cut short",
               "truncated by hand", expect="InvalidImageContentException", lossless=False)

    rec.record("bad_no_image_chunk", _riff(_chunk(b"ZZZZ", b"nothing decodable here")),
               "A RIFF/WEBP file whose only chunk carries no image data",
               "assembled by hand", expect="InvalidImageContentException")

    rec.record("bad_chunk_overrun", still[:12] + b"VP8L" + struct.pack("<I", 0x00ffffff) + still[20:],
               "A chunk header claiming far more bytes than the file holds",
               "assembled by hand", expect="InvalidImageContentException")

    rec.write_manifest()
    _write_expected_doc(out_dir, rec.entries)
    _check_budget(out_dir)


def _write_expected_doc(out_dir: str, entries: list[dict]) -> None:
    lines = [
        "# WebP fixtures",
        "",
        "Generated by `gen_webp.py` (Pillow 11 / libwebp). Every `<name>.rgba` is **Pillow's own decode**",
        "of `<name>.webp`, so the library is checked against an independent decoder, never against itself.",
        "For animations the file holds every composited canvas frame, concatenated in display order.",
        "",
        "`tolerance` is the largest absolute per-channel difference the decoder is allowed to show. It is",
        "`0` for every lossless fixture, which must come back byte-identical (composited animations",
        "included). The lossy fixtures are given a tolerance of 3 as headroom for the reference decoder's",
        "SIMD paths rounding a shade differently, but the library currently reproduces all of them exactly",
        "too: the test prints the observed maximum error and PSNR for each fixture.",
        "",
        "| fixture | size | frames | kind | alpha | tol | notes |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for e in entries:
        if "expect" in e:
            continue
        kind = "lossless" if e.get("lossless") else "lossy"
        lines.append(f"| `{e['name']}` | {e['width']}x{e['height']} | {e['frames']} | {kind} | "
                     f"{'yes' if e.get('has_alpha') else 'no'} | {e['tolerance']} | {e['notes']} |")
    lines += ["", "## Files that must fail", "", "| fixture | expected exception | notes |", "| --- | --- | --- |"]
    for e in entries:
        if "expect" in e:
            lines.append(f"| `{e['name']}` | `{e['expect']}` | {e['notes']} |")
    lines.append("")
    with open(os.path.join(out_dir, "EXPECTED.md"), "w", newline="\n") as fh:
        fh.write("\n".join(lines))


def _check_budget(out_dir: str) -> None:
    total = 0
    for name in os.listdir(out_dir):
        size = os.path.getsize(os.path.join(out_dir, name))
        total += size
        assert size < 50 * 1024, f"{name} is {size} bytes, over the 50 KB per-file budget"
    assert total < 500 * 1024, f"webp fixtures total {total} bytes, over the 500 KB budget"
    print(f"  webp: {total / 1024:.1f} KB total")


if __name__ == "__main__":
    gen_webp(os.path.join(os.path.dirname(os.path.abspath(__file__)), "webp"))
