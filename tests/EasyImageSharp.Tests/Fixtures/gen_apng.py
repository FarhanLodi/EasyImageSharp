#!/usr/bin/env python
"""Fixture generator for APNG (animated PNG).

Discovered by generate.py, which calls ``gen_apng(<Fixtures>/apng)``. Three ways to run it:

    python generate.py                    # regenerates Fixtures/apng/ along with every other format
    python gen_apng.py                    # regenerates Fixtures/apng/ on its own
    python gen_apng.py --verify <dir>     # decodes APNGs the library's encoder wrote and compares them

Layout per fixture, as for the other formats:

  <name>.png            the fixture, hand-assembled chunk by chunk here or written by Pillow/libpng
  <name>.rgba           ground truth: width*height*4 bytes of RGBA, row-major, top-left origin, one
                        block per *animation* frame, all frames concatenated in display order
  <name>.expected.png   Pillow-written rendering of the first composited frame (for eyeballing)
  manifest.json         list of entries (see below)
  EXPECTED.md           the same contract in prose

Ground truth comes in three independent layers, none of which is EasyImageSharp:

1. Fixture bytes. ``_apng_encode`` hand-assembles the container from IHDR/acTL/fcTL/IDAT/fdAT/IEND with
   explicit control of every fcTL field and every sequence number - the only way to express the malformed
   corpus and the exotic dispose x blend combinations Pillow will not emit. ``_pillow_encode`` writes the
   plain shapes with Pillow 11 / libpng so part of the corpus comes from a genuinely independent encoder.

2. Expected pixels. ``_composite`` is a NumPy compositor written from the APNG specification's own
   definitions of APNG_DISPOSE_OP_NONE/BACKGROUND/PREVIOUS and APNG_BLEND_OP_SOURCE/OVER. It never imports
   the library and never calls Pillow. Its inputs are recovered from the fixture bytes by ``_parse_apng``,
   a small independent PNG/APNG reader in this file, so the manifest's rectangles, delays, disposal and
   blend values are read back out of the file rather than assumed. Rounding is ``np.floor(v + 0.5)`` to
   match FrameOps.ToRgba32's ``(int)(v + 0.5f)`` - explicitly NOT np.round, whose banker's rounding would
   disagree on exact halves. The canvas is 8-bit between frames, exactly as the decoder's Rgba32 canvas is.

3. Third-party cross-check, gated by a predicate computed from the frame data rather than hand-set.
   ``_pillow_trustworthy`` returns False when Pillow's APNG decoder is known to disagree with the spec:

     * Pillow 11.3 implements APNG_BLEND_OP_OVER as ``Image.paste(src, box, mask=src)``, a straight lerp
       that also lerps the alpha channel, so it is wrong for any source pixel with 0 < alpha < 255.
       Measured: green (0,255,0,128) OVER opaque red gives Pillow [127,128,0,191] where the spec gives
       [127,128,0,255]; the same pixel OVER a transparent canvas gives Pillow [0,128,0,64] where the spec
       gives [0,255,0,128]. Encoding Pillow's answer as the expectation would build the library wrong.
     * Pillow reduces 16-bit samples by scaling, where the library narrows to the high byte.
     * Pillow cannot read an interlaced fdAT frame at all: seeking to one raises TypeError out of its zip
       decoder, even though it reads the identical Adam7 sub-image correctly when it arrives as an IDAT.
     * When the default image is hidden, Pillow starts the animation from that still image instead of the
       fully transparent black output buffer the specification mandates, so anything the first animation
       frame does not cover opaquely shows the still image through.

   Where the predicate says Pillow is trustworthy the generator HARD-ASSERTS that Pillow's frame-by-frame
   decode equals ``_composite``'s output exactly and records ``"pillow_verified": true``; where it is not,
   the entry records ``"pillow_verified": false`` plus a note saying why.

   For the malformed corpus ``_pillow_rejects`` asserts Pillow refuses the file too, so "malformed" is
   corroborated by a second implementation rather than being this library's private opinion. The few files
   Pillow does accept are listed in ``_PILLOW_ACCEPTS_MALFORMED`` with the reason, and the assertion is
   bidirectional: a fixture in that set must be accepted and one outside it must be rejected.

Exactness gate: every fixture without a ``tolerance`` field is asserted to produce no composited channel
value within 0.01 of a half-integer, so C# float32 and Python float64 rounding cannot possibly diverge and
the byte comparison the test does is genuinely exact. Fixtures that deliberately exercise messy alphas
carry ``"tolerance": 1`` and the test compares per channel with that slack.

Manifest entry: name, file, writer, notes, sha256, size, width, height, frames, repeat_count,
animate_root_frame, is_animated, delays [[num, den], ...], disposals [int, ...], blends [int, ...],
rects [[x, y, w, h], ...], color_type, bit_depth, interlaced, pillow_verified, optional tolerance, optional
raw_delays (what the fcTL literally holds when it differs from the resolved delay), and for the malformed
files "expect" (the exception type NAME the decoder must throw) plus pillow_rejects. A malformed entry
carries every field, with width, height, frames and the per-frame arrays empty or 0 - the other formats'
convention - so one deserialiser reads both shapes without nullable collections.

Everything is derived from fixed constants, so re-running the script produces byte-identical output.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import struct
import sys
import warnings
import zlib

import numpy as np
from PIL import Image

PNG_SIG = b"\x89PNG\r\n\x1a\n"
ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]

DISPOSE_NONE, DISPOSE_BACKGROUND, DISPOSE_PREVIOUS = 0, 1, 2
BLEND_SOURCE, BLEND_OVER = 0, 1

#: Samples per pixel for each PNG colour type.
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}

#: Canvas every fixture uses unless it says otherwise. 16x12 keeps each frame's .rgba block at 768 bytes.
CANVAS = (16, 12)

#: The malformed fixtures Pillow 11.3 does NOT reject, with the reason. The assertion over this set runs
#: both ways, so a Pillow upgrade that starts or stops rejecting one of these fails the generator loudly.
_PILLOW_ACCEPTS_MALFORMED = {
    "bad_fctl_after_last": "Pillow stops after acTL's num_frames frames and never looks at the chunks that "
                           "follow, so a surplus fcTL is simply never read",
    "bad_fdat_without_fctl": "acTL declares 2 frames, so Pillow reads 2 and never reaches the third "
                             "frame's unintroduced fdAT",
    "bad_fdat_orphan_after_frame": "Pillow reads the 3 declared frames and stops, never reaching the "
                                   "stray trailing fdAT",
    "bad_dispose_op": "Pillow keeps dispose_op verbatim and only compares it against its own three "
                      "constants, so an out-of-range value silently behaves like APNG_DISPOSE_OP_NONE",
    "bad_blend_op": "Pillow tests `blend_op == OP_SOURCE` and treats every other value as OP_OVER, so an "
                    "out-of-range value is silently accepted",
    "bad_actl_after_idat": "Pillow only honours an acTL seen before IDAT; a later one is ignored and the "
                           "file decodes as a still PNG with n_frames 1 instead of raising",
    "bad_two_actl": "Pillow warns 'Invalid APNG, will use default PNG image if possible' and falls back to "
                    "the still image rather than raising",
}


# --------------------------------------------------------------------------------------------------
# Byte-level PNG/APNG assembly
# --------------------------------------------------------------------------------------------------

def _png_chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def _pack_row(samples: np.ndarray, depth: int) -> bytes:
    """Packs one scanline's samples (1-D, in order) at the given bit depth, MSB first."""
    if depth == 8:
        return samples.astype(np.uint8).tobytes()
    if depth == 16:
        return samples.astype(">u2").tobytes()
    bits = np.unpackbits(samples.astype(np.uint8)[:, None], axis=1)[:, 8 - depth:].reshape(-1)
    pad = (-len(bits)) % 8
    return np.packbits(np.concatenate([bits, np.zeros(pad, np.uint8)])).tobytes()


def _filter_row(ftype: int, row: bytes, prev: bytes | None, bpp: int) -> bytes:
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


