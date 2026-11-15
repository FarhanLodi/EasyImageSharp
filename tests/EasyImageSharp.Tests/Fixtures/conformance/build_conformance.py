#!/usr/bin/env python
"""Builds the third-party conformance corpus under Fixtures/conformance/ from the suites' official releases.

    python -I build_conformance.py --download            # fetch the pinned archives into a fresh temp dir and build
    python -I build_conformance.py --pngsuite DIR --libtiff DIR --libjpeg-turbo DIR   # build from extracted releases
    python -I build_conformance.py --download --libwebp  # also build the libwebp-test-data set (git-ignored; see below)
    python -I build_conformance.py --check               # re-derive every reference from the committed files and compare

Run it with ``-I`` (isolated mode): it reads downloaded archives, and isolated mode keeps the interpreter from importing
anything that sits next to them. Requires Python 3.11+, Pillow 11, NumPy 2 and tifffile, the same toolchain as
../generate.py. It is deliberately not named gen_*.py, so generate.py and check_determinism.py never run it: the inputs
here are downloaded, not generated, and regenerating the main corpus must not need the network.

Every suite folder follows the fixture conventions (see ../README.md):

  <file>                    the third-party file, byte for byte as released
  <name>.rgba[.gz]          ground truth: raw RGBA8, top-left origin, frames concatenated; gzip-wrapped when the dump
                            would exceed GZIP_ABOVE bytes (a bilevel fax page is 33 MB raw and 100 KB gzipped)
  manifest.json             one entry per file (see ENTRY KEYS below)
  LICENSE files             the suite's own licence text, verbatim

Ground truth never comes from EasyImageSharp:

  pngsuite        a PNG reader written here from the PNG specification (inflate via zlib, unfiltering, Adam7, bit
                  unpacking), cross-checked against Pillow wherever Pillow keeps full information; the samples are
                  narrowed the way ../generate.py's png_expected documents the library contract (16-bit samples keep
                  their high byte, sub-byte gray is scaled to 0..255, colour keys compare raw samples)
  libtiff         Pillow (libtiff); 16-bit samples from tifffile, high byte kept, cross-checked against Pillow
  libjpeg-turbo   Pillow (libjpeg-turbo) for JPEG, BMP and 8-bit PNG/PNM; the spec reader for 16-bit PNG; NumPy
                  from the raw samples for 16-bit PGM (round(v * 255 / maxval), the library's documented scaling)
  libwebp         Pillow (libwebp), cross-checked against the dwebp PAM MD5 sums that libwebp-test-data ships

ENTRY KEYS: name, file, format, width, height, frames, reference (null when no independent decoder can read the
file), tolerance (max abs channel difference; null = not enforced), min_psnr (null = exact), expect (exception type
Image.Load must throw), expect_direct (exception type the format's decoder must throw when called directly),
contract_only (no reference: the decode must succeed or fail through the documented exception contract),
oracle, notes, sha256, size.

libwebp-test-data is not committed. Its repository carries no licence file for the images (only its scripts say
they are distributed under libwebp's terms), so the set is built into libwebp/, which .gitignore excludes, and
LibwebpConformanceTests runs it only when present. See README.md.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import shutil
import struct
import sys
import tarfile
import tempfile
import urllib.request
import zlib

import numpy as np
from PIL import Image, features

HERE = os.path.dirname(os.path.abspath(__file__))
GZIP_ABOVE = 256 * 1024

# Official release archives, pinned by SHA-256 (computed from the downloads used to build the committed corpus).
ARCHIVES = {
    "pngsuite": ("http://www.schaik.com/pngsuite/PngSuite-2017jul19.tgz",
                 "0294b244c95a8342c01b00010cf34abdcabc7c6a34fd0fe1bd963917537bfdc8"),
    "libtiff": ("https://download.osgeo.org/libtiff/tiff-4.7.2.tar.gz",
                "672bd7d10aee4606171afb864f3570b83340f6a33e2c186dc0512f7145ffdf6a"),
    "libjpeg-turbo": ("https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.1/libjpeg-turbo-3.2.1.tar.gz",
                      "c27ad72a68da151177929aa969ae773a2da5ac2af9ce946079314fdac8cece7f"),
}
PNGSUITE_EXTRA = {
    "PngSuite.LICENSE": "http://www.schaik.com/pngsuite/PngSuite.LICENSE",
    "PngSuite.README": "http://www.schaik.com/pngsuite/PngSuite.README",
}
# Gitiles builds archives on the fly, so the archive bytes are not stable; the commit is pinned instead and every file
# is hashed into the manifest.
LIBWEBP_COMMIT = "06ddd96e276c2c638a72d39d3c0f340afd61978c"
LIBWEBP_URL = f"https://chromium.googlesource.com/webm/libwebp-test-data/+archive/{LIBWEBP_COMMIT}.tar.gz"


# ---------------------------------------------------------------------------------------------------------------------
# Shared helpers
# ---------------------------------------------------------------------------------------------------------------------

def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read(path: str) -> bytes:
    with open(path, "rb") as fh:
        return fh.read()


def rgba_from_gray(v8: np.ndarray, alpha: np.ndarray | int = 255) -> np.ndarray:
    g = v8.astype(np.uint8)
    a = np.full(g.shape, alpha, np.uint8) if np.isscalar(alpha) else alpha.astype(np.uint8)
    return np.dstack([g, g, g, a])


def rgba_from_rgb(rgb8: np.ndarray, alpha: np.ndarray | int = 255) -> np.ndarray:
    a = np.full(rgb8.shape[:2], alpha, np.uint8) if np.isscalar(alpha) else alpha.astype(np.uint8)
    return np.dstack([rgb8.astype(np.uint8), a])


def pillow_rgba_frames(path: str) -> list[np.ndarray]:
    """Every frame as Pillow renders it in RGBA."""
    frames = []
    with Image.open(path) as im:
        for index in range(getattr(im, "n_frames", 1)):
            im.seek(index)
            frames.append(np.array(im.convert("RGBA")))
    return frames


class Suite:
    """Collects one folder's entries and writes the files, references and manifest."""

    def __init__(self, name: str, out_dir: str) -> None:
        self.name = name
        self.out_dir = out_dir
        self.entries: list[dict] = []
        if os.path.isdir(out_dir):
            for item in os.listdir(out_dir):
                path = os.path.join(out_dir, item)
                if os.path.isfile(path):
                    os.remove(path)
        os.makedirs(out_dir, exist_ok=True)

    def copy(self, source: str, file_name: str | None = None) -> str:
        target = os.path.join(self.out_dir, file_name or os.path.basename(source))
        shutil.copyfile(source, target)
        return target

    def add(self, name: str, source: str, fmt: str, *, frames: list[np.ndarray] | None, oracle: str, notes: str,
            tolerance: int | None = 0, min_psnr: float | None = None, expect: str | None = None,
            expect_direct: str | None = None, width: int | None = None, height: int | None = None,
            frame_count: int | None = None) -> None:
        data = read(source)
        file_name = os.path.basename(source)
        shutil.copyfile(source, os.path.join(self.out_dir, file_name))
        entry: dict = {"name": name, "file": file_name, "format": fmt}
        reference = None
        if frames:
            for f in frames:
                assert f.dtype == np.uint8 and f.ndim == 3 and f.shape[2] == 4, (name, f.shape, f.dtype)
                assert f.shape == frames[0].shape, f"{name}: frames of different sizes are not supported here"
            raw = b"".join(np.ascontiguousarray(f).tobytes() for f in frames)
            reference = name + ".rgba"
            if len(raw) > GZIP_ABOVE:
                reference += ".gz"
                with open(os.path.join(self.out_dir, reference), "wb") as fh:
                    # mtime=0 and no file name keep the wrapper free of build-time noise.
                    with gzip.GzipFile(filename="", mode="wb", fileobj=fh, compresslevel=9, mtime=0) as gz:
                        gz.write(raw)
            else:
                with open(os.path.join(self.out_dir, reference), "wb") as fh:
                    fh.write(raw)
            height, width = frames[0].shape[:2]
            frame_count = len(frames)
        entry.update({
            "width": width, "height": height, "frames": frame_count, "reference": reference,
            "tolerance": tolerance if reference else None, "min_psnr": min_psnr if reference else None,
            "expect": expect, "expect_direct": expect_direct,
            "contract_only": reference is None and expect is None,
            "oracle": oracle, "notes": notes, "sha256": sha256(data), "size": len(data),
        })
        self.entries.append(entry)

    def write_manifest(self) -> None:
        with open(os.path.join(self.out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as fh:
            json.dump(self.entries, fh, indent=1)
            fh.write("\n")


# ---------------------------------------------------------------------------------------------------------------------
# A PNG reader written from the specification (W3C PNG, third edition), used as the PngSuite oracle
# ---------------------------------------------------------------------------------------------------------------------

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


class SpecPng:
    """Raw samples plus the header facts the library contract needs."""

    def __init__(self, data: bytes) -> None:
        if data[:8] != PNG_SIGNATURE:
            raise ValueError("bad signature")
        pos = 8
        idat = bytearray()
        self.palette = None
        self.trns = None
        header = None
        while pos < len(data):
            length, kind = struct.unpack(">I4s", data[pos:pos + 8])
            body = data[pos + 8:pos + 8 + length]
            crc = struct.unpack(">I", data[pos + 8 + length:pos + 12 + length])[0]
            if zlib.crc32(kind + body) & 0xFFFFFFFF != crc:
                raise ValueError(f"CRC mismatch in {kind!r}")
            if kind == b"IHDR":
                header = struct.unpack(">IIBBBBB", body)
            elif kind == b"PLTE":
                self.palette = np.frombuffer(body, np.uint8).reshape(-1, 3)
            elif kind == b"tRNS":
                self.trns = body
            elif kind == b"IDAT":
                idat += body
            elif kind == b"IEND":
                break
            pos += 12 + length
        if header is None:
            raise ValueError("no IHDR")
        self.width, self.height, self.depth, self.ctype, _, _, self.interlace = header
        if self.ctype not in CHANNELS or not idat:
            raise ValueError("invalid colour type or no image data")
        self.samples = self._decode(zlib.decompress(bytes(idat)))

    def _decode(self, raw: bytes) -> np.ndarray:
        channels = CHANNELS[self.ctype]
        bits = channels * self.depth
        bpp = max(1, bits // 8)
        out = np.zeros((self.height, self.width, channels), np.int64)
        passes = ADAM7 if self.interlace else [(0, 0, 1, 1)]
        pos = 0
        for xs, ys, dx, dy in passes:
            pw = (self.width - xs + dx - 1) // dx if self.width > xs else 0
            ph = (self.height - ys + dy - 1) // dy if self.height > ys else 0
            if pw == 0 or ph == 0:
                continue
            row_bytes = (pw * bits + 7) // 8
            prev = bytearray(row_bytes)
            for r in range(ph):
                ftype = raw[pos]
                row = bytearray(raw[pos + 1:pos + 1 + row_bytes])
                pos += 1 + row_bytes
                self._unfilter(ftype, row, prev, bpp)
                out[ys + r * dy, xs::dx][:pw] = self._unpack(bytes(row), pw, channels)
                prev = row
        return out

    @staticmethod
    def _unfilter(ftype: int, row: bytearray, prev: bytearray, bpp: int) -> None:
        for i in range(len(row)):
            a = row[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if ftype == 0:
                predictor = 0
            elif ftype == 1:
                predictor = a
            elif ftype == 2:
                predictor = b
            elif ftype == 3:
                predictor = (a + b) // 2
            elif ftype == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                predictor = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
            else:
                raise ValueError(f"filter type {ftype}")
            row[i] = (row[i] + predictor) & 0xFF

    def _unpack(self, row: bytes, count: int, channels: int) -> np.ndarray:
        if self.depth == 16:
            return np.frombuffer(row, ">u2").astype(np.int64).reshape(-1, channels)[:count]
        if self.depth == 8:
            return np.frombuffer(row, np.uint8).astype(np.int64).reshape(-1, channels)[:count]
        bits = np.unpackbits(np.frombuffer(row, np.uint8))
        values = bits.reshape(-1, self.depth) @ (1 << np.arange(self.depth - 1, -1, -1))
        return values[:count * channels].reshape(-1, channels)

    def library_rgba(self) -> np.ndarray:
        """The RGBA8 pixels the library contract promises (see ../generate.py png_expected)."""
        s, d = self.samples, self.depth
        h, w = s.shape[:2]
        if self.ctype == 0:
            v = s[..., 0]
            v8 = (v >> 8) if d == 16 else v * (255 // ((1 << d) - 1))
            alpha = np.full((h, w), 255, np.uint8)
            if self.trns is not None:
                alpha = np.where(v == struct.unpack(">H", self.trns[:2])[0], 0, 255)
            return rgba_from_gray(v8, alpha)
        if self.ctype == 2:
            v8 = (s >> 8) if d == 16 else s
            alpha = np.full((h, w), 255, np.uint8)
            if self.trns is not None:
                kr, kg, kb = struct.unpack(">HHH", self.trns[:6])
                alpha = np.where((s[..., 0] == kr) & (s[..., 1] == kg) & (s[..., 2] == kb), 0, 255)
            return rgba_from_rgb(v8, alpha)
        if self.ctype == 3:
            idx = s[..., 0]
            pal_alpha = np.full(len(self.palette), 255, np.uint8)
            if self.trns is not None:
                t = np.frombuffer(self.trns, np.uint8)[:len(self.palette)]
                pal_alpha[:len(t)] = t
            return rgba_from_rgb(self.palette[idx], pal_alpha[idx])
        v = (s >> 8) if d == 16 else s
        if self.ctype == 4:
            return rgba_from_gray(v[..., 0], v[..., 1])
        return v.astype(np.uint8)


# ---------------------------------------------------------------------------------------------------------------------
# PngSuite
# ---------------------------------------------------------------------------------------------------------------------

# What each invalid-by-design file breaks (descriptions from the PngSuite page), and what the library must throw.
# A damaged signature means Image.Load cannot recognise the file as PNG at all; called directly, the PNG decoder must
# still reject it as malformed.
PNGSUITE_CORRUPT = {
    "xs1n0g01": ("signature byte 1 MSBit reset to zero", "UnknownImageFormatException"),
    "xs2n0g01": ("signature byte 2 is a 'Q'", "UnknownImageFormatException"),
    "xs4n0g01": ("signature byte 4 lowercase", "UnknownImageFormatException"),
    "xs7n0g01": ("7th byte a space instead of control-Z", "UnknownImageFormatException"),
    "xcrn0g04": ("added cr bytes", "UnknownImageFormatException"),
    "xlfn0g04": ("added lf bytes", "UnknownImageFormatException"),
    "xhdn0g08": ("incorrect IHDR checksum", "InvalidImageContentException"),
    "xc1n0g08": ("color type 1", "InvalidImageContentException"),
    "xc9n2c08": ("color type 9", "InvalidImageContentException"),
    "xd0n2c08": ("bit-depth 0", "InvalidImageContentException"),
    "xd3n2c08": ("bit-depth 3", "InvalidImageContentException"),
    "xd9n2c08": ("bit-depth 99", "InvalidImageContentException"),
    "xdtn0g01": ("missing IDAT chunk", "InvalidImageContentException"),
    "xcsn0g01": ("incorrect IDAT checksum", "InvalidImageContentException"),
}


def pillow_png_disagreement(spec: SpecPng) -> str | None:
    """Explains why Pillow's RGBA rendering is not a valid cross-check for this file, or None when it is."""
    if spec.ctype == 0 and spec.depth == 16:
        return "Pillow narrows 16-bit grayscale by clipping to 255 rather than keeping the high byte"
    if spec.trns is not None and spec.depth == 16:
        return "Pillow applies a 16-bit colour key after narrowing to 8 bits, the specification compares raw samples"
    if spec.trns is not None and spec.ctype == 0 and spec.depth < 8:
        return "Pillow compares a sub-byte gray colour key with the sample after scaling it to 0..255"
    return None


def build_pngsuite(source: str, extras: dict[str, str]) -> Suite:
    suite = Suite("pngsuite", os.path.join(HERE, "pngsuite"))
    for name, path in extras.items():
        suite.copy(path, name)
    pngs = sorted(f for f in os.listdir(source) if f.endswith(".png"))
    for file_name in pngs:
        name = file_name[:-4]
        path = os.path.join(source, file_name)
        data = read(path)
        if name in PNGSUITE_CORRUPT:
            what, expect = PNGSUITE_CORRUPT[name]
            # The spec reader must agree that the file is broken, or the expectation is not trustworthy. Pillow is
            # only reported: it does not verify IDAT CRCs, so it decodes xcsn0g01.
            try:
                SpecPng(data)
            except Exception:  # noqa: BLE001 - any failure is what we want to see here
                pass
            else:
                raise AssertionError(f"{name}: the spec reader accepted a file PngSuite marks as corrupt")
            try:
                pillow_rgba_frames(path)
                pillow_verdict = "Pillow decodes it anyway"
            except Exception:  # noqa: BLE001
                pillow_verdict = "Pillow rejects it too"
            suite.add(name, path, "png", frames=None, oracle="PngSuite (invalid by design); spec reader rejects it",
                      notes=f"invalid by design: {what}; {pillow_verdict}", expect=expect,
                      expect_direct="InvalidImageContentException")
            continue

        spec = SpecPng(data)
        expected = spec.library_rgba()
        pillow = pillow_rgba_frames(path)[0]
        reason = pillow_png_disagreement(spec)
        if np.array_equal(pillow, expected):
            oracle = "spec reader; Pillow agrees exactly"
        elif reason is not None and spec.ctype == 0 and spec.depth == 16:
            # Pillow's RGBA rendering clips, but its raw I;16 samples are still a valid second opinion.
            with Image.open(path) as im:
                raw16 = np.array(im).astype(np.int64)
            if not np.array_equal((raw16 >> 8).astype(np.uint8), expected[..., 0]):
                raise AssertionError(f"{name}: Pillow's raw 16-bit samples disagree with the spec reader")
            oracle = f"spec reader; Pillow's raw 16-bit samples agree ({reason})"
        elif reason is not None:
            oracle = f"spec reader; Pillow not comparable ({reason})"
        else:
            diff = int(np.count_nonzero(np.any(pillow != expected, axis=2)))
            raise AssertionError(f"{name}: Pillow and the spec reader disagree on {diff} pixel(s) with no known reason")
        notes = (f"PngSuite {name}: {spec.width}x{spec.height}, colour type {spec.ctype}, {spec.depth}-bit"
                 f"{', Adam7' if spec.interlace else ''}{', tRNS' if spec.trns is not None else ''}")
        suite.add(name, path, "png", frames=[expected], oracle=oracle, notes=notes)
    suite.write_manifest()
    return suite


# ---------------------------------------------------------------------------------------------------------------------
# libtiff test images
# ---------------------------------------------------------------------------------------------------------------------

# The synthetic images of libtiff's test/images. Left out: the old-style JPEG photographs (ojpeg_*), whose subject
# matter was contributed and whose licence is not stated beyond the release's; deflate-last-strip-extra-data.tiff,
# which the README credits to GDAL's autotest data rather than to libtiff; lzw-single-strip.tiff (7795x3122) and the
# non-TIFF companions (.pgm/.ppm/.pbm/.g3).
LIBTIFF_FILES = {
    "minisblack-1c-8b.tiff": "8-bit BlackIsZero",
    "minisblack-1c-16b.tiff": "16-bit BlackIsZero, big-endian",
    "miniswhite-1c-1b.tiff": "1-bit WhiteIsZero",
    "palette-1c-1b.tiff": "1-bit palette",
    "palette-1c-4b.tiff": "4-bit palette",
    "palette-1c-8b.tiff": "8-bit palette",
    "rgb-3c-8b.tiff": "8-bit RGB",
    "rgb-3c-16b.tiff": "16-bit RGB",
    "minisblack-2c-8b-alpha.tiff": "8-bit gray with an extra (alpha) sample",
    "logluv-3c-16b.tiff": "SGI LogLuv compression and photometric",
    "quad-lzw-compat.tiff": "LZW written in the pre-6.0 'compat' bit order",
    "quad-tile.jpg.tiff": "tiled JPEG-in-TIFF (compression 7)",
    "32bpp-None.tiff": "32 bits per pixel, uncompressed",
    "32bpp-None-jpeg.tiff": "32 bits per pixel stored with PackBits",
    "custom_dir_EXIF_GPS.tiff": "1x1 with EXIF and GPS private IFDs",
    "test_two_ifds.tif": "two pages",
    "tiff_with_subifd_chain.tif": "two pages plus a SubIFD chain",
    "test_ifd_loop_to_self.tif": "malformed: the IFD chain points back to itself",
    "test_ifd_loop_to_first.tif": "malformed: the second IFD points back to the first",
    "test_ifd_loop_subifd.tif": "malformed: a SubIFD chain that loops",
    "test_float64_predictor2_le_lzw.tif": "64-bit float samples, horizontal predictor, LZW, little-endian",
    "test_float64_predictor2_be_lzw.tif": "64-bit float samples, horizontal predictor, LZW, big-endian",
    "testfax3_bug_513.tiff": "CCITT Group 3",
    "testfax3_bug54_1dnoEOL.tif": "CCITT Group 3, 1-D without EOL codes",
    "testfax4.tiff": "CCITT Group 4, a full 2453x3369 page",
    "webp_lossless_rgba_alpha_fully_opaque.tif": "WebP-in-TIFF (compression 50001)",
}

# Files whose IFD chain is malformed: the frame count is whatever a reader chooses when it detects the loop, so only
# the decoder contract (no hang, no escaped exception) is asserted, not pixels.
LIBTIFF_CONTRACT_ONLY = {"test_ifd_loop_to_self.tif", "test_ifd_loop_to_first.tif", "test_ifd_loop_subifd.tif"}


def tiff_reference(path: str) -> tuple[list[np.ndarray] | None, str]:
    """Returns (frames, oracle) or (None, why-not) for one libtiff test image."""
    import tifffile

    try:
        with Image.open(path) as im:
            mode = im.mode
        pillow = pillow_rgba_frames(path)
    except Exception as ex:  # noqa: BLE001
        return None, f"no independent reference: Pillow cannot decode it ({type(ex).__name__})"

    with tifffile.TiffFile(path) as tif:
        page = tif.pages[0]
        bits = page.bitspersample
        photometric = int(page.photometric)
        samples = page.samplesperpixel
        if bits == 16 and page.sampleformat == 1:
            raw = page.asarray()
            if photometric == 1 and samples == 1:      # BlackIsZero
                expected = rgba_from_gray(raw >> 8)
            elif photometric == 2 and samples == 3:    # RGB
                expected = rgba_from_rgb(raw >> 8)
            else:
                return None, f"no reference: 16-bit photometric {photometric} with {samples} samples is not mapped here"
            # Pillow narrows 16-bit RGB by keeping the high byte as well, so it must agree; for 16-bit gray it clips.
            if mode != "I;16B" and not np.array_equal(pillow[0], expected):
                raise AssertionError(f"{path}: tifffile and Pillow disagree on a 16-bit page")
            return [expected], "tifffile raw samples, high byte kept" + ("" if mode == "I;16B" else "; Pillow agrees")
    return pillow, f"Pillow {Image.__version__} (libtiff {features.version('libtiff')})"


def tiff_has_associated_alpha(path: str) -> bool:
    import tifffile

    with tifffile.TiffFile(path) as tif:
        return any(int(v) == 1 for v in (tif.pages[0].extrasamples or ()))


def tiff_is_jpeg_compressed(path: str) -> bool:
    import tifffile

    with tifffile.TiffFile(path) as tif:
        return int(tif.pages[0].compression) in (6, 7)


def build_libtiff(source: str) -> Suite:
    suite = Suite("libtiff", os.path.join(HERE, "libtiff"))
    images = os.path.join(source, "test", "images")
    suite.copy(os.path.join(source, "LICENSE.md"))
    suite.copy(os.path.join(images, "README.txt"), "README.libtiff-test-images.txt")
    for file_name, what in LIBTIFF_FILES.items():
        path = os.path.join(images, file_name)
        name = os.path.splitext(file_name)[0].replace(".", "_")
        if file_name in LIBTIFF_CONTRACT_ONLY:
            suite.add(name, path, "tiff", frames=None, oracle="none (malformed IFD chain)", notes=what)
            continue
        frames, oracle = tiff_reference(path)
        tolerance = 0
        if frames is not None and tiff_has_associated_alpha(path):
            # Premultiplied (ExtraSamples = 1) colour must be divided by alpha to become straight RGBA; Pillow does so,
            # and a decoder that rounds the division the other way is still correct, hence one level of slack.
            tolerance = 1
            what += "; associated (premultiplied) alpha, so the reference is un-premultiplied"
        if frames is not None and tiff_is_jpeg_compressed(path):
            # JPEG-in-TIFF is lossy: a compliant IDCT and upsampler may round differently from libjpeg-turbo, so it is
            # held to the same PSNR floor as the standalone JPEG suites rather than to an exact match.
            suite.add(name, path, "tiff", frames=frames, oracle=oracle, notes=what, tolerance=None, min_psnr=40.0)
            continue
        suite.add(name, path, "tiff", frames=frames, oracle=oracle, notes=what, tolerance=tolerance)
    suite.write_manifest()
    return suite


# ---------------------------------------------------------------------------------------------------------------------
# libjpeg-turbo test images
# ---------------------------------------------------------------------------------------------------------------------

def pnm_16bit_reference(path: str) -> np.ndarray:
    """A binary 16-bit P5/P6, scaled the way the library documents: round(v * 255 / maxval) in integers."""
    data = read(path)
    tokens, pos = [], 0
    while len(tokens) < 4:
        while data[pos:pos + 1].isspace():
            pos += 1
        if data[pos:pos + 1] == b"#":
            pos = data.index(b"\n", pos) + 1
            continue
        start = pos
        while not data[pos:pos + 1].isspace():
            pos += 1
        tokens.append(data[start:pos])
    pos += 1
    magic, w, h, maxval = tokens[0], int(tokens[1]), int(tokens[2]), int(tokens[3])
    channels = 1 if magic == b"P5" else 3
    raw = np.frombuffer(data[pos:pos + w * h * channels * 2], ">u2").astype(np.int64).reshape(h, w, channels)
    v8 = ((raw * 255) + (maxval // 2)) // maxval
    return rgba_from_gray(v8[..., 0]) if channels == 1 else rgba_from_rgb(v8)


LIBJPEG_TURBO_FILES = {
    "testorig.jpg": ("jpeg", "IJG reference image: baseline, 4:2:0"),
    "testimgint.jpg": ("jpeg", "IJG reference image: progressive"),
    "testimgari.jpg": ("jpeg", "IJG reference image: arithmetic coding"),
    "monkey12.jpg": ("jpeg", "12-bit precision JPEG with an embedded sRGB ICC profile"),
    "testorig.png": ("png", "8-bit grayscale PNG"),
    "monkey16.png": ("png", "16-bit RGB PNG with an embedded sRGB ICC profile"),
    "vgl_5674_0098.png": ("png", "8-bit RGB PNG with an embedded sRGB ICC profile"),
    "testorig.ppm": ("pnm", "binary PPM, maxval 255"),
    "monkey16.pgm": ("pnm", "binary PGM, maxval 65535"),
    "shira_bird8.bmp": ("bmp", "BMP"),
    "vgl_6434_0018a.bmp": ("bmp", "BMP"),
    "vgl_6548_0026a.bmp": ("bmp", "BMP"),
}


def build_libjpeg_turbo(source: str) -> Suite:
    suite = Suite("libjpeg-turbo", os.path.join(HERE, "libjpeg-turbo"))
    images = os.path.join(source, "testimages")
    suite.copy(os.path.join(images, "LICENSE.txt"), "LICENSE.testimages.txt")
    suite.copy(os.path.join(source, "LICENSE.md"))
    suite.copy(os.path.join(source, "README.ijg"))
    for file_name, (fmt, what) in LIBJPEG_TURBO_FILES.items():
        path = os.path.join(images, file_name)
        # The suite holds the same picture in several formats (testorig.jpg/.png/.ppm), so the extension stays in the name.
        name = file_name.replace(".", "_")
        if fmt == "jpeg":
            try:
                frames = pillow_rgba_frames(path)
            except Exception as ex:  # noqa: BLE001
                suite.add(name, path, fmt, frames=None,
                          oracle=f"none: Pillow cannot decode it ({type(ex).__name__})", notes=what)
                continue
            # A compliant IDCT may round differently, so JPEG is compared by PSNR, as JpegDecoderTests does.
            suite.add(name, path, fmt, frames=frames, tolerance=None, min_psnr=40.0,
                      oracle=f"Pillow {Image.__version__} (libjpeg-turbo {features.version('jpg')})", notes=what)
            continue
        if file_name == "monkey16.png":
            frames = [SpecPng(read(path)).library_rgba()]
            if not np.array_equal(frames[0], pillow_rgba_frames(path)[0]):
                raise AssertionError("monkey16.png: Pillow and the spec reader disagree")
            suite.add(name, path, fmt, frames=frames, oracle="spec reader, high byte kept; Pillow agrees", notes=what)
            continue
        if file_name == "monkey16.pgm":
            suite.add(name, path, fmt, frames=[pnm_16bit_reference(path)],
                      oracle="NumPy from the raw samples, round(v * 255 / maxval)", notes=what)
            continue
        suite.add(name, path, fmt, frames=pillow_rgba_frames(path), oracle=f"Pillow {Image.__version__}", notes=what)
    suite.write_manifest()
    return suite


# ---------------------------------------------------------------------------------------------------------------------
# libwebp-test-data (built locally, never committed)
# ---------------------------------------------------------------------------------------------------------------------

def dwebp_pam(rgba: np.ndarray) -> bytes:
    """The bytes `dwebp -pam` writes for an RGBA image (libwebp examples/../imageio/image_enc.c)."""
    h, w = rgba.shape[:2]
    header = f"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n".encode("ascii")
    return header + np.ascontiguousarray(rgba).tobytes()


def build_libwebp(source: str) -> Suite:
    suite = Suite("libwebp", os.path.join(HERE, "libwebp"))
    md5s = {}
    md5_path = os.path.join(source, "libwebp_tests.md5")
    if os.path.exists(md5_path):
        for line in open(md5_path, encoding="ascii"):
            parts = line.split()
            if len(parts) == 2:
                md5s[parts[1]] = parts[0]
    for file_name in sorted(f for f in os.listdir(source) if f.endswith(".webp")):
        path = os.path.join(source, file_name)
        name = file_name[:-5]
        data = read(path)
        lossless = b"VP8L" in data[:64] or (b"VP8X" in data[:16] and b"VP8L" in data)
        try:
            frames = pillow_rgba_frames(path)
        except Exception as ex:  # noqa: BLE001
            suite.add(name, path, "webp", frames=None, oracle=f"none: Pillow cannot decode it ({type(ex).__name__})",
                      notes="libwebp-test-data")
            continue
        cross = "no dwebp MD5 published"
        expected_md5 = md5s.get(file_name + ".pam")
        if expected_md5:
            cross = "dwebp -pam MD5 matches" if hashlib.md5(dwebp_pam(frames[0])).hexdigest() == expected_md5 \
                else "dwebp -pam MD5 differs from Pillow's decode"
        oracle = f"Pillow {Image.__version__} (libwebp {features.version('webp')}); {cross}"
        if lossless:
            suite.add(name, path, "webp", frames=frames, oracle=oracle, notes="libwebp-test-data, lossless")
        else:
            # The same allowance WebpTests gives lossy fixtures: libwebp's SIMD paths may round a shade differently.
            suite.add(name, path, "webp", frames=frames, tolerance=3, min_psnr=40.0, oracle=oracle,
                      notes="libwebp-test-data, lossy")
    suite.write_manifest()
    return suite


# ---------------------------------------------------------------------------------------------------------------------
# Provenance, download and check
# ---------------------------------------------------------------------------------------------------------------------

LICENCES = {
    "pngsuite": ("PngSuite 2017jul19, Willem van Schaik", "http://www.schaik.com/pngsuite/",
                 "PngSuite licence (see pngsuite/PngSuite.LICENSE): \"Permission to use, copy, modify and distribute "
                 "these images for any purpose and without fee is hereby granted.\""),
    "libtiff": ("libtiff 4.7.2, test/images", "https://download.osgeo.org/libtiff/",
                "LibTIFF licence (libtiff/LICENSE.md, a permissive BSD-style notice). test/images states no licence "
                "of its own; the images are part of the libtiff source release, which LICENSE.md covers. Only the "
                "synthetic images are taken (see build_conformance.py LIBTIFF_FILES for what is left out and why)."),
    "libjpeg-turbo": ("libjpeg-turbo 3.2.1, testimages", "https://github.com/libjpeg-turbo/libjpeg-turbo/releases/tag/3.2.1",
                      "Per testimages/LICENSE.txt (libjpeg-turbo/LICENSE.testimages.txt): testorig* and testimg* are "
                      "under the IJG License (README.ijg); the other images are under the Modified (3-clause) BSD "
                      "License (LICENSE.md); the embedded sRGB2014.icc profile is the ICC's freely redistributable "
                      "profile."),
}


def write_provenance(suites: list[Suite]) -> None:
    lines = [
        "# Conformance corpus provenance",
        "",
        "Generated by `build_conformance.py`; do not edit by hand. Every file below was downloaded from the suite's",
        "official release into a fresh, empty directory and copied here unmodified. `sha256` is the digest of the",
        "committed file. Archive digests are pinned in `build_conformance.py` (`ARCHIVES`).",
        "",
    ]
    for suite in suites:
        title, url, licence = LICENCES[suite.name]
        archive = ARCHIVES.get(suite.name)
        lines += [f"## {suite.name}", "", f"- Source: {title}", f"- URL: {url}"]
        if archive:
            lines.append(f"- Archive: {archive[0]} (SHA-256 `{archive[1]}`)")
        lines += [f"- Licence: {licence}", "", "| File | Bytes | SHA-256 | Reference |", "| --- | ---: | --- | --- |"]
        for e in suite.entries:
            reference = e["reference"] or ("(must throw " + e["expect"] + ")" if e["expect"] else "(contract only)")
            lines.append(f"| `{e['file']}` | {e['size']} | `{e['sha256']}` | {reference} |")
        lines.append("")
    with open(os.path.join(HERE, "PROVENANCE.md"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines))


def download(url: str, directory: str, expected_sha256: str | None) -> str:
    target = os.path.join(directory, url.rsplit("/", 1)[-1])
    with urllib.request.urlopen(url, timeout=120) as response, open(target, "wb") as fh:
        shutil.copyfileobj(response, fh)
    if expected_sha256 is not None and sha256(read(target)) != expected_sha256:
        raise SystemExit(f"{url}: SHA-256 {sha256(read(target))} does not match the pinned {expected_sha256}")
    return target


def extract(archive: str, directory: str) -> str:
    with tarfile.open(archive) as tar:
        for member in tar.getmembers():
            resolved = os.path.realpath(os.path.join(directory, member.name))
            if not resolved.startswith(os.path.realpath(directory) + os.sep) or member.issym() or member.islnk():
                raise SystemExit(f"{archive}: refusing unsafe member {member.name}")
        tar.extractall(directory)
    return directory


def check() -> int:
    """Re-derives every reference from the committed third-party files and compares it with the committed one."""
    failures = 0
    for folder in ("pngsuite", "libtiff", "libjpeg-turbo", "libwebp"):
        manifest_path = os.path.join(HERE, folder, "manifest.json")
        if not os.path.exists(manifest_path):
            continue
        entries = json.load(open(manifest_path, encoding="utf-8"))
        for e in entries:
            path = os.path.join(HERE, folder, e["file"])
            if sha256(read(path)) != e["sha256"]:
                print(f"DIFF {folder}/{e['file']}: file digest changed")
                failures += 1
            if not e["reference"]:
                continue
            blob = read(os.path.join(HERE, folder, e["reference"]))
            committed = gzip.decompress(blob) if e["reference"].endswith(".gz") else blob
            if folder == "pngsuite" or e["file"] == "monkey16.png":
                derived = [SpecPng(read(path)).library_rgba()]
            elif e["file"] == "monkey16.pgm":
                derived = [pnm_16bit_reference(path)]
            elif folder == "libtiff":
                derived, _ = tiff_reference(path)
            else:
                derived = pillow_rgba_frames(path)
            if b"".join(np.ascontiguousarray(f).tobytes() for f in derived) != committed:
                print(f"DIFF {folder}/{e['name']}: re-derived reference differs from the committed one")
                failures += 1
    print("conformance references:", "OK" if failures == 0 else f"{failures} difference(s)")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--download", action="store_true", help="fetch the pinned official archives into a fresh temp dir")
    parser.add_argument("--pngsuite", help="directory holding the extracted PngSuite PNGs")
    parser.add_argument("--pngsuite-license", help="PngSuite.LICENSE (with --pngsuite)")
    parser.add_argument("--pngsuite-readme", help="PngSuite.README (with --pngsuite)")
    parser.add_argument("--libtiff", help="an extracted libtiff source release (the directory holding LICENSE.md)")
    parser.add_argument("--libjpeg-turbo", dest="libjpeg_turbo", help="an extracted libjpeg-turbo source release")
    parser.add_argument("--libwebp", nargs="?", const="download", metavar="DIR",
                        help="also build libwebp/ (git-ignored) from DIR, or download it when DIR is omitted")
    parser.add_argument("--check", action="store_true", help="only re-derive and compare the committed references")
    args = parser.parse_args(argv)

    print(f"Pillow {Image.__version__}, NumPy {np.__version__}, Python {sys.version.split()[0]}")
    if args.check:
        return check()

    scratch = None
    try:
        if args.download:
            scratch = tempfile.mkdtemp(prefix="eis-conformance-")
            for suite_name, (url, digest) in ARCHIVES.items():
                directory = os.path.join(scratch, suite_name)
                os.makedirs(directory)
                print(f"downloading {url}")
                archive = download(url, directory, digest)
                extract(archive, os.path.join(directory, "x"))
            args.pngsuite = os.path.join(scratch, "pngsuite", "x")
            extras_dir = os.path.join(scratch, "pngsuite-extras")
            os.makedirs(extras_dir)
            args.pngsuite_license = download(PNGSUITE_EXTRA["PngSuite.LICENSE"], extras_dir, None)
            args.pngsuite_readme = download(PNGSUITE_EXTRA["PngSuite.README"], extras_dir, None)
            args.libtiff = os.path.join(scratch, "libtiff", "x", "tiff-4.7.2")
            args.libjpeg_turbo = os.path.join(scratch, "libjpeg-turbo", "x", "libjpeg-turbo-3.2.1")
        if args.libwebp == "download":
            scratch = scratch or tempfile.mkdtemp(prefix="eis-conformance-")
            directory = os.path.join(scratch, "libwebp")
            os.makedirs(directory)
            print(f"downloading {LIBWEBP_URL}")
            archive = download(LIBWEBP_URL, directory, None)
            args.libwebp = extract(archive, os.path.join(directory, "x"))

        suites = []
        if args.pngsuite:
            extras = {}
            for name, value in (("PngSuite.LICENSE", args.pngsuite_license), ("PngSuite.README", args.pngsuite_readme)):
                path = value or os.path.join(args.pngsuite, name)
                if not os.path.exists(path):
                    parser.error(f"{name} not found; pass --pngsuite-license/--pngsuite-readme or use --download")
                extras[name] = path
            suites.append(build_pngsuite(args.pngsuite, extras))
        if args.libtiff:
            suites.append(build_libtiff(args.libtiff))
        if args.libjpeg_turbo:
            suites.append(build_libjpeg_turbo(args.libjpeg_turbo))
        if not suites and not args.libwebp:
            parser.error("nothing to build: pass --download or the suite directories")
        if suites:
            write_provenance(suites)
        if args.libwebp:
            build_libwebp(args.libwebp)

        total = 0
        for root, _, files in os.walk(HERE):
            total += sum(os.path.getsize(os.path.join(root, f)) for f in files)
        print(f"conformance corpus on disk: {total / 1048576:.2f} MiB")
        return 0
    finally:
        if scratch:
            shutil.rmtree(scratch, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
