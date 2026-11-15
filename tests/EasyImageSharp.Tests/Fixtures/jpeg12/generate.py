#!/usr/bin/env python
"""12-bit DCT JPEG fixtures (SOF1, Huffman) and libjpeg-turbo's decode of each.

    python -m pip install "numpy>=2" "imagecodecs>=2024.1"
    python generate.py

* monkey12.jpg is libjpeg-turbo's own 12-bit test image (testimages/, BSD-3-Clause, see PROVENANCE.md), fetched
  from a pinned commit and checked against its SHA-256: 149x227, YCbCr 4:2:0, optimised Huffman tables and an
  embedded ICC profile, written by libjpeg-turbo's cjpeg.
* The other files are fixed-seed synthetic patterns covering the whole 12-bit range, encoded by libjpeg-turbo 3.x
  through imagecodecs.jpeg8_encode(bitspersample=12): grayscale, YCbCr 4:4:4, 4:2:2 and 4:2:0 at an odd size, RGB
  without a colour transform, and optimised Huffman tables.

Ground truth is libjpeg-turbo's decode of each file (imagecodecs.jpeg8_decode: the accurate integer IDCT and
triangle upsampling), stored as <name>.u16.gz: gzip-compressed little-endian uint16 samples, row-major, channels
interleaved, 12-bit values (0-4095). For monkey12 only the rows listed under "truthRows" in manifest.json are
stored, to keep the file small. This decoder runs a floating-point IDCT, so tests compare with a tolerance
rather than bit for bit, exactly as the 8-bit JPEG fixtures do.
"""
from __future__ import annotations

import gzip
import hashlib
import json
import os
import shutil
import sys
import tempfile
import urllib.request

import imagecodecs
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))

LIBJPEG_TURBO_COMMIT = "0103036f949f30c4f794e2032a03eb183b007092"
MONKEY12_URL = f"https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/{LIBJPEG_TURBO_COMMIT}/testimages/monkey12.jpg"
MONKEY12_SHA256 = "a3cb218412cecfa877a4adee520aa3d58ac0d6d0f36a7a2541e598324ce481e3"


def fetch(url: str, sha256: str) -> str:
    """Returns a local copy of `url`, downloading it once into a cache and refusing a copy whose hash differs."""
    cache = os.path.join(tempfile.gettempdir(), "easyimagesharp-fixture-sources")
    os.makedirs(cache, exist_ok=True)
    path = os.path.join(cache, sha256 + "-" + url.rsplit("/", 1)[-1])
    if not os.path.exists(path):
        with urllib.request.urlopen(url) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != sha256:
            raise SystemExit(f"{url}: SHA-256 mismatch")
        with open(path, "wb") as f:
            f.write(data)
    with open(path, "rb") as f:
        if hashlib.sha256(f.read()).hexdigest() != sha256:
            raise SystemExit(f"{path}: SHA-256 mismatch")
    return path