def _raw_scanlines(samples: np.ndarray, depth: int, interlaced: bool) -> bytes:
    """Filters one sub-image into the raw (pre-zlib) PNG scanline stream, Adam7 pass by pass if asked."""
    channels = samples.shape[2]
    bpp = max(1, (depth * channels + 7) // 8)
    raw = bytearray()
    for (xs, ys, xstep, ystep) in (ADAM7 if interlaced else [(0, 0, 1, 1)]):
        sub = samples[ys::ystep, xs::xstep]
        if sub.shape[0] == 0 or sub.shape[1] == 0:
            continue
        prev: bytes | None = None
        for r in range(sub.shape[0]):
            row = _pack_row(sub[r].reshape(-1), depth)
            candidates = [_filter_row(f, row, prev, bpp) for f in range(5)]
            raw += min(candidates, key=lambda b: sum(abs(x - 128) for x in b[1:]))
            prev = row
    return bytes(raw)


def _split(data: bytes, parts: int) -> list[bytes]:
    if parts <= 1:
        return [data]
    cut = [(len(data) * i) // parts for i in range(parts + 1)]
    return [data[cut[i]:cut[i + 1]] for i in range(parts)]


def _frame(samples: np.ndarray, x: int = 0, y: int = 0, *, delay: tuple[int, int] = (10, 100),
           dispose: int = DISPOSE_NONE, blend: int = BLEND_SOURCE,
           width: int | None = None, height: int | None = None) -> dict:
    """One animation frame. `width`/`height` override what the fcTL claims, for the malformed fixtures."""
    return {
        "samples": samples,
        "x": x,
        "y": y,
        "delay": delay,
        "dispose": dispose,
        "blend": blend,
        "width": samples.shape[1] if width is None else width,
        "height": samples.shape[0] if height is None else height,
    }


def _apng_encode(canvas: tuple[int, int], frames: list[dict], *, plays: int = 0,
                 default_image: np.ndarray | None = None, idat_split: int = 1,
                 fdat_split: int | dict[int, int] = 1, sequence_overrides: dict[int, int] | None = None,
                 actl_after_idat: bool = False, duplicate_actl: bool = False,
                 extra_chunks: dict[int, list[bytes]] | None = None, trailing_chunks: tuple[bytes, ...] = (),
                 num_frames: int | None = None, omit_fctl: frozenset[int] = frozenset(),
                 truncate_fdat: int = 0, depth: int = 8, ctype: int = 6,
                 palette: np.ndarray | None = None, trns: bytes | None = None,
                 interlaced: bool = False, level: int = 9) -> bytes:
    """Assembles an APNG byte by byte.

    Every acTL and fcTL field is settable and every fcTL/fdAT sequence number can be overridden by its
    emission ordinal through `sequence_overrides`, which is what makes the malformed corpus expressible.
    When `default_image` is None the first frame becomes the IDAT image and gets an fcTL before it, so it
    is part of the animation; when it is given, that image is the hidden still fallback and every frame in
    `frames` is carried by fdAT chunks.
    """
    width, height = canvas
    sequence_overrides = sequence_overrides or {}
    extra_chunks = extra_chunks or {}
    splits = fdat_split if isinstance(fdat_split, dict) else {}
    default_split = 1 if isinstance(fdat_split, dict) else fdat_split
    slot = 0

    def next_sequence() -> bytes:
        nonlocal slot
        value = sequence_overrides.get(slot, slot)
        slot += 1
        return struct.pack(">I", value)

    def fctl(frame: dict) -> bytes:
        return _png_chunk(b"fcTL", next_sequence() + struct.pack(
            ">IIIIHHBB", frame["width"], frame["height"], frame["x"], frame["y"],
            frame["delay"][0], frame["delay"][1], frame["dispose"], frame["blend"]))

    def compress(samples: np.ndarray) -> bytes:
        return zlib.compress(_raw_scanlines(samples, depth, interlaced), level)

    out = bytearray(PNG_SIG)
    out += _png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, ctype, 0, 0, 1 if interlaced else 0))
    if palette is not None:
        out += _png_chunk(b"PLTE", np.asarray(palette, np.uint8).tobytes())
    if trns is not None:
        out += _png_chunk(b"tRNS", trns)

    declared = len(frames) if num_frames is None else num_frames
    actl = _png_chunk(b"acTL", struct.pack(">II", declared, plays))
    if not actl_after_idat:
        out += actl
        if duplicate_actl:
            out += actl

    if default_image is None:
        still = frames[0]["samples"]
        animation = list(enumerate(frames))[1:]
        if 0 not in omit_fctl:
            out += fctl(frames[0])
    else:
        still = default_image
        animation = list(enumerate(frames))

    for part in _split(compress(still), idat_split):
        out += _png_chunk(b"IDAT", part)

    if actl_after_idat:
        out += actl
        if duplicate_actl:
            out += actl

    for index, frame in animation:
        if index not in omit_fctl:
            out += fctl(frame)
        for chunk in extra_chunks.get(index, []):
            out += chunk
        parts = _split(compress(frame["samples"]), splits.get(index, default_split))
        for position, part in enumerate(parts):
            final = index == animation[-1][0] and position == len(parts) - 1
            if final and truncate_fdat:
                part = part[:-truncate_fdat]
            out += _png_chunk(b"fdAT", next_sequence() + part)

    for chunk in trailing_chunks:
        out += chunk
    out += _png_chunk(b"IEND", b"")
    return bytes(out)


def _pillow_encode(frames: list[np.ndarray], *, delays: list[int], plays: int, dispose: int, blend: int,
                   default_image: np.ndarray | None = None) -> bytes:
    """Writes an APNG with Pillow 11 / libpng, so part of the corpus comes from an independent encoder.

    Pillow only exposes one dispose_op and one blend_op for the whole animation and always writes the
    delay denominator as 1000 (the duration is milliseconds), which is why the shapes needing per-frame
    control are hand-assembled instead.
    """
    images = [Image.fromarray(np.ascontiguousarray(f)) for f in frames]
    first = Image.fromarray(np.ascontiguousarray(default_image)) if default_image is not None else images[0]
    rest = images if default_image is not None else images[1:]
    buffer = io.BytesIO()
    first.save(buffer, "PNG", save_all=True, append_images=rest, duration=delays, loop=plays,
               disposal=dispose, blend=blend, default_image=default_image is not None, optimize=False)
    return buffer.getvalue()


# --------------------------------------------------------------------------------------------------
# Independent PNG/APNG reader: recovers the frames back out of the bytes we just wrote
# --------------------------------------------------------------------------------------------------

def _chunks(data: bytes):
    """Yields (kind, payload) for every chunk. Deliberately trusting: it only ever reads our own output."""
    assert data[:8] == PNG_SIG, "not a PNG"
    pos = 8
    while pos + 8 <= len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8]
        yield kind, data[pos + 8:pos + 8 + length]
        pos += 12 + length


