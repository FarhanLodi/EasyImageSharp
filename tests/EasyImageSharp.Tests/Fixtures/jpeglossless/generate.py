#!/usr/bin/env python
"""Lossless JPEG (SOF3, ITU-T T.81 Annex H) fixtures and their ground truth.

    python -m pip install "numpy>=2" "imagecodecs>=2024.1"
    python generate.py

Two independent encoders write the files here:

* libjpeg-turbo 3.x, through imagecodecs.jpeg8_encode(lossless=True), for every predictor, sample precisions
  from 2 to 16 bits, grayscale, RGB and YCbCr. Its own decoder (imagecodecs.jpeg8_decode) must return the source
  samples exactly before a file is kept; for YCbCr, where the colour transform itself is lossy, its decode is the
  ground truth.
* A small encoder written below straight from T.81 Annex H (prediction, modulo-2^16 differences, Annex K.2
  optimal Huffman tables, restart markers), for what that binding cannot request: point transforms, restart
  intervals, four components, one scan per component and subsampled components. Every file it writes is decoded
  by libjpeg-turbo, and, where it handles the layout, by the independent jpegsof3 decoder bundled with
  imagecodecs; both must reproduce the expected samples exactly.

Ground truth is stored as <name>.u16.gz: gzip-compressed little-endian uint16 samples, row-major, channels
interleaved, in the file's own precision (not rescaled). Sources are a crop of libjpeg-turbo's monkey16.pgm test
image (BSD-3-Clause, see PROVENANCE.md) and fixed-seed synthetic patterns. The pixels are deterministic; gzip
and libjpeg-turbo versions may change the compressed bytes, which is why manifest.json records hashes of the
decoded samples rather than of the files.
"""
from __future__ import annotations

import gzip
import hashlib
import json
import os
import sys
import tempfile
import urllib.request

import imagecodecs
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))

# libjpeg-turbo's 16-bit test image, BSD-3-Clause (testimages/LICENSE.txt), pinned to one commit and hash. It is
# only a generator input, so it is fetched into a cache outside the repository instead of being committed.
LIBJPEG_TURBO_COMMIT = "0103036f949f30c4f794e2032a03eb183b007092"
MONKEY16_URL = f"https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/{LIBJPEG_TURBO_COMMIT}/testimages/monkey16.pgm"
MONKEY16_SHA256 = "8d122b926fba68154c4e3d73d29496c8bc67a8498ab34bb3edde56093364359f"


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


# ----- Sources -----------------------------------------------------------------------------------------------

def read_pgm16(path: str) -> np.ndarray:
    with open(path, "rb") as f:
        data = f.read()
    # P5 header: magic, width, height, maxval, each separated by whitespace, then one whitespace byte.
    parts = []
    pos = 0
    while len(parts) < 4:
        while data[pos:pos + 1].isspace():
            pos += 1
        if data[pos:pos + 1] == b"#":
            while data[pos:pos + 1] not in (b"\n", b"\r"):
                pos += 1
            continue
        start = pos
        while not data[pos:pos + 1].isspace():
            pos += 1
        parts.append(data[start:pos])
    pos += 1
    assert parts[0] == b"P5", parts
    width, height, maxval = int(parts[1]), int(parts[2]), int(parts[3])
    assert maxval == 65535, maxval
    return np.frombuffer(data[pos:pos + (width * height * 2)], dtype=">u2").reshape(height, width).astype(np.uint16)


def monkey_crop(width: int, height: int, bits: int) -> np.ndarray:
    """A crop of the 16-bit monkey test image, reduced to `bits` bits per sample."""
    image = read_pgm16(fetch(MONKEY16_URL, MONKEY16_SHA256))
    crop = image[40:40 + height, 30:30 + width]
    return (crop >> (16 - bits)).astype(np.uint16)