def synthetic(width: int, height: int, channels: int, seed: int) -> np.ndarray:
    """Smooth gradients, a hard edge, fine texture and noise across 0-4095."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width]
    planes = []
    for c in range(channels):
        base = (x * (2 + c) + y * (3 - c * 0.5)) / (width * 2.0 + height * 3.0)
        texture = 0.12 * np.sin(x * (0.9 + 0.2 * c)) * np.cos(y * 0.7)
        edge = 0.3 * ((x - width / 2) ** 2 + (y - height / 2) ** 2 < (min(width, height) / 3) ** 2)
        value = np.clip(base + texture + edge, 0, 1) * 4095 + rng.normal(0, 25, size=(height, width))
        planes.append(np.clip(np.round(value), 0, 4095))
    out = np.stack(planes, axis=-1).astype(np.uint16)
    out[0, :4] = 0
    out[-1, -4:] = 4095
    return out if channels > 1 else out[..., 0]


def frame_header(data: bytes) -> tuple[int, int, int, list[tuple[int, int, int]]]:
    pos = 2
    while pos + 4 <= len(data):
        if data[pos] != 0xFF:
            pos += 1
            continue
        marker = data[pos + 1]
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
            pos += 2
            continue
        length = (data[pos + 2] << 8) | data[pos + 3]
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            body = data[pos + 4:pos + 2 + length]
            comps = [(body[6 + 3 * i], body[7 + 3 * i] >> 4, body[7 + 3 * i] & 15) for i in range(body[5])]
            return marker, body[0], body[5], comps
        pos += 2 + length
    raise ValueError("no frame header")


# ----- Coefficient-level transcoding ------------------------------------------------------------------------
#
# The encoder binding cannot write 12-bit progressive files or restart intervals, so these are derived from a
# libjpeg-turbo sequential file without touching its quantized coefficients: a minimal sequential decoder reads
# them, and minimal encoders written from T.81 Annex G (spectral selection) and F.1.2.3/B.2.4.4 (restart markers)
# re-entropy-code them. libjpeg-turbo must then decode the result to exactly the pixels it decodes the original
# to, which proves the coefficients survived; that decode is also the ground truth.

ZIGZAG = [0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7,
          14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39,
          46, 53, 60, 61, 54, 47, 55, 62, 63]


def segments(data: bytes) -> list[tuple[int, bytes, int]]:
    """(marker, payload, offset after the segment) for every segment up to and including the first SOS."""
    out = []
    pos = 2
    while True:
        assert data[pos] == 0xFF
        marker = data[pos + 1]
        length = (data[pos + 2] << 8) | data[pos + 3]
        out.append((marker, data[pos + 4:pos + 2 + length], pos + 2 + length))
        pos += 2 + length
        if marker == 0xDA:
            return out


class BitReader:
    def __init__(self, data: bytes, pos: int) -> None:
        self.data, self.pos, self.acc, self.n = data, pos, 0, 0

    def bit(self) -> int:
        if self.n == 0:
            b = self.data[self.pos]
            if b == 0xFF:
                assert self.data[self.pos + 1] == 0, "unexpected marker inside entropy-coded data"
                self.pos += 2
            else:
                self.pos += 1
            self.acc, self.n = b, 8
        self.n -= 1
        return (self.acc >> self.n) & 1

    def bits(self, count: int) -> int:
        value = 0
        for _ in range(count):
            value = (value << 1) | self.bit()
        return value

    def decode(self, table: dict[tuple[int, int], int]) -> int:
        code = 0
        for length in range(1, 17):
            code = (code << 1) | self.bit()
            if (length, code) in table:
                return table[(length, code)]
        raise ValueError("bad Huffman code")


def extend(value: int, size: int) -> int:
    return value - (1 << size) + 1 if size and value < (1 << (size - 1)) else value


def read_coefficients(data: bytes) -> dict:
    """Decodes a single-scan sequential Huffman JPEG down to its quantized coefficients (zigzag order)."""
    tables = {}
    kept = []
    frame = None
    for marker, payload, end in segments(data):
        if marker == 0xC4:
            pos = 0
            while pos < len(payload):
                tc_th = payload[pos]
                counts = payload[pos + 1:pos + 17]
                values = payload[pos + 17:pos + 17 + sum(counts)]
                table, code, k = {}, 0, 0
                for length in range(1, 17):
                    for _ in range(counts[length - 1]):
                        table[(length, code)] = values[k]
                        code, k = code + 1, k + 1
                    code <<= 1
                tables[tc_th] = table
                pos += 17 + sum(counts)
        elif marker in (0xC0, 0xC1):
            frame = payload
        elif marker == 0xDD:
            raise ValueError("source already has restart markers")
        elif marker == 0xDA:
            sos, scan_start = payload, end
        if marker in (0xDB, 0xE0, 0xE2):
            kept.append((marker, payload))
    precision, height, width, count = frame[0], (frame[1] << 8) | frame[2], (frame[3] << 8) | frame[4], frame[5]
    comps = [{"id": frame[6 + 3 * i], "h": frame[7 + 3 * i] >> 4, "v": frame[7 + 3 * i] & 15} for i in range(count)]
    assert sos[0] == count, "only single-scan sources are supported"
    for i in range(count):
        assert sos[1 + 2 * i] == comps[i]["id"]
        comps[i]["dc"], comps[i]["ac"] = tables[sos[2 + 2 * i] >> 4], tables[0x10 | (sos[2 + 2 * i] & 15)]
    max_h, max_v = max(c["h"] for c in comps), max(c["v"] for c in comps)
    mcus_x, mcus_y = -(-width // (8 * max_h)), -(-height // (8 * max_v))
    for c in comps:
        c["width"] = -(-width * c["h"] // max_h)
        c["height"] = -(-height * c["v"] // max_v)
        c["coef"] = np.zeros((mcus_y * c["v"], mcus_x * c["h"], 64), dtype=np.int32)
    reader = BitReader(data, scan_start)

    def block(c, by, bx, pred):
        t = reader.decode(c["dc"])
        pred += extend(reader.bits(t), t)
        c["coef"][by, bx, 0] = pred
        k = 1
        while k < 64:
            rs = reader.decode(c["ac"])
            r, s = rs >> 4, rs & 15
            if s == 0:
                if r != 15:
                    break
                k += 16
                continue
            k += r
            c["coef"][by, bx, k] = extend(reader.bits(s), s)
            k += 1
        return pred

    preds = [0] * count
    if count == 1:
        c = comps[0]
        for by in range(-(-c["height"] // 8)):
            for bx in range(-(-c["width"] // 8)):
                preds[0] = block(c, by, bx, preds[0])
    else:
        for my in range(mcus_y):
            for mx in range(mcus_x):
                for i, c in enumerate(comps):
                    for v in range(c["v"]):
                        for h in range(c["h"]):
                            preds[i] = block(c, my * c["v"] + v, mx * c["h"] + h, preds[i])
    return {"precision": precision, "width": width, "height": height, "comps": comps, "frame": frame,
            "kept": kept, "mcus": (mcus_x, mcus_y)}


class BitWriter:
    def __init__(self) -> None:
        self.out, self.acc, self.n = bytearray(), 0, 0

    def put(self, value: int, length: int) -> None:
        for i in range(length - 1, -1, -1):
            self.acc = (self.acc << 1) | ((value >> i) & 1)
            self.n += 1
            if self.n == 8:
                self.out.append(self.acc)
                if self.acc == 0xFF:
                    self.out.append(0)
                self.acc, self.n = 0, 0

    def flush(self) -> None:
        if self.n:
            self.put((1 << (8 - self.n)) - 1, 8 - self.n)


def optimal_table(freq: list[int]) -> tuple[list[int], list[int]]:
    """T.81 Annex K.2 code lengths, limited to 16 bits, with the all-ones code point reserved."""
    freq = list(freq) + [1]
    size = len(freq)
    codesize, others = [0] * size, [-1] * size
    while True:
        candidates = [i for i in range(size) if freq[i] > 0]
        if len(candidates) < 2:
            break
        v1 = min(candidates, key=lambda i: (freq[i], -i))
        v2 = min((i for i in candidates if i != v1), key=lambda i: (freq[i], -i))
        freq[v1] += freq[v2]
        freq[v2] = 0
        codesize[v1] += 1
        while others[v1] >= 0:
            v1 = others[v1]
            codesize[v1] += 1
        others[v1] = v2
        codesize[v2] += 1
        while others[v2] >= 0:
            v2 = others[v2]
            codesize[v2] += 1
    bits = [0] * 33
    for i in range(size):
        if codesize[i]:
            bits[codesize[i]] += 1
    for i in range(32, 16, -1):
        while bits[i] > 0:
            j = i - 2
            while bits[j] == 0:
                j -= 1
            bits[i] -= 2
            bits[i - 1] += 1
            bits[j + 1] += 2
            bits[j] -= 1
    i = 16
    while bits[i] == 0:
        i -= 1
    bits[i] -= 1
    return bits[1:17], sorted((i for i in range(size - 1) if codesize[i]), key=lambda i: (codesize[i], i))


def emit_scan(out: bytearray, scan_ids: list[int], spectral: tuple[int, int], events: list, restart: int) -> None:
    """Writes DHT, SOS and the entropy-coded data for a list of (table index, symbol, bits, length) events, with
    None entries marking restart points."""
    keys = sorted({e[0] for e in events if e is not None})
    codes = {}
    for key in keys:
        freq = [0] * 256
        for e in events:
            if e is not None and e[0] == key:
                freq[e[1]] += 1
        bits, values = optimal_table(freq)
        out += b"\xFF\xC4" + (19 + len(values)).to_bytes(2, "big") + bytes([key]) + bytes(bits) + bytes(values)
        table, code, k = {}, 0, 0
        for length in range(1, 17):
            for _ in range(bits[length - 1]):
                table[values[k]] = (code, length)
                code, k = code + 1, k + 1
            code <<= 1
        codes[key] = table
    if restart:
        out += b"\xFF\xDD\x00\x04" + restart.to_bytes(2, "big")
    sos = bytes([len(scan_ids)])
    for i, cid in enumerate(scan_ids):
        sos += bytes([cid, (i << 4) | i])
    sos += bytes([spectral[0], spectral[1], 0])
    out += b"\xFF\xDA" + (len(sos) + 2).to_bytes(2, "big") + sos
    writer, rst = BitWriter(), 0
    for e in events:
        if e is None:
            writer.flush()
            out += writer.out + bytes([0xFF, 0xD0 + (rst & 7)])
            writer, rst = BitWriter(), rst + 1
            continue
        code, length = codes[e[0]][e[1]]
        writer.put(code, length)
        if e[3]:
            writer.put(e[2], e[3])
    writer.flush()
    out += writer.out


def magnitude(value: int) -> tuple[int, int]:
    size = abs(value).bit_length()
    return size, (value if value >= 0 else value + (1 << size) - 1)


def header(info: dict, sof: int) -> bytearray:
    out = bytearray(b"\xFF\xD8")
    for marker, payload in info["kept"]:
        out += bytes([0xFF, marker]) + (len(payload) + 2).to_bytes(2, "big") + payload
    out += bytes([0xFF, sof]) + (len(info["frame"]) + 2).to_bytes(2, "big") + info["frame"]
    return out


def ac_events(coef: np.ndarray, key: int, start: int, end: int) -> list:
    events = []
    run = 0
    for k in range(start, end + 1):
        value = int(coef[k])
        if value == 0:
            run += 1
            continue
        while run > 15:
            events.append((key, 0xF0, 0, 0))
            run -= 16
        size, bits = magnitude(value)
        events.append((key, (run << 4) | size, bits, size))
        run = 0
    if run:
        events.append((key, 0x00, 0, 0))  # EOB (EOB0 in a progressive scan).
    return events


def to_progressive(info: dict) -> bytes:
    """SOF2 with an interleaved DC scan, then two AC bands per component; no successive approximation."""
    out = header(info, 0xC2)
    comps = info["comps"]
    mcus_x, mcus_y = info["mcus"]
    events, preds = [], [0] * len(comps)
    for my in range(mcus_y):
        for mx in range(mcus_x):
            for i, c in enumerate(comps):
                for v in range(c["v"]):
                    for h in range(c["h"]):
                        dc = int(c["coef"][my * c["v"] + v, mx * c["h"] + h, 0])
                        size, bits = magnitude(dc - preds[i])
                        events.append((i, size, bits, size))
                        preds[i] = dc
    emit_scan(out, [c["id"] for c in comps], (0, 0), events, 0)
    for c in comps:
        for band in ((1, 5), (6, 63)):
            events = []
            for by in range(-(-c["height"] // 8)):
                for bx in range(-(-c["width"] // 8)):
                    events += ac_events(c["coef"][by, bx], 0x10, band[0], band[1])
            emit_scan(out, [c["id"]], band, events, 0)
    return bytes(out + b"\xFF\xD9")


def with_restarts(info: dict, interval: int) -> bytes:
    """The same sequential scan, re-coded with a restart marker every `interval` MCUs."""
    out = header(info, 0xC1)
    comps = info["comps"]
    mcus_x, mcus_y = info["mcus"]
    events, preds, count = [], [0] * len(comps), 0
    units = [(my, mx) for my in range(mcus_y) for mx in range(mcus_x)]
    if len(comps) == 1:
        c = comps[0]
        units = [(by, bx) for by in range(-(-c["height"] // 8)) for bx in range(-(-c["width"] // 8))]
    for u, (my, mx) in enumerate(units):
        if u and u % interval == 0:
            events.append(None)
            preds = [0] * len(comps)
        for i, c in enumerate(comps):
            vs, hs = (1, 1) if len(comps) == 1 else (c["v"], c["h"])
            for v in range(vs):
                for h in range(hs):
                    block = c["coef"][my * vs + v, mx * hs + h]
                    dc = int(block[0])
                    size, bits = magnitude(dc - preds[i])
                    events.append((i, size, bits, size))
                    preds[i] = dc
                    events += ac_events(block, 0x10 | i, 1, 63)
        count += 1
    emit_scan(out, [c["id"] for c in comps], (0, 63), events, interval)
    return bytes(out + b"\xFF\xD9")


def main() -> int:
    entries = []

    def keep(name: str, data: bytes, notes: str, encoder: str, sample_rows: bool = False) -> None:
        path = os.path.join(HERE, name + ".jpg")
        with open(path, "wb") as f:
            f.write(data)
        truth = np.asarray(imagecodecs.jpeg8_decode(data)).astype(np.uint16)
        height = truth.shape[0]
        # Large images keep the truth of a sample of rows only - the first and last row of every 8-row band, which
        # covers both 4:2:0 row phases, every MCU row and the bottom edge - so the file stays small.
        rows = [y for y in range(height) if y % 8 in (0, 7) or y == height - 1] if sample_rows else None
        stored = truth[rows] if rows is not None else truth
        raw = np.ascontiguousarray(stored, dtype="<u2").tobytes()
        with open(os.path.join(HERE, name + ".u16.gz"), "wb") as f:
            f.write(gzip.compress(raw, compresslevel=9, mtime=0))
        marker, precision, components, comps = frame_header(data)
        assert precision == 12 and marker in (0xC1, 0xC2), (name, hex(marker), precision)
        entries.append({
            "name": name, "file": name + ".jpg", "truth": name + ".u16.gz",
            "width": int(truth.shape[1]), "height": int(truth.shape[0]),
            "channels": 1 if truth.ndim == 2 else int(truth.shape[2]),
            "frame": f"SOF{marker - 0xC0}", "precision": precision,
            "sampling": [f"{h}x{v}" for _, h, v in comps],
            "componentIds": [cid for cid, _, _ in comps],
            "encoder": encoder, "truthSha256": hashlib.sha256(raw).hexdigest(), "notes": notes,
            **({"truthRows": rows} if rows is not None else {}),
        })

    monkey = fetch(MONKEY12_URL, MONKEY12_SHA256)
    with open(monkey, "rb") as f:
        keep("monkey12", f.read(), "libjpeg-turbo testimages/monkey12.jpg at commit " + LIBJPEG_TURBO_COMMIT,
             "libjpeg-turbo cjpeg (upstream test image)", sample_rows=True)

    gray = synthetic(45, 37, 1, seed=12)
    keep("gray12_q90", imagecodecs.jpeg8_encode(gray, level=90, bitspersample=12), "synthetic grayscale",
         "libjpeg-turbo")

    color = synthetic(45, 37, 3, seed=1212)
    for subsampling, quality in (("444", 92), ("422", 88), ("420", 85)):
        keep(f"ycc12_{subsampling}_q{quality}",
             imagecodecs.jpeg8_encode(color, level=quality, bitspersample=12, subsampling=subsampling),
             f"synthetic YCbCr {subsampling}, odd size", "libjpeg-turbo")

    keep("ycc12_420_optimized", imagecodecs.jpeg8_encode(color, level=75, bitspersample=12, subsampling="420",
                                                         optimize=True),
         "synthetic YCbCr 420, optimised Huffman tables", "libjpeg-turbo")

    keep("rgb12_q95", imagecodecs.jpeg8_encode(color, level=95, bitspersample=12, colorspace="rgb",
                                               outcolorspace="rgb"),
         "synthetic RGB components (ids R G B), no colour transform", "libjpeg-turbo")

    # Transcoded variants: identical coefficients, so libjpeg-turbo must decode them to identical pixels.
    for source_name, variants in (("ycc12_420_q85", ("progressive", "restart")), ("gray12_q90", ("progressive", "restart"))):
        with open(os.path.join(HERE, source_name + ".jpg"), "rb") as f:
            original = f.read()
        info = read_coefficients(original)
        reference = np.asarray(imagecodecs.jpeg8_decode(original))
        for variant in variants:
            data = to_progressive(info) if variant == "progressive" else with_restarts(info, 3)
            assert np.array_equal(np.asarray(imagecodecs.jpeg8_decode(data)), reference), (source_name, variant)
            notes = ("SOF2: interleaved DC scan, AC bands 1-5 and 6-63 per component" if variant == "progressive"
                     else "SOF1 with a restart marker every 3 MCUs")
            keep(f"{source_name}_{variant}", data, f"{notes}; coefficients of {source_name}.jpg",
                 "coefficient transcoder in generate.py (T.81 Annex G / F.1.2.3)")

    manifest = {
        "generator": "tests/EasyImageSharp.Tests/Fixtures/jpeg12/generate.py",
        "imagecodecs": imagecodecs.__version__,
        "libjpegTurbo": imagecodecs.jpeg8_version(),
        "truthFormat": "gzip of little-endian uint16 samples, row-major, channels interleaved, 12-bit values",
        "fixtures": entries,
    }
    with open(os.path.join(HERE, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"jpeg12: {len(entries)} fixtures")
    return 0


if __name__ == "__main__":
    sys.exit(main())