def _unfilter(raw: bytes, width: int, height: int, depth: int, channels: int) -> bytes:
    """Reverses the PNG per-scanline filters. Images here are tiny, so a plain Python loop is fine."""
    bpp = max(1, (depth * channels + 7) // 8)
    stride = (width * channels * depth + 7) // 8
    out = bytearray()
    prev = bytearray(stride)
    pos = 0
    for _ in range(height):
        ftype = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if ftype == 0:
                pred = 0
            elif ftype == 1:
                pred = a
            elif ftype == 2:
                pred = b
            elif ftype == 3:
                pred = (a + b) >> 1
            elif ftype == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
            else:
                raise ValueError(f"filter type {ftype}")
            line[i] = (line[i] + pred) & 0xFF
        out += line
        prev = line
    assert pos == len(raw), f"scanline stream has {len(raw) - pos} trailing bytes"
    return bytes(out)


def _unpack(rows: bytes, width: int, height: int, depth: int, channels: int) -> np.ndarray:
    stride = (width * channels * depth + 7) // 8
    values = np.zeros((height, width * channels), np.int64)
    for y in range(height):
        line = rows[y * stride:(y + 1) * stride]
        if depth == 8:
            values[y] = np.frombuffer(line, np.uint8)[:width * channels]
        elif depth == 16:
            values[y] = np.frombuffer(line, ">u2")[:width * channels]
        else:
            bits = np.unpackbits(np.frombuffer(line, np.uint8))
            packed = bits[:width * channels * depth].reshape(-1, depth)
            values[y] = packed.dot(1 << np.arange(depth - 1, -1, -1))
    return values.reshape(height, width, channels)


def _decode_subimage(compressed: bytes, width: int, height: int, depth: int, ctype: int,
                     interlaced: bool) -> np.ndarray:
    """Inflates and unfilters one sub-image into an (h, w, channels) sample array."""
    channels = CHANNELS[ctype]
    raw = zlib.decompress(compressed)
    if not interlaced:
        return _unpack(_unfilter(raw, width, height, depth, channels), width, height, depth, channels)

    samples = np.zeros((height, width, channels), np.int64)
    pos = 0
    for (xs, ys, xstep, ystep) in ADAM7:
        pass_w = (width - xs + xstep - 1) // xstep
        pass_h = (height - ys + ystep - 1) // ystep
        if pass_w <= 0 or pass_h <= 0:
            continue
        stride = (pass_w * channels * depth + 7) // 8
        size = (stride + 1) * pass_h
        block = _unpack(_unfilter(raw[pos:pos + size], pass_w, pass_h, depth, channels),
                        pass_w, pass_h, depth, channels)
        pos += size
        samples[ys::ystep, xs::xstep] = block
    assert pos == len(raw), f"Adam7 stream has {len(raw) - pos} trailing bytes"
    return samples


def _to_rgba(samples: np.ndarray, depth: int, ctype: int, palette: np.ndarray | None,
             trns: bytes | None) -> np.ndarray:
    """What the library must produce for one sub-image: 16-bit samples narrowed to their high byte,
    sub-byte grey scaled to 0..255, colour-key tRNS compared on the raw sample values before narrowing."""
    height, width = samples.shape[:2]
    alpha = np.full((height, width), 255, np.uint8)
    if ctype == 0:
        value = samples[..., 0]
        eight = (value >> 8) if depth == 16 else (value * (255 // ((1 << depth) - 1)))
        if trns is not None:
            alpha = np.where(value == struct.unpack(">H", trns)[0], 0, 255).astype(np.uint8)
        return np.dstack([eight, eight, eight, alpha]).astype(np.uint8)
    if ctype == 2:
        eight = (samples >> 8) if depth == 16 else samples
        if trns is not None:
            key = struct.unpack(">HHH", trns)
            match = (samples[..., 0] == key[0]) & (samples[..., 1] == key[1]) & (samples[..., 2] == key[2])
            alpha = np.where(match, 0, 255).astype(np.uint8)
        return np.dstack([eight[..., 0], eight[..., 1], eight[..., 2], alpha]).astype(np.uint8)
    if ctype == 3:
        assert palette is not None, "colour type 3 needs a PLTE"
        index = samples[..., 0]
        entries = np.full(len(palette), 255, np.uint8)
        if trns is not None:
            table = np.frombuffer(trns, np.uint8)
            entries[:len(table)] = table[:len(palette)]
        rgb = palette[index]
        return np.dstack([rgb[..., 0], rgb[..., 1], rgb[..., 2], entries[index]]).astype(np.uint8)
    if ctype == 4:
        eight = (samples >> 8) if depth == 16 else samples
        grey = eight[..., 0]
        return np.dstack([grey, grey, grey, eight[..., 1]]).astype(np.uint8)
    if ctype == 6:
        eight = (samples >> 8) if depth == 16 else samples
        return eight.astype(np.uint8)
    raise ValueError(ctype)


def _parse_apng(data: bytes) -> dict:
    """Reads an APNG back into canvas facts and a list of decoded animation frames.

    Everything the manifest reports about a fixture - rectangles, delays, disposal, blend, loop count,
    colour type - is recovered here from the file itself rather than from what the caller intended, so a
    bug in the assembler shows up as a mismatch instead of being copied into the expectations.
    """
    width = height = depth = ctype = 0
    interlaced = False
    palette: np.ndarray | None = None
    trns: bytes | None = None
    plays = 0
    declared = 0
    seen_idat = False
    fctl_before_idat = False
    pending: dict | None = None
    frames: list[dict] = []
    parts: list[bytes] = []
    idat: list[bytes] = []
    sequences: list[int] = []

    def flush() -> None:
        nonlocal pending, parts
        if pending is None:
            return
        # The frame described by an fcTL that precedes IDAT is carried by the IDAT chunks themselves;
        # every later frame is carried by the fdAT payloads collected since its own fcTL.
        blocks = idat if pending["from_idat"] else parts
        samples = _decode_subimage(b"".join(blocks), pending["w"], pending["h"], depth, ctype, interlaced)
        pending["rgba"] = _to_rgba(samples, depth, ctype, palette, trns)
        frames.append(pending)
        pending, parts = None, []

    for kind, payload in _chunks(data):
        if kind == b"IHDR":
            width, height, depth, ctype, _, _, inter = struct.unpack(">IIBBBBB", payload)
            interlaced = bool(inter)
        elif kind == b"PLTE":
            palette = np.frombuffer(payload, np.uint8).reshape(-1, 3)
        elif kind == b"tRNS":
            trns = payload
        elif kind == b"acTL":
            declared, plays = struct.unpack(">II", payload)
        elif kind == b"fcTL":
            flush()
            seq, fw, fh, fx, fy, num, den, dispose, blend = struct.unpack(">IIIIIHHBB", payload)
            sequences.append(seq)
            pending = {"w": fw, "h": fh, "x": fx, "y": fy, "delay": (num, den),
                       "dispose": dispose, "blend": blend, "from_idat": not seen_idat}
            if not seen_idat:
                fctl_before_idat = True
        elif kind == b"IDAT":
            seen_idat = True
            idat.append(payload)
        elif kind == b"fdAT":
            sequences.append(struct.unpack(">I", payload[:4])[0])
            parts.append(payload[4:])
        elif kind == b"IEND":
            flush()

    return {"width": width, "height": height, "bit_depth": depth, "color_type": ctype,
            "interlaced": interlaced, "plays": plays, "num_frames": declared, "frames": frames,
            "sequences": sequences, "animate_root_frame": fctl_before_idat}


# --------------------------------------------------------------------------------------------------
# The pixel oracle: a compositor written straight from the APNG specification
# --------------------------------------------------------------------------------------------------

def _round(values: np.ndarray) -> np.ndarray:
    """Rounds the way FrameOps.ToRgba32 does: `(int)(v + 0.5f)`, i.e. floor(v + 0.5) for non-negative v.

    Deliberately not np.round, whose banker's rounding sends 0.5 down and would disagree on exact halves.
    """
    return np.clip(np.floor(values + 0.5), 0, 255).astype(np.uint8)


def _source_over(source: np.ndarray, background: np.ndarray) -> np.ndarray:
    """APNG_BLEND_OP_OVER, unrounded, in whatever floating-point type the inputs carry.

    out.a = a_s + a_d*(1 - a_s) and out.c = (c_s*a_s + c_d*a_d*(1 - a_s)) / out.a, with the source taken
    verbatim when it is opaque or the background is transparent - the identity that lets OVER onto the
    initial all-transparent canvas be the same as SOURCE, so the first frame needs no special case.
    """
    source_alpha = source[..., 3:4] / 255.0
    background_weight = (background[..., 3:4] / 255.0) * (1.0 - source_alpha)
    out_alpha = source_alpha + background_weight
    with np.errstate(invalid="ignore", divide="ignore"):
        rgb = ((source[..., :3] * source_alpha) + (background[..., :3] * background_weight)) / out_alpha
    blended = np.concatenate([rgb, out_alpha * 255.0], axis=-1)
    blended = np.where(out_alpha <= 0.0, 0.0, blended)
    verbatim = (source[..., 3:4] == 255.0) | (background[..., 3:4] == 0.0)
    return np.where(verbatim, source, blended)


def _composite(canvas_width: int, canvas_height: int, frames: list[dict],
               dtype: type = np.float64) -> tuple[list[np.ndarray], list[np.ndarray]]:
    """Renders every animation frame onto the canvas exactly as the APNG specification describes.

    The buffer starts fully transparent black; the previous frame's disposal is applied before the next
    frame is drawn; APNG_DISPOSE_OP_PREVIOUS snapshots the canvas before the frame's own contribution.
    A first frame asking for APNG_DISPOSE_OP_PREVIOUS needs no special case: the snapshot it would restore
    is the all-transparent starting canvas, which is what APNG_DISPOSE_OP_BACKGROUND would leave anyway.

    `dtype` picks the arithmetic width: float64 produces the committed ground truth, and re-running it at
    float32 reproduces what C# computes, which is how the exactness gate is checked empirically rather
    than only by the half-integer heuristic.

    Returns (rendered frames as uint8, the unrounded float values written into the canvas), the second of
    which feeds the exactness gate.
    """
    buffer = np.zeros((canvas_height, canvas_width, 4), np.uint8)
    rendered: list[np.ndarray] = []
    unrounded: list[np.ndarray] = []
    for frame in frames:
        x, y = frame["x"], frame["y"]
        h, w = frame["rgba"].shape[:2]
        snapshot = buffer.copy() if frame["dispose"] == DISPOSE_PREVIOUS else None
        source = frame["rgba"].astype(dtype)
        if frame["blend"] == BLEND_SOURCE:
            value = source
        else:
            value = _source_over(source, buffer[y:y + h, x:x + w].astype(dtype))
        unrounded.append(value)
        buffer[y:y + h, x:x + w] = _round(value)
        rendered.append(buffer.copy())
        if frame["dispose"] == DISPOSE_BACKGROUND:
            buffer[y:y + h, x:x + w] = 0
        elif frame["dispose"] == DISPOSE_PREVIOUS:
            buffer = snapshot if snapshot is not None else buffer
    return rendered, unrounded


def _assert_exact(name: str, unrounded: list[np.ndarray]) -> None:
    """The exactness gate: no composited channel may land within 0.01 of a half-integer.

    C# computes the blend in float32 and Python in float64. Away from the .5 boundary both round to the
    same byte, so keeping every value clear of it is what makes the test's byte comparison legitimate.
    """
    for index, values in enumerate(unrounded):
        distance = np.abs((values - np.floor(values)) - 0.5)
        close = distance <= 0.01
        if close.any():
            first = np.argwhere(close)[0]
            raise AssertionError(
                f"{name}: frame {index} composites channel {tuple(int(v) for v in first)} to "
                f"{values[tuple(first)]!r}, within 0.01 of a half-integer - float32 and float64 could "
                f"round it differently. Give the fixture a tolerance or move the alpha off the boundary.")


def _pillow_trustworthy(parsed: dict) -> str | None:
    """Returns None when Pillow's decode may be used as a cross-check, else the reason it may not.

    Computed from the frame data, never hand-set. Three measured divergences disqualify Pillow 11.3:

    * it blends APNG_BLEND_OP_OVER with `Image.paste(src, box, mask=src)`, a straight lerp that also lerps
      alpha, so it is wrong for any source pixel with 0 < alpha < 255;
    * it scales 16-bit samples to 8 bits where the library narrows them to the high byte;
    * it cannot read an interlaced fdAT frame at all - seeking to one raises TypeError out of its zip
      decoder, although it reads the very same Adam7 sub-image correctly when it arrives as an IDAT;
    * when the default image is hidden it starts the animation from that image rather than from the fully
      transparent black output buffer the specification mandates, so anything the first animation frame
      does not cover opaquely shows the still image through. That only stays invisible when the first
      animation frame covers the whole canvas and is fully opaque, which is the case this allows.
    """
    if parsed["bit_depth"] == 16:
        return ("Pillow scales 16-bit samples to 8 bits where the library narrows them to the high byte, "
                "so its decode is not the expected image")
    if parsed["interlaced"] and len(parsed["frames"]) > 1:
        return ("Pillow 11.3 raises TypeError from its zip decoder when it seeks to an interlaced fdAT "
                "frame, so it cannot read this file at all")
    if not parsed["animate_root_frame"]:
        first = parsed["frames"][0]
        covers = (first["x"] == 0 and first["y"] == 0
                  and first["w"] == parsed["width"] and first["h"] == parsed["height"])
        if not (covers and bool((first["rgba"][..., 3] == 255).all())):
            return ("Pillow starts a hidden-default-image animation from the still image instead of the "
                    "fully transparent black output buffer the specification mandates, and the first "
                    "animation frame does not cover it opaquely")
    for frame in parsed["frames"]:
        if frame["blend"] != BLEND_OVER:
            continue
        alpha = frame["rgba"][..., 3]
        if ((alpha > 0) & (alpha < 255)).any():
            return ("Pillow composites APNG_BLEND_OP_OVER as a straight lerp that also lerps alpha, so it "
                    "disagrees with the specification wherever a source pixel is partly transparent")
    return None


def _pillow_frames(path: str, skip: int) -> list[np.ndarray]:
    """Pillow's own frame-by-frame decode.

    `skip` drops the hidden default image, which Pillow exposes as frame 0 and counts in n_frames even
    though it is not part of the animation.
    """
    decoded: list[np.ndarray] = []
    with Image.open(path) as image:
        for index in range(skip, getattr(image, "n_frames", 1)):
            image.seek(index)
            decoded.append(np.array(image.convert("RGBA")))
    return decoded


def _pillow_rejects(path: str) -> bool:
    """True when Pillow refuses to decode the file end to end, which is what a malformed fixture should do.

    Pillow's warnings are silenced here: several of these files make it warn "Invalid APNG, will use default
    PNG image if possible" and carry on, which counts as acceptance, not rejection.
    """
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            with Image.open(path) as image:
                for index in range(getattr(image, "n_frames", 1)):
                    image.seek(index)
                    image.convert("RGBA").load()
    except Exception:  # noqa: BLE001 - any failure counts as a rejection
        return True
    return False


# --------------------------------------------------------------------------------------------------
# Sources
# --------------------------------------------------------------------------------------------------

def _grid(width: int, height: int) -> tuple[np.ndarray, np.ndarray]:
    y, x = np.mgrid[0:height, 0:width]
    return x, y


def _opaque_backdrop(width: int = CANVAS[0], height: int = CANVAS[1]) -> np.ndarray:
    """A fully opaque full-canvas frame 0. Every channel is a small linear function of x and y, so a
    mis-signed frame offset or a transposed row shows up as a completely different picture."""
    x, y = _grid(width, height)
    return np.dstack([(20 + 12 * x) % 256, (30 + 15 * y) % 256, (200 - 7 * x + 3 * y) % 256,
                      np.full_like(x, 255)]).astype(np.uint8)


def _split_backdrop(width: int = CANVAS[0], height: int = CANVAS[1]) -> np.ndarray:
    """Opaque on the left half and fully transparent on the right, so the later frames blend over both a
    populated and an empty canvas and the "OVER onto transparent equals SOURCE" identity is exercised."""
    rgba = _opaque_backdrop(width, height)
    rgba[:, width // 2:] = 0
    return rgba


def _solid(width: int, height: int, colour: tuple[int, int, int, int]) -> np.ndarray:
    return np.tile(np.asarray(colour, np.uint8), (height, width, 1))


def _blank(width: int, height: int) -> np.ndarray:
    return np.zeros((height, width, 4), np.uint8)


def _stamp(canvas: np.ndarray, patch: np.ndarray, x: int, y: int) -> np.ndarray:
    """Pastes `patch` into a copy of `canvas`, for building the full-canvas frames Pillow is handed."""
    out = canvas.copy()
    out[y:y + patch.shape[0], x:x + patch.shape[1]] = patch
    return out


#: The three overlapping 6x5 rectangles frames 1..3 of every dispose x blend fixture draw, chosen so the
#: rectangles overlap each other and straddle the split backdrop's opaque/transparent boundary.
_RECTS = ((2, 1), (6, 3), (4, 5))
_RECT_COLOURS = ((240, 40, 60, 255), (40, 220, 90, 255), (60, 80, 250, 255))


def _matrix_frames(dispose: int, blend: int) -> list[dict]:
    """Four frames: an opaque/transparent backdrop then three overlapping opaque 6x5 rectangles.

    Only frames 1 and 2 carry the fixture's disposal, so APNG_DISPOSE_OP_PREVIOUS restores a *populated*
    canvas rather than the degenerate all-transparent starting state and is genuinely observable.
    """
    frames = [_frame(_split_backdrop(), delay=(10, 100), dispose=DISPOSE_NONE, blend=blend)]
    for index, ((x, y), colour) in enumerate(zip(_RECTS, _RECT_COLOURS)):
        frames.append(_frame(_solid(6, 5, colour), x, y, delay=(15 + 5 * index, 100),
                             dispose=dispose if index < 2 else DISPOSE_NONE, blend=blend))
    return frames


# --------------------------------------------------------------------------------------------------
# Corpus recorder
# --------------------------------------------------------------------------------------------------

class _Corpus:
    """Writes each fixture, derives its manifest facts from the bytes, and runs every cross-check."""

    def __init__(self, out_dir: str) -> None:
        self.out_dir = out_dir
        self.entries: list[dict] = []
        self.verified = 0
        self.unverified: list[str] = []

    def add(self, name: str, data: bytes, *, notes: str, writer: str = "hand-assembled",
            tolerance: int | None = None) -> None:
        path = os.path.join(self.out_dir, name + ".png")
        with open(path, "wb") as handle:
            handle.write(data)

        parsed = _parse_apng(data)
        frames = parsed["frames"]
        assert frames, f"{name}: no animation frames were parsed back out of the file"
        assert parsed["num_frames"] == len(frames), (
            f"{name}: acTL declares {parsed['num_frames']} frames but the file holds {len(frames)}")
        assert parsed["sequences"] == list(range(len(parsed["sequences"]))), (
            f"{name}: fcTL/fdAT sequence numbers are {parsed['sequences']}, not a 0..n-1 run")
        for frame in frames:
            assert frame["x"] + frame["w"] <= parsed["width"] and frame["y"] + frame["h"] <= parsed["height"], (
                f"{name}: frame rectangle {frame['x'], frame['y'], frame['w'], frame['h']} leaves the canvas")

        rendered, unrounded = _composite(parsed["width"], parsed["height"], frames)
        if tolerance is None:
            _assert_exact(name, unrounded)

        # Re-run the whole animation in float32, which is the width C# computes in, and require the two
        # to agree within the fixture's tolerance. This checks the exactness gate against the arithmetic
        # it is protecting instead of trusting the half-integer heuristic on its own.
        single, _ = _composite(parsed["width"], parsed["height"], frames, dtype=np.float32)
        drift = max(int(np.abs(a.astype(np.int16) - b.astype(np.int16)).max())
                    for a, b in zip(rendered, single))
        assert drift <= (tolerance or 0), (
            f"{name}: compositing in float32 instead of float64 moves a channel by {drift}, more than the "
            f"fixture's tolerance of {tolerance or 0}")

        reason = _pillow_trustworthy(parsed)
        if reason is None:
            skip = 0 if parsed["animate_root_frame"] else 1
            decoded = _pillow_frames(path, skip)
            assert len(decoded) == len(rendered), (
                f"{name}: Pillow decodes {len(decoded)} animation frame(s), the specification compositor "
                f"produces {len(rendered)}")
            for index, (got, want) in enumerate(zip(decoded, rendered)):
                assert got.shape == want.shape, (
                    f"{name}: Pillow decodes frame {index} as {got.shape}, the specification compositor "
                    f"produces {want.shape}")
                differing = np.argwhere((got != want).any(axis=-1))
                assert len(differing) == 0, (
                    f"{name}: Pillow's decode of frame {index} differs from the specification compositor "
                    f"in {len(differing)} pixel(s); the first is ({differing[0][1]},{differing[0][0]}), "
                    f"where Pillow says {list(got[tuple(differing[0])])} and the specification says "
                    f"{list(want[tuple(differing[0])])}")
            self.verified += 1
        else:
            self.unverified.append(name)
            notes = f"{notes}; {reason}"

        with open(os.path.join(self.out_dir, name + ".rgba"), "wb") as handle:
            for frame in rendered:
                handle.write(np.ascontiguousarray(frame).tobytes())
        Image.fromarray(np.ascontiguousarray(rendered[0])).save(
            os.path.join(self.out_dir, name + ".expected.png"))

        entry = {
            "name": name,
            "file": name + ".png",
            "writer": writer,
            "notes": notes,
            "sha256": hashlib.sha256(data).hexdigest()[:16],
            "size": len(data),
            "width": parsed["width"],
            "height": parsed["height"],
            "frames": len(frames),
            "is_animated": True,
            "animate_root_frame": parsed["animate_root_frame"],
            "repeat_count": parsed["plays"],
            "delays": [[f["delay"][0], f["delay"][1] or 100] for f in frames],
            "disposals": [f["dispose"] for f in frames],
            "blends": [f["blend"] for f in frames],
            "rects": [[f["x"], f["y"], f["w"], f["h"]] for f in frames],
            "color_type": parsed["color_type"],
            "bit_depth": parsed["bit_depth"],
            "interlaced": parsed["interlaced"],
            "pillow_verified": reason is None,
        }
        if any(f["delay"][1] == 0 for f in frames):
            entry["raw_delays"] = [[f["delay"][0], f["delay"][1]] for f in frames]
        if tolerance is not None:
            entry["tolerance"] = tolerance
        self.entries.append(entry)

    def add_bad(self, name: str, data: bytes, *, notes: str,
                expect: str = "InvalidImageContentException") -> None:
        """Records a file the decoder must refuse. Width/height/frames are 0, as the other formats do."""
        path = os.path.join(self.out_dir, name + ".png")
        with open(path, "wb") as handle:
            handle.write(data)

        rejects = _pillow_rejects(path)
        expected_to_reject = name not in _PILLOW_ACCEPTS_MALFORMED
        assert rejects == expected_to_reject, (
            f"{name}: Pillow {'rejects' if rejects else 'accepts'} this file but "
            f"_PILLOW_ACCEPTS_MALFORMED says it should {'reject' if expected_to_reject else 'accept'} it. "
            f"Either the fixture changed or Pillow did; re-measure and update the table with the reason.")
        if not rejects:
            notes = f"{notes}; Pillow accepts it: {_PILLOW_ACCEPTS_MALFORMED[name]}"

        # The per-frame facts are all zero/empty rather than absent, so one deserialiser handles both
        # shapes without nullable collections: there are no frames to describe when the file must be
        # refused, and "width", "height" and "frames" are 0 as the other formats' manifests do it.
        self.entries.append({
            "name": name,
            "file": name + ".png",
            "writer": "hand-assembled",
            "notes": notes,
            "sha256": hashlib.sha256(data).hexdigest()[:16],
            "size": len(data),
            "width": 0,
            "height": 0,
            "frames": 0,
            "is_animated": False,
            "animate_root_frame": False,
            "repeat_count": 0,
            "delays": [],
            "disposals": [],
            "blends": [],
            "rects": [],
            "color_type": 0,
            "bit_depth": 0,
            "interlaced": False,
            "expect": expect,
            "pillow_rejects": rejects,
        })

    def write_manifest(self) -> None:
        with open(os.path.join(self.out_dir, "manifest.json"), "w", newline="\n") as handle:
            json.dump(self.entries, handle, indent=1)
            handle.write("\n")


# --------------------------------------------------------------------------------------------------
# The corpus
# --------------------------------------------------------------------------------------------------

def gen_apng(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    corpus = _Corpus(out_dir)
    width, height = CANVAS

    # ---- dispose x blend matrix ------------------------------------------------------------------
    names = {DISPOSE_NONE: "none", DISPOSE_BACKGROUND: "background", DISPOSE_PREVIOUS: "previous"}
    described = {
        DISPOSE_NONE: "the rectangle is left as it is, so the frames accumulate",
        DISPOSE_BACKGROUND: "the rectangle is cleared to transparent black before the next frame",
        DISPOSE_PREVIOUS: "the canvas is restored to what it held before the frame was drawn",
    }
    for dispose in (DISPOSE_NONE, DISPOSE_BACKGROUND, DISPOSE_PREVIOUS):
        for blend, blend_name in ((BLEND_SOURCE, "source"), (BLEND_OVER, "over")):
            frames = _matrix_frames(dispose, blend)
            corpus.add(
                f"dispose_{names[dispose]}_blend_{blend_name}",
                _apng_encode(CANVAS, frames, plays=1),
                notes=(f"APNG_DISPOSE_OP_{names[dispose].upper()} x APNG_BLEND_OP_{blend_name.upper()}: "
                       f"{described[dispose]}. Frame 0 is a full-canvas backdrop that is opaque on the left "
                       f"and transparent on the right; frames 1..3 are overlapping opaque 6x5 rectangles at "
                       f"differing offsets, and only frames 1 and 2 carry the disposal so the restore is "
                       f"observable against a populated canvas"))

    # ---- alpha and APNG_BLEND_OP_OVER ------------------------------------------------------------
    checker = _solid(6, 5, (250, 220, 60, 255))
    checker[..., 3] = np.where(((_grid(6, 5)[0] + _grid(6, 5)[1]) % 2) == 0, 255, 0)
    corpus.add(
        "over_alpha_exact",
        _apng_encode(CANVAS, [
            _frame(_blank(width, height), delay=(10, 100)),
            _frame(_solid(6, 5, (0, 255, 0, 128)), 2, 1, delay=(10, 100), blend=BLEND_OVER),
            _frame(checker, 8, 4, delay=(10, 100), blend=BLEND_OVER),
            _frame(_solid(6, 5, (30, 60, 200, 255)), 4, 2, delay=(10, 100), blend=BLEND_OVER),
        ], plays=1),
        notes=("APNG_BLEND_OP_OVER restricted to the degenerate cases, which are exact under any rounding: "
               "half-transparent green over the empty canvas must come back verbatim (source-over onto a "
               "transparent destination is the identity), a 0/255 alpha checkerboard must punch holes "
               "rather than blend, and an opaque rectangle over the half-transparent one must overwrite it"))

    ramp = _solid(6, 5, (250, 90, 30, 255))
    ramp[..., 3] = (_grid(6, 5)[0] * 43 + _grid(6, 5)[1] * 17) % 256
    corpus.add(
        "over_alpha_general",
        _apng_encode(CANVAS, [
            _frame(_opaque_backdrop(), delay=(10, 100)),
            _frame(ramp, 2, 1, delay=(10, 100), blend=BLEND_OVER),
            _frame(_solid(6, 5, (0, 0, 0, 255)), 8, 4, delay=(10, 100)),
            _frame(_solid(6, 5, (254, 254, 254, 128)), 8, 4, delay=(10, 100), blend=BLEND_OVER),
            _frame(_solid(width, height, (255, 255, 255, 64)), delay=(10, 100), blend=BLEND_OVER),
        ], plays=1),
        tolerance=1,
        notes=("real source-over arithmetic: an alpha ramp over an opaque backdrop, then 254 at alpha 128 "
               "over an opaque black patch, then a full-canvas 25% white wash. That middle blend is "
               "(128*254 + 127*0)/255 = 127.498, only 0.002 from a rounding boundary, so this is the one "
               "fixture the exactness gate refuses and the one that carries a tolerance"))

    corpus.add(
        "over_noop_alpha_zero",
        _apng_encode(CANVAS, [
            _frame(_opaque_backdrop(), delay=(10, 100)),
            _frame(_blank(width, height), delay=(10, 100), blend=BLEND_OVER),
            _frame(_solid(6, 5, (255, 255, 255, 255)), 5, 4, delay=(10, 100), blend=BLEND_OVER),
        ], plays=1),
        notes=("a fully transparent full-canvas frame blended with APNG_BLEND_OP_OVER must leave the canvas "
               "exactly as it was. An implementation that treats OVER as SOURCE wipes the picture here"))

    # ---- geometry ---------------------------------------------------------------------------------
    edges = [_frame(_opaque_backdrop(), delay=(10, 100))]
    edge_rects = [(0, 0, 2, 2), (14, 0, 2, 2), (0, 10, 2, 2), (14, 10, 2, 2),
                  (6, 0, 4, 1), (6, 11, 4, 1), (0, 4, 1, 4), (15, 4, 1, 4), (0, 0, 16, 12)]
    for index, (x, y, w, h) in enumerate(edge_rects):
        shade = 30 + index * 24
        edges.append(_frame(_solid(w, h, (shade, 255 - shade, (shade * 3) % 256, 255)), x, y, delay=(10, 100)))
    corpus.add("offsets_edges", _apng_encode(CANVAS, edges, plays=1),
               notes=("frame rectangles on all four corners, all four edges and one exactly equal to the "
                      "canvas: a mis-signed or transposed offset moves a rectangle off the edge it belongs on"))

    corpus.add(
        "frame_1x1",
        _apng_encode(CANVAS, [
            _frame(_opaque_backdrop(), delay=(10, 100)),
            _frame(_solid(1, 1, (255, 0, 0, 255)), 0, 0, delay=(10, 100)),
            _frame(_solid(1, 1, (0, 255, 0, 255)), 15, 11, delay=(10, 100)),
            _frame(_solid(1, 1, (0, 0, 255, 255)), 8, 6, delay=(10, 100)),
            _frame(_solid(1, 1, (255, 255, 0, 255)), 3, 9, delay=(10, 100)),
        ], plays=1),
        notes="single-pixel frames in the corners, the middle and off-centre: the smallest legal rectangle")

    # ---- structural shapes -------------------------------------------------------------------------
    corpus.add(
        "single_frame",
        _apng_encode(CANVAS, [_frame(_opaque_backdrop(), delay=(1, 2))], plays=1),
        notes=("acTL num_frames 1, one fcTL before IDAT and no fdAT chunk at all: a legal animation whose "
               "only frame is the still image"))

    hidden_still = _solid(width, height, (255, 0, 255, 255))
    hidden_frames = [
        _frame(_solid(8, 6, (10, 190, 210, 255)), 3, 2, delay=(10, 100)),
        _frame(_solid(6, 5, (240, 130, 20, 255)), 8, 5, delay=(10, 100), dispose=DISPOSE_BACKGROUND),
        _frame(_solid(16, 12, (25, 25, 120, 255)), 0, 0, delay=(10, 100)),
    ]
    corpus.add(
        "hidden_first_frame",
        _apng_encode(CANVAS, hidden_frames, plays=2, default_image=hidden_still),
        notes=("the IDAT image is a still fallback that is NOT part of the animation - it is solid magenta "
               "(255,0,255,255), a colour no animation frame uses, so a decoder that emits it is caught. The "
               "first fcTL follows IDAT, the animation is the three fdAT frames only, and the canvas those "
               "three composite onto starts fully transparent"))

    corpus.add(
        "hidden_first_frame_single",
        _pillow_encode([_opaque_backdrop()], delays=[40], plays=1, dispose=DISPOSE_NONE, blend=BLEND_SOURCE,
                       default_image=hidden_still),
        writer="pillow",
        notes=("Pillow/libpng writing a hidden default image with exactly one animation frame after it: "
               "acTL num_frames 1 with no fcTL before IDAT"))

    multi = _matrix_frames(DISPOSE_NONE, BLEND_SOURCE)
    corpus.add("multi_fdat", _apng_encode(CANVAS, multi, plays=1, fdat_split={2: 3}),
               notes=("frame 2's compressed data is split across three consecutive fdAT chunks, so the "
                      "decoder must concatenate the payloads after stripping each 4-byte sequence number"))

    corpus.add("multi_idat_frame0", _apng_encode(CANVAS, multi[:3], plays=1, idat_split=3),
               notes="frame 0 (the IDAT image, which is part of the animation) is split across three IDAT chunks")

    loop_frames = [_opaque_backdrop()]
    loop_frames.append(_stamp(loop_frames[0], _solid(6, 5, _RECT_COLOURS[0]), *_RECTS[0]))
    loop_frames.append(_stamp(loop_frames[1], _solid(6, 5, _RECT_COLOURS[1]), *_RECTS[1]))
    corpus.add(
        "loop_forever",
        _pillow_encode(loop_frames, delays=[100, 120, 140], plays=0, dispose=DISPOSE_NONE, blend=BLEND_SOURCE),
        writer="pillow",
        notes=("acTL num_plays 0, which means loop forever. Written by Pillow/libpng, which cropped frames "
               "1 and 2 to the rectangle that actually changed"))

    corpus.add(
        "loop_three",
        _pillow_encode(loop_frames, delays=[100, 120, 140], plays=3, dispose=DISPOSE_NONE, blend=BLEND_SOURCE),
        writer="pillow",
        notes="acTL num_plays 3: the animation runs three times and stops. Written by Pillow/libpng")

    corpus.add(
        "delay_den_zero",
        _apng_encode(CANVAS, [
            _frame(_opaque_backdrop(), delay=(5, 0)),
            _frame(_solid(6, 5, _RECT_COLOURS[0]), 2, 1, delay=(7, 0)),
        ], plays=1),
        notes=("delay_den 0 means 1/100 s by specification, so these frames must report 5/100 and 7/100 - "
               "a Rational with a zero denominator would make ToDouble() return NaN"))

    corpus.add(
        "delay_exotic",
        _apng_encode(CANVAS, [
            _frame(_opaque_backdrop(), delay=(1, 24)),
            _frame(_solid(6, 5, _RECT_COLOURS[1]), 6, 3, delay=(1001, 30000)),
            _frame(_solid(6, 5, _RECT_COLOURS[2]), 4, 5, delay=(0, 1)),
        ], plays=1),
        notes=("film (1/24), NTSC (1001/30000) and an explicit zero delay: the numerator and denominator "
               "must be kept verbatim rather than collapsed into milliseconds"))

    text = _png_chunk(b"tEXt", b"Comment\x00chunk between fcTL and fdAT")
    phys = _png_chunk(b"pHYs", struct.pack(">IIB", 2835, 2835, 1))
    corpus.add(
        "ancillary_between_frames",
        _apng_encode(CANVAS, multi, plays=1, extra_chunks={1: [text], 2: [phys], 3: [text, phys]}),
        notes=("tEXt and pHYs chunks sit between each fcTL and its fdAT. They must be skipped without "
               "disturbing the frame sequence numbering"))

    # ---- colour types and interlacing ---------------------------------------------------------------
    corpus.add(
        "interlaced_adam7",
        _apng_encode(CANVAS, [
            _frame(_opaque_backdrop(), delay=(10, 100)),
            _frame(_solid(6, 5, _RECT_COLOURS[0]), 2, 1, delay=(10, 100)),
            _frame(_solid(9, 7, _RECT_COLOURS[2]), 5, 3, delay=(10, 100), dispose=DISPOSE_BACKGROUND),
        ], plays=1, interlaced=True),
        notes=("every frame is Adam7 interlaced over its own rectangle, so the seven-pass loop has to run "
               "per frame and on sub-canvas dimensions where several passes are empty"))

    palette = np.array([[0, 0, 0], [230, 40, 50], [40, 200, 90], [50, 90, 240],
                        [250, 220, 60], [20, 20, 30], [255, 255, 255], [130, 70, 200]], np.uint8)
    trns = bytes([0, 255, 255, 255, 255, 255, 255, 255])
    x, y = _grid(width, height)
    palette_root = (((x // 3) + (y // 2)) % 7 + 1).astype(np.int64)[..., None]
    corpus.add(
        "palette_animated",
        _apng_encode(CANVAS, [
            _frame(palette_root, delay=(10, 100)),
            _frame(np.full((5, 6, 1), 4, np.int64), 2, 1, delay=(10, 100), dispose=DISPOSE_BACKGROUND),
            _frame(np.full((5, 6, 1), 0, np.int64), 6, 3, delay=(10, 100)),
            _frame(np.full((5, 6, 1), 7, np.int64), 4, 5, delay=(10, 100)),
        ], plays=1, ctype=3, palette=palette, trns=trns),
        notes=("colour type 3 at 8 bits with a shared PLTE and a tRNS that makes entry 0 transparent: the "
               "frames share one palette, use offsets and disposal, and frame 2 paints the transparent entry"))

    grey = np.dstack([(x * 15 + y * 5) % 256, np.full_like(x, 255)]).astype(np.int64)
    corpus.add(
        "gray_alpha",
        _apng_encode(CANVAS, [
            _frame(grey, delay=(10, 100)),
            _frame(np.dstack([np.full((5, 6), 200), np.full((5, 6), 255)]).astype(np.int64), 2, 1,
                   delay=(10, 100)),
            _frame(np.dstack([np.full((5, 6), 40), np.full((5, 6), 0)]).astype(np.int64), 6, 3,
                   delay=(10, 100)),
        ], plays=1, ctype=4),
        notes=("colour type 4 (grey + alpha) at 8 bits, including a fully transparent frame that must "
               "overwrite - not blend into - the canvas because its blend op is APNG_BLEND_OP_SOURCE"))

    grey16 = (((x * 4111) + (y * 271)) % 65536).astype(np.int64)[..., None]
    corpus.add(
        "gray16",
        _apng_encode(CANVAS, [
            _frame(grey16, delay=(10, 100)),
            _frame(np.full((5, 6, 1), 0x1234, np.int64), 2, 1, delay=(10, 100)),
            _frame(np.full((5, 6, 1), 0xFEDC, np.int64), 6, 3, delay=(10, 100)),
        ], plays=1, ctype=0, depth=16),
        notes=("colour type 0 at 16 bits. An animated PNG composites in an Rgba32 canvas, so unlike a still "
               "16-bit PNG loaded into Rgba64 the samples are narrowed to their high byte - the .rgba holds "
               "that narrowed value, which is the documented fidelity asymmetry of the animated path"))

    # ---- malformed ----------------------------------------------------------------------------------
    # Every one of these is the well-formed three-frame animation above with exactly one thing wrong, so
    # the test can attribute the rejection to the defect the name describes.
    def broken(frame0: dict | None = None, frame1: dict | None = None,
               frame2: dict | None = None) -> list[dict]:
        defaults = [
            ({"samples": _opaque_backdrop(), "x": 0, "y": 0, "delay": (10, 100)}, frame0),
            ({"samples": _solid(6, 5, _RECT_COLOURS[0]), "x": 2, "y": 1, "delay": (10, 100)}, frame1),
            ({"samples": _solid(6, 5, _RECT_COLOURS[1]), "x": 6, "y": 3, "delay": (10, 100)}, frame2),
        ]
        built = []
        for base, override in defaults:
            merged = dict(base)
            merged.update(override or {})
            built.append(_frame(merged.pop("samples"), **merged))
        return built

    corpus.add_bad("bad_seq_gap", _apng_encode(CANVAS, broken(), plays=1, sequence_overrides={3: 7}),
                   notes="frame 2's fcTL carries sequence number 7 where 3 was due: the series must have no gaps")
    corpus.add_bad("bad_seq_reordered", _apng_encode(CANVAS, broken(), plays=1, sequence_overrides={3: 4, 4: 3}),
                   notes="frame 2's fcTL and fdAT sequence numbers are swapped, so the series runs 0,1,2,4,3")
    corpus.add_bad("bad_seq_duplicate", _apng_encode(CANVAS, broken(), plays=1, sequence_overrides={3: 2}),
                   notes="frame 2's fcTL repeats sequence number 2, which frame 1's fdAT already used")
    corpus.add_bad("bad_fctl_after_last", _apng_encode(CANVAS, broken(), plays=1, num_frames=2),
                   notes="acTL declares 2 frames but a third fcTL follows the second frame's data")
    corpus.add_bad("bad_frame_exceeds_canvas",
                   _apng_encode(CANVAS, broken(frame2={"x": 12}), plays=1),
                   notes="frame 2's rectangle starts at x=12 and is 6 wide, so it runs 2 pixels past the "
                         "16-pixel canvas")
    corpus.add_bad("bad_frame_zero_size",
                   _apng_encode(CANVAS, broken(frame2={"width": 0, "height": 0}), plays=1),
                   notes="frame 2's fcTL declares a 0x0 rectangle; the specification requires both to be > 0")
    corpus.add_bad("bad_fdat_without_fctl",
                   _apng_encode(CANVAS, broken(), plays=1, num_frames=2, omit_fctl=frozenset({2})),
                   notes="the third frame's data arrives as an fdAT with no fcTL introducing it")
    corpus.add_bad("bad_fdat_orphan_after_frame",
                   _apng_encode(CANVAS, broken(), plays=1, trailing_chunks=(
                       _png_chunk(b"fdAT", struct.pack(">I", 5) + zlib.compress(b"\x00" * 16)),)),
                   notes="a stray fdAT with the next sequence number follows the final frame, which is "
                         "already complete")
    corpus.add_bad("bad_first_fctl_offset",
                   _apng_encode(CANVAS, broken(frame0={"x": 2, "y": 1, "width": 14, "height": 11}), plays=1),
                   notes="the fcTL that precedes IDAT describes a 14x11 rectangle at (2,1); the fcTL for the "
                         "default image must be the whole canvas at the origin")
    corpus.add_bad("bad_num_frames_mismatch", _apng_encode(CANVAS, broken(), plays=1, num_frames=5),
                   notes="acTL declares 5 frames but the file only holds 3")
    corpus.add_bad("bad_dispose_op", _apng_encode(CANVAS, broken(frame1={"dispose": 9}), plays=1),
                   notes="frame 1's dispose_op is 9; only 0, 1 and 2 are defined")
    corpus.add_bad("bad_blend_op", _apng_encode(CANVAS, broken(frame1={"blend": 7}), plays=1),
                   notes="frame 1's blend_op is 7; only 0 and 1 are defined")
    corpus.add_bad("bad_actl_after_idat", _apng_encode(CANVAS, broken(), plays=1, actl_after_idat=True),
                   notes="acTL follows IDAT, so the fcTL before IDAT arrives while the file is not yet "
                         "declared animated; acTL must precede the first IDAT")
    corpus.add_bad("bad_two_actl", _apng_encode(CANVAS, broken(), plays=1, duplicate_actl=True),
                   notes="two acTL chunks; the specification allows exactly one")
    corpus.add_bad("bad_fdat_short", _apng_encode(CANVAS, broken(), plays=1, truncate_fdat=8),
                   notes="the last fdAT chunk's payload is cut short, so the frame's zlib stream ends "
                         "mid-way through")

    corpus.write_manifest()
    _write_expected_doc(out_dir, corpus.entries)
    _check_budget(out_dir)
    animated = [e for e in corpus.entries if "expect" not in e]
    print(f"apng: {len(corpus.entries)} fixtures ({len(animated)} decodable, "
          f"{len(corpus.entries) - len(animated)} malformed), {corpus.verified} cross-checked against Pillow"
          + (f", {len(corpus.unverified)} not ({', '.join(corpus.unverified)})" if corpus.unverified else ""))


def _tolerance_sentence(entries: list[dict]) -> str:
    named = [f"`{e['name']}` ({e['tolerance']})" for e in entries if "tolerance" in e]
    which = "No decodable fixture carries a `tolerance`" if not named else (
        "The only decodable fixture(s) carrying a `tolerance` are " + ", ".join(named))
    return (f"{which}. Every other one is asserted to composite no channel within 0.01 of a half-integer, "
            "and the generator additionally re-runs each animation in `float32` - the width C# computes "
            "in - and requires the result to be byte-identical, so the comparison really is exact.")


def _write_expected_doc(out_dir: str, entries: list[dict]) -> None:
    disposals = {0: "none", 1: "background", 2: "previous"}
    blends = {0: "source", 1: "over"}
    lines = [
        "# APNG fixtures",
        "",
        "Generated by `gen_apng.py`. Every `<name>.rgba` holds the fully composited canvas for each",
        "animation frame, concatenated in display order, `width * height * 4` bytes of RGBA per frame,",
        "row-major from the top-left. The values come from a NumPy compositor written from the APNG",
        "specification (`_composite`), not from this library and not from Pillow: Pillow 11.3 blends",
        "`APNG_BLEND_OP_OVER` as a straight lerp that also lerps alpha, so it is wrong wherever a source",
        "pixel is partly transparent. Pillow is used as a *cross-check* on the fixtures where that bug",
        "cannot bite, and the `pillow_verified` column records which those are; on those files the",
        "generator asserts Pillow's decode matches the compositor exactly.",
        "",
        "Fixture bytes come from two independent writers: `hand-assembled` files are built chunk by chunk",
        "in `gen_apng.py`, which is the only way to control every fcTL field and sequence number, and",
        "`pillow` files are written by Pillow/libpng.",
        "",
        _tolerance_sentence(entries),
        "",
        "Regenerate with `python generate.py` (or `python gen_apng.py`). Cross-check the library's own",
        "encoder output against Pillow with `python gen_apng.py --verify <test output>/apng-roundtrip`.",
        "",
        "| fixture | size | frames | writer | loops | root frame | dispose | blend | tol | pillow |",
        "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    for entry in entries:
        if "expect" in entry:
            continue
        used_dispose = "/".join(sorted({disposals.get(d, str(d)) for d in entry["disposals"]}))
        used_blend = "/".join(sorted({blends.get(b, str(b)) for b in entry["blends"]}))
        loops = "forever" if entry["repeat_count"] == 0 else str(entry["repeat_count"])
        lines.append(
            f"| `{entry['name']}` | {entry['width']}x{entry['height']} | {entry['frames']} | "
            f"{entry['writer']} | {loops} | {'animated' if entry['animate_root_frame'] else 'hidden'} | "
            f"{used_dispose} | {used_blend} | {entry.get('tolerance', 0)} | "
            f"{'yes' if entry['pillow_verified'] else 'no'} |")

    lines += ["", "## Files that must fail", "",
              "Each is the same well-formed three-frame animation with exactly one thing wrong.",
              "`pillow` records whether Pillow refuses the file too, so \"malformed\" is corroborated by a",
              "second implementation rather than being this library's private opinion.", "",
              "| fixture | expected exception | pillow rejects | notes |", "| --- | --- | --- | --- |"]
    for entry in entries:
        if "expect" not in entry:
            continue
        lines.append(f"| `{entry['name']}` | `{entry['expect']}` | "
                     f"{'yes' if entry['pillow_rejects'] else 'no'} | {entry['notes']} |")

    lines += ["", "## Per-fixture notes", ""]
    for entry in entries:
        if "expect" not in entry:
            lines.append(f"- `{entry['name']}`: {entry['notes']}.")
    lines.append("")
    with open(os.path.join(out_dir, "EXPECTED.md"), "w", newline="\n") as handle:
        handle.write("\n".join(lines))


def _check_budget(out_dir: str) -> None:
    """Keeps the corpus small enough to live in the repository: canvases are 16x12, so one composited
    frame is 768 bytes and even the ten-frame offsets_edges .rgba stays well inside the per-file cap."""
    total = 0
    for name in sorted(os.listdir(out_dir)):
        size = os.path.getsize(os.path.join(out_dir, name))
        total += size
        cap = 64 * 1024 if name in ("manifest.json", "EXPECTED.md") else 16 * 1024
        assert size < cap, f"{name} is {size} bytes, over its {cap // 1024} KB budget"
    assert total < 400 * 1024, f"apng fixtures total {total} bytes, over the 400 KB budget"
    print(f"  apng: {total / 1024:.1f} KB total")


# --------------------------------------------------------------------------------------------------
# --verify: check the library's own encoder output with Pillow
# --------------------------------------------------------------------------------------------------

def verify(directory: str) -> int:
    """Decodes every APNG in `directory` with Pillow and compares it against the committed ground truth.

    ApngEncoderTests writes each re-encoded fixture to ``<test output>/apng-roundtrip/<fixture name>.png``.
    Only the fixtures whose manifest entry says ``pillow_verified`` can be checked this way: for the rest
    Pillow's own compositing disagrees with the specification, so a mismatch would prove nothing about the
    encoder. Files with no manifest entry only have to be something Pillow can read end to end.
    """
    fixtures = os.path.join(os.path.dirname(os.path.abspath(__file__)), "apng")
    manifest_path = os.path.join(fixtures, "manifest.json")
    if not os.path.exists(manifest_path):
        print(f"no manifest at {manifest_path}; run `python gen_apng.py` first")
        return 1
    with open(manifest_path) as handle:
        entries = {entry["name"]: entry for entry in json.load(handle)}

    files = sorted(name for name in os.listdir(directory) if name.endswith(".png"))
    if not files:
        print(f"no .png files in {directory}")
        return 1

    bad = 0
    compared = 0
    decoded_only = 0
    skipped = 0
    for name in files:
        path = os.path.join(directory, name)
        entry = entries.get(name[: -len(".png")])
        if entry is not None and ("expect" in entry or not entry["pillow_verified"]):
            # Either the source fixture is one the decoder must refuse, so no encoder output corresponds
            # to it, or Pillow's own decode of this shape disagrees with the specification - see
            # _pillow_trustworthy. Comparing against it would prove nothing either way.
            skipped += 1
            continue
        if entry is None:
            try:
                _pillow_frames(path, 0)
                decoded_only += 1
            except Exception as exc:  # noqa: BLE001
                print(f"{name}: Pillow cannot read it ({exc})")
                bad += 1
            continue

        want = np.frombuffer(open(os.path.join(fixtures, entry["name"] + ".rgba"), "rb").read(), np.uint8)
        want = want.reshape(entry["frames"], entry["height"], entry["width"], 4)
        with Image.open(path) as image:
            count = getattr(image, "n_frames", 1)
        skip = count - entry["frames"]
        if skip not in (0, 1):
            print(f"{name}: Pillow sees {count} frame(s), expected {entry['frames']} "
                  f"(plus at most one hidden default image)")
            bad += 1
            continue

        got = _pillow_frames(path, skip)
        mismatch = next((index for index, frame in enumerate(got)
                         if frame.shape != want[index].shape or not bool((frame == want[index]).all())), None)
        if mismatch is not None:
            print(f"{name}: frame {mismatch} differs from the committed ground truth")
            bad += 1
            continue
        compared += 1

    print(f"verified {len(files)} file(s): {compared} against committed pixels, {decoded_only} decode-only, "
          f"{skipped} skipped (Pillow is not an oracle for them), {bad} mismatch(es)")
    return 1 if bad else 0


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--verify":
        sys.exit(verify(sys.argv[2]))
    gen_apng(os.path.join(os.path.dirname(os.path.abspath(__file__)), "apng"))