def synthetic(width: int, height: int, channels: int, bits: int, seed: int) -> np.ndarray:
    """Gradients, a hard edge and noise across the full range of `bits` bits."""
    rng = np.random.default_rng(seed)
    top = (1 << bits) - 1
    y, x = np.mgrid[0:height, 0:width]
    planes = []
    for c in range(channels):
        base = (x * (c + 1) * 7 + y * (3 + c) * 5) / ((width + height) * 12.0)
        wave = 0.25 * np.sin((x + 2 * c) / 3.0) * np.cos(y / 4.0)
        edge = ((x > width // 2) ^ (y > height // 3)) * 0.2
        value = np.clip(base + wave + edge, 0, 1) * top
        noise = rng.integers(-max(1, top // 40), max(2, top // 40), size=(height, width))
        planes.append(np.clip(value + noise, 0, top))
    # Pin the extremes so every fixture exercises both ends of the range.
    out = np.stack(planes, axis=-1).astype(np.uint16)
    out[0, 0, :] = 0
    out[-1, -1, :] = top
    return out if channels > 1 else out[..., 0]


# ----- A lossless encoder written from T.81 Annex H ----------------------------------------------------------

class BitWriter:
    def __init__(self) -> None:
        self.out = bytearray()
        self.acc = 0
        self.n = 0

    def put(self, value: int, length: int) -> None:
        for i in range(length - 1, -1, -1):
            self.acc = (self.acc << 1) | ((value >> i) & 1)
            self.n += 1
            if self.n == 8:
                self.out.append(self.acc)
                if self.acc == 0xFF:
                    self.out.append(0)  # Byte stuffing (T.81 F.1.2.3).
                self.acc = 0
                self.n = 0

    def flush(self) -> None:
        """Pads the last byte with one bits (T.81 F.1.2.3)."""
        if self.n:
            self.put((1 << (8 - self.n)) - 1, 8 - self.n)


def category(diff: int) -> int:
    if diff == 32768:
        return 16
    return abs(diff).bit_length()


def optimal_table(freq: list[int]) -> tuple[list[int], list[int]]:
    """T.81 Annex K.2: code lengths limited to 16 bits, with one reserved code point so no code is all ones."""
    freq = list(freq) + [1]  # Reserved symbol 256 (index len(freq)).
    size = len(freq)
    codesize = [0] * size
    others = [-1] * size
    freq = [f if f > 0 else 0 for f in freq]
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
    bits[i] -= 1  # Remove the reserved code point.
    order = sorted((i for i in range(size - 1) if codesize[i]), key=lambda i: (codesize[i], i))
    return bits[1:17], order


def canonical_codes(bits: list[int], values: list[int]) -> dict[int, tuple[int, int]]:
    codes = {}
    code = 0
    k = 0
    for length in range(1, 17):
        for _ in range(bits[length - 1]):
            codes[values[k]] = (code, length)
            code += 1
            k += 1
        code <<= 1
    return codes


def predict(sel: int, ra: int, rb: int, rc: int) -> int:
    return {1: ra, 2: rb, 3: rc, 4: ra + rb - rc, 5: ra + ((rb - rc) >> 1), 6: rb + ((ra - rc) >> 1),
            7: (ra + rb) >> 1}[sel]


def differences(plane: np.ndarray, predictor: int, precision: int, pt: int, first_lines: set[int]) -> np.ndarray:
    """Per-sample differences of one component's sample lines (T.81 H.1.2.1), reduced to [-32767, 32768]."""
    height, width = plane.shape
    x = plane.astype(np.int64) >> pt
    d = np.zeros_like(x)
    for row in range(height):
        for col in range(width):
            if row in first_lines:
                pred = (1 << (precision - pt - 1)) if col == 0 else x[row, col - 1]
            elif col == 0:
                pred = x[row - 1, 0]
            else:
                pred = predict(predictor, x[row, col - 1], x[row - 1, col], x[row - 1, col - 1])
            diff = (x[row, col] - pred) & 0xFFFF
            d[row, col] = diff - 65536 if diff > 32768 else diff
    return d


def encode_lossless(planes: list[np.ndarray], sampling: list[tuple[int, int]], ids: list[int], precision: int,
                    predictor: int, pt: int, restart_rows: int, interleaved: bool, adobe: int | None) -> bytes:
    """Encodes component planes (already at their subsampled sizes) as a lossless JPEG."""
    max_h = max(h for h, _ in sampling)
    max_v = max(v for _, v in sampling)
    width = max(p.shape[1] * max_h // h for p, (h, _) in zip(planes, sampling))
    height = max(p.shape[0] * max_v // v for p, (_, v) in zip(planes, sampling))
    out = bytearray(b"\xFF\xD8")
    if adobe is not None:
        payload = b"Adobe" + bytes([0, 100, 0, 0, 0, 0, adobe])
        out += b"\xFF\xEE" + (len(payload) + 2).to_bytes(2, "big") + payload
    sof = bytes([precision]) + height.to_bytes(2, "big") + width.to_bytes(2, "big") + bytes([len(planes)])
    for cid, (h, v) in zip(ids, sampling):
        sof += bytes([cid, (h << 4) | v, 0])
    out += b"\xFF\xC3" + (len(sof) + 2).to_bytes(2, "big") + sof

    scans = [list(range(len(planes)))] if interleaved else [[c] for c in range(len(planes))]
    for scan in scans:
        # The MCU grid: whole MCUs of H x V samples per component when interleaved, single samples otherwise.
        if len(scan) > 1:
            mcus_x = -(-width // max_h)
            mcus_y = -(-height // max_v)
            padded = []
            for c in scan:
                h, v = sampling[c]
                p = planes[c]
                pw, ph = mcus_x * h, mcus_y * v
                q = np.pad(p, ((0, ph - p.shape[0]), (0, pw - p.shape[1])), mode="edge")
                padded.append(q)
            lines_per_row = [sampling[c][1] for c in scan]
            cols_per_mcu = [sampling[c][0] for c in scan]
        else:
            padded = [planes[scan[0]]]
            mcus_y, mcus_x = padded[0].shape
            lines_per_row = [1]
            cols_per_mcu = [1]
        restart_mcus = restart_rows * mcus_x
        diffs = []
        for p, lpr in zip(padded, lines_per_row):
            firsts = {0}
            if restart_rows:
                firsts |= {r * lpr for r in range(0, mcus_y, restart_rows)}
            diffs.append(differences(p, predictor, precision, pt, firsts))
        # One optimal table per component of the scan (table ids 0..3).
        tables = []
        for d in diffs:
            freq = [0] * 17
            for value in d.flatten():
                freq[category(int(value))] += 1
            bits, values = optimal_table(freq)
            tables.append((bits, values, canonical_codes(bits, values)))
        for k, (bits, values, _) in enumerate(tables):
            body = bytes([k]) + bytes(bits) + bytes(values)
            out += b"\xFF\xC4" + (len(body) + 2).to_bytes(2, "big") + body
        if restart_mcus:
            out += b"\xFF\xDD\x00\x04" + restart_mcus.to_bytes(2, "big")
        sos = bytes([len(scan)])
        for k, c in enumerate(scan):
            sos += bytes([ids[c], k << 4])
        sos += bytes([predictor, 0, pt])
        out += b"\xFF\xDA" + (len(sos) + 2).to_bytes(2, "big") + sos
        writer = BitWriter()
        restart_index = 0
        for my in range(mcus_y):
            if restart_rows and my and my % restart_rows == 0:
                writer.flush()
                out += writer.out + bytes([0xFF, 0xD0 + (restart_index & 7)])
                restart_index += 1
                writer = BitWriter()
            for mx in range(mcus_x):
                for k in range(len(scan)):
                    codes = tables[k][2]
                    for v in range(lines_per_row[k]):
                        for h in range(cols_per_mcu[k]):
                            value = int(diffs[k][my * lines_per_row[k] + v, mx * cols_per_mcu[k] + h])
                            s = category(value)
                            code, length = codes[s]
                            writer.put(code, length)
                            if 0 < s < 16:
                                writer.put(value if value >= 0 else value + (1 << s) - 1, s)
        writer.flush()
        out += writer.out
    out += b"\xFF\xD9"
    return bytes(out)


# ----- Output ------------------------------------------------------------------------------------------------

def samples_digest(samples: np.ndarray) -> str:
    return hashlib.sha256(np.ascontiguousarray(samples, dtype="<u2").tobytes()).hexdigest()


def write_truth(name: str, samples: np.ndarray) -> None:
    raw = np.ascontiguousarray(samples, dtype="<u2").tobytes()
    with open(os.path.join(HERE, name + ".u16.gz"), "wb") as f:
        f.write(gzip.compress(raw, compresslevel=9, mtime=0))


def libjpeg_turbo_decode(data: bytes) -> np.ndarray:
    return np.asarray(imagecodecs.jpeg8_decode(data)).astype(np.uint16)


def main() -> int:
    entries = []

    def keep(name, data, truth, encoder, notes, **facts):
        with open(os.path.join(HERE, name + ".jpg"), "wb") as f:
            f.write(data)
        write_truth(name, truth)
        channels = 1 if truth.ndim == 2 else truth.shape[2]
        entries.append({"name": name, "file": name + ".jpg", "truth": name + ".u16.gz",
                        "width": int(truth.shape[1]), "height": int(truth.shape[0]), "channels": channels,
                        "encoder": encoder, "truthSha256": samples_digest(truth), "notes": notes, **facts})

    # libjpeg-turbo: every predictor at 16 bits on a natural image; other precisions on synthetic patterns.
    for predictor in range(1, 8):
        source = monkey_crop(40, 30, 16)
        data = imagecodecs.jpeg8_encode(source, lossless=True, predictor=predictor, bitspersample=16)
        assert np.array_equal(libjpeg_turbo_decode(data), source)
        keep(f"gray16_p{predictor}", data, source, "libjpeg-turbo", "monkey16 crop, 16-bit",
             precision=16, predictor=predictor, pointTransform=0, colorType="Grayscale")

    for bits, predictor, w, h in ((2, 4, 23, 17), (7, 5, 21, 19), (8, 1, 33, 9), (12, 6, 31, 22)):
        source = synthetic(w, h, 1, bits, seed=bits)
        data = imagecodecs.jpeg8_encode(source.astype(np.uint8) if bits <= 8 else source, lossless=True,
                                        predictor=predictor, bitspersample=bits)
        assert np.array_equal(libjpeg_turbo_decode(data), source)
        keep(f"gray{bits}_p{predictor}", data, source, "libjpeg-turbo", "synthetic pattern",
             precision=bits, predictor=predictor, pointTransform=0, colorType="Grayscale")

    for bits, predictor in ((8, 7), (12, 3), (16, 4)):
        source = synthetic(26, 18, 3, bits, seed=100 + bits)
        array = source.astype(np.uint8) if bits <= 8 else source
        data = imagecodecs.jpeg8_encode(array, lossless=True, predictor=predictor, bitspersample=bits,
                                        colorspace="rgb", outcolorspace="rgb")
        assert np.array_equal(libjpeg_turbo_decode(data), source)
        keep(f"rgb{bits}_p{predictor}", data, source, "libjpeg-turbo", "synthetic pattern, component ids R G B",
             precision=bits, predictor=predictor, pointTransform=0, colorType="Rgb")

    # YCbCr: the colour transform is not lossless, so libjpeg-turbo's decode is the truth.
    source = synthetic(24, 16, 3, 8, seed=7).astype(np.uint8)
    data = imagecodecs.jpeg8_encode(source, lossless=True, predictor=1, bitspersample=8,
                                    colorspace="rgb", outcolorspace="ycbcr")
    keep("ycc8_p1", data, libjpeg_turbo_decode(data), "libjpeg-turbo",
         "YCbCr components; truth is libjpeg-turbo's decode to RGB", precision=8, predictor=1, pointTransform=0,
         colorType="YCbCr")

    # Spec-derived encoder: point transform, restart intervals, CMYK, one scan per component, subsampling.
    def spec(name, planes, sampling, ids, precision, predictor, pt, restart_rows, interleaved, adobe, color, notes,
             expected):
        data = encode_lossless(planes, sampling, ids, precision, predictor, pt, restart_rows, interleaved, adobe)
        decoded = libjpeg_turbo_decode(data)
        assert np.array_equal(decoded, expected), (name, "libjpeg-turbo disagrees")
        checked = ["libjpeg-turbo"]
        if len(planes) == 1 and not restart_rows:
            # jpegsof3 handles neither restart markers nor several components per scan, and returns the samples
            # as coded, without undoing the point transform.
            other = np.asarray(imagecodecs.jpegsof3_decode(data)).astype(np.uint16) << pt
            assert np.array_equal(other.reshape(expected.shape), expected), (name, "jpegsof3 disagrees")
            checked.append("jpegsof3")
        keep(name, data, expected, "T.81 Annex H encoder in generate.py", notes, precision=precision,
             predictor=predictor, pointTransform=pt, restartRows=restart_rows, interleaved=interleaved,
             colorType=color, verifiedBy=checked)

    gray = monkey_crop(37, 29, 12)
    for pt, predictor in ((1, 1), (3, 6)):
        expected = (gray >> pt) << pt
        spec(f"gray12_pt{pt}_p{predictor}", [gray], [(1, 1)], [1], 12, predictor, pt, 0, True, None, "Grayscale",
             f"point transform {pt}", expected)

    spec("gray12_restart_p5", [gray], [(1, 1)], [1], 12, 5, 0, 4, True, None, "Grayscale",
         "restart interval of four lines", gray)

    rgb = synthetic(22, 15, 3, 10, seed=31)
    spec("rgb10_restart_pt2_p7", [rgb[..., c] for c in range(3)], [(1, 1)] * 3, [ord("R"), ord("G"), ord("B")],
         10, 7, 2, 3, True, None, "Rgb", "10-bit RGB, restart interval of three MCU rows, point transform 2",
         (rgb >> 2) << 2)

    spec("rgb8_planar_p2", [rgb[..., c].astype(np.uint16) >> 2 for c in range(3)], [(1, 1)] * 3,
         [ord("R"), ord("G"), ord("B")], 8, 2, 0, 0, False, None, "Rgb",
         "one non-interleaved scan per component", (rgb >> 2).astype(np.uint16))

    cmyk = synthetic(19, 13, 4, 12, seed=44)
    spec("cmyk12_p4", [cmyk[..., c] for c in range(4)], [(1, 1)] * 4, [1, 2, 3, 4], 12, 4, 0, 2, True, None,
         "Cmyk", "four components (plain CMYK, no Adobe marker), restart interval of two MCU rows", cmyk)

    # Subsampled: component 1 at 2x2 against 1x1 chroma-like components, RGB ids so no colour transform runs.
    # libjpeg-turbo upsamples lossless frames by replication (it never applies the triangle filter there).
    full = synthetic(18, 14, 1, 8, seed=9)
    small = [synthetic(9, 7, 1, 8, seed=s) for s in (10, 11)]
    data = encode_lossless([full, small[0], small[1]], [(2, 2), (1, 1), (1, 1)], [ord("R"), ord("G"), ord("B")],
                           8, 1, 0, 0, True, 0)
    expected = libjpeg_turbo_decode(data)
    assert np.array_equal(expected[..., 0], full)
    assert np.array_equal(expected[..., 1], np.repeat(np.repeat(small[0], 2, axis=0), 2, axis=1))
    keep("rgb8_sub22_p1", data, expected, "T.81 Annex H encoder in generate.py",
         "first component 2x2, others 1x1, Adobe transform 0; truth is libjpeg-turbo's replicated upsampling",
         precision=8, predictor=1, pointTransform=0, restartRows=0, interleaved=True, colorType="Rgb",
         verifiedBy=["libjpeg-turbo"])

    manifest = {
        "generator": "tests/EasyImageSharp.Tests/Fixtures/jpeglossless/generate.py",
        "imagecodecs": imagecodecs.__version__,
        "libjpegTurbo": imagecodecs.jpeg8_version(),
        "truthFormat": "gzip of little-endian uint16 samples, row-major, channels interleaved, file precision",
        "fixtures": entries,
    }
    with open(os.path.join(HERE, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"jpeglossless: {len(entries)} fixtures")
    return 0


if __name__ == "__main__":
    sys.exit(main())
