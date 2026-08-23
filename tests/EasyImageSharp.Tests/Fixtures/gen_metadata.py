#!/usr/bin/env python
"""Metadata fixtures (EXIF, orientation, DPI, ICC, XMP, PNG text, GIF frame facts) under Fixtures/metadata/.

Discovered and run by generate.py: gen_metadata(out_dir). Requires Python 3.11 + Pillow 11 + numpy.
Everything is deterministic (fixed patterns, fixed metadata, no timestamps), so re-running produces
byte-identical files. See metadata/EXPECTED.md (written by this script) for the per-file facts.
"""
from __future__ import annotations

import io
import json
import os
import struct
import zlib

import numpy as np
from PIL import Image, ImageOps, PngImagePlugin, TiffImagePlugin
from PIL.ExifTags import IFD

# ---------------------------------------------------------------------------------------------------
# Test card: 8x6 blocks of 8x8 pixels, each block a unique gray level. Being flat per 8x8 block, a
# quality-100 grayscale JPEG of it decodes bit-exactly in every conforming decoder (DC-only blocks),
# which is what makes the orientation fixtures verifiable pixel by pixel.
# ---------------------------------------------------------------------------------------------------

CARD_BLOCKS_X, CARD_BLOCKS_Y, BLOCK = 8, 6, 8
CARD_W, CARD_H = CARD_BLOCKS_X * BLOCK, CARD_BLOCKS_Y * BLOCK


def card_value(bx: int, by: int) -> int:
    return 20 + ((by * CARD_BLOCKS_X) + bx) * 4


def make_card() -> Image.Image:
    arr = np.zeros((CARD_H, CARD_W), dtype=np.uint8)
    for by in range(CARD_BLOCKS_Y):
        for bx in range(CARD_BLOCKS_X):
            arr[by * BLOCK:(by + 1) * BLOCK, bx * BLOCK:(bx + 1) * BLOCK] = card_value(bx, by)
    return Image.fromarray(arr, "L")


def rgba_bytes(im: Image.Image) -> bytes:
    return im.convert("RGBA").tobytes()


# ---------------------------------------------------------------------------------------------------
# Minimal hand-built EXIF (TIFF structure) writer covering every field type and every sub-IFD.
# ---------------------------------------------------------------------------------------------------

BYTE, ASCII, SHORT, LONG, RATIONAL, SBYTE, UNDEFINED, SSHORT, SLONG, SRATIONAL, FLOAT, DOUBLE = range(1, 13)
TYPE_SIZE = {BYTE: 1, ASCII: 1, SHORT: 2, LONG: 4, RATIONAL: 8, SBYTE: 1, UNDEFINED: 1, SSHORT: 2, SLONG: 4,
             SRATIONAL: 8, FLOAT: 4, DOUBLE: 8}


def encode_values(typ: int, values, endian: str) -> tuple[bytes, int]:
    """Encodes a value list for the given TIFF type; returns (bytes, count)."""
    if typ == ASCII:
        data = values.encode("utf-8") + b"\0"
        return data, len(data)
    if typ in (BYTE, UNDEFINED):
        data = bytes(values)
        return data, len(data)
    if typ == SBYTE:
        return struct.pack(f"{endian}{len(values)}b", *values), len(values)
    fmt = {SHORT: "H", LONG: "L", SSHORT: "h", SLONG: "l", FLOAT: "f", DOUBLE: "d"}.get(typ)
    if fmt:
        return struct.pack(f"{endian}{len(values)}{fmt}", *values), len(values)
    if typ == RATIONAL:
        return b"".join(struct.pack(f"{endian}LL", n, d) for n, d in values), len(values)
    if typ == SRATIONAL:
        return b"".join(struct.pack(f"{endian}ll", n, d) for n, d in values), len(values)
    raise ValueError(typ)


class Ifd:
    """An image file directory: entries {tag: (type, values)} plus sub-IFDs {pointer_tag: Ifd}."""

    def __init__(self) -> None:
        self.entries: dict[int, tuple[int, object]] = {}
        self.subs: dict[int, Ifd] = {}

    def add(self, tag: int, typ: int, values) -> "Ifd":
        self.entries[tag] = (typ, values)
        return self

    def measure(self, endian: str) -> int:
        size = 2 + 12 * (len(self.entries) + len(self.subs)) + 4
        for typ, values in self.entries.values():
            data, _ = encode_values(typ, values, endian)
            if len(data) > 4:
                size += (len(data) + 1) & ~1
        for sub in self.subs.values():
            size += sub.measure(endian)
        return size

    def serialize(self, base: int, next_ifd: int, endian: str) -> bytes:
        items = [(tag, e, None) for tag, e in self.entries.items()] + [(tag, None, s) for tag, s in self.subs.items()]
        items.sort(key=lambda t: t[0])
        n = len(items)
        table = bytearray(struct.pack(f"{endian}H", n))
        external = bytearray()
        data_pos = 2 + 12 * n + 4
        ext_total = 0
        for _, e, _ in items:
            if e is not None:
                data, _ = encode_values(e[0], e[1], endian)
                if len(data) > 4:
                    ext_total += (len(data) + 1) & ~1
        sub_blobs = bytearray()
        sub_pos = data_pos + ext_total
        for tag, e, s in items:
            if e is not None:
                data, count = encode_values(e[0], e[1], endian)
                if len(data) <= 4:
                    table += struct.pack(f"{endian}HHL", tag, e[0], count) + data.ljust(4, b"\0")
                else:
                    table += struct.pack(f"{endian}HHLL", tag, e[0], count, base + data_pos + len(external))
                    external += data
                    if len(data) & 1:
                        external += b"\0"
            else:
                size = s.measure(endian)
                table += struct.pack(f"{endian}HHLL", tag, LONG, 1, base + sub_pos)
                sub_blobs += s.serialize(base + sub_pos, 0, endian)
                sub_pos += size
        table += struct.pack(f"{endian}L", next_ifd)
        return bytes(table) + bytes(external) + bytes(sub_blobs)


def build_exif(ifd0: Ifd, endian: str, ifd1: Ifd | None = None, thumbnail: bytes | None = None) -> bytes:
    """Serializes IFD0 (with its sub-IFDs), optionally IFD1 + JPEG thumbnail, behind a TIFF header."""
    header = (b"II" if endian == "<" else b"MM") + struct.pack(f"{endian}HL", 42, 8)
    ifd0_size = ifd0.measure(endian)
    ifd1_offset = 8 + ifd0_size if ifd1 is not None else 0
    body = ifd0.serialize(8, ifd1_offset, endian)
    if ifd1 is not None:
        assert thumbnail is not None
        ifd1.add(0x0201, LONG, [0]).add(0x0202, LONG, [len(thumbnail)])
        ifd1_size = ifd1.measure(endian)
        ifd1.add(0x0201, LONG, [ifd1_offset + ifd1_size])
        body += ifd1.serialize(ifd1_offset, 0, endian) + thumbnail
    return header + body


def all_types_exif(endian: str, user_comment: bytes) -> bytes:
    """The 'all types' EXIF profile: every TIFF field type, IFD0/Exif/Interop/GPS/IFD1, unknown tags, thumbnail."""
    ifd0 = Ifd()
    ifd0.add(0x010E, ASCII, "All EXIF types")
    ifd0.add(0x010F, ASCII, "EasyImageSharp")
    ifd0.add(0x0110, ASCII, "Test Camera")
    ifd0.add(0x0112, SHORT, [1])
    ifd0.add(0x011A, RATIONAL, [(72, 1)])
    ifd0.add(0x011B, RATIONAL, [(72, 1)])
    ifd0.add(0x0128, SHORT, [2])
    ifd0.add(0x0131, ASCII, "gen_metadata.py")
    ifd0.add(0x0132, ASCII, "2026:08:18 12:34:56")
    ifd0.add(0x013B, ASCII, "Fixture Author")
    ifd0.add(0x8298, ASCII, "(c) 2026 EasyImageSharp")
    ifd0.add(0x9C9B, BYTE, "Title".encode("utf-16-le") + b"\0\0")
    # Unknown (private) tags exercising every remaining field type.
    ifd0.add(0xC001, BYTE, [1, 2, 3])
    ifd0.add(0xC002, SBYTE, [-1, 2, -3])
    ifd0.add(0xC003, SSHORT, [-1000, 1000])
    ifd0.add(0xC004, SLONG, [-123456])
    ifd0.add(0xC005, FLOAT, [1.5, -2.25])
    ifd0.add(0xC006, DOUBLE, [3.141592653589793])
    ifd0.add(0xC007, SRATIONAL, [(-1, 3), (5, 2)])
    ifd0.add(0xC008, UNDEFINED, b"\xde\xad\xbe\xef\x01")
    ifd0.add(0xC009, LONG, [1, 2, 3])
    ifd0.add(0xC00A, ASCII, "unknown ascii")
    ifd0.add(0xC00B, SHORT, [7])

    exif = Ifd()
    exif.add(0x829A, RATIONAL, [(1, 125)])
    exif.add(0x829D, RATIONAL, [(28, 10)])
    exif.add(0x8822, SHORT, [3])
    exif.add(0x8827, SHORT, [200, 400])
    exif.add(0x9000, UNDEFINED, b"0232")
    exif.add(0x9003, ASCII, "2026:08:18 12:34:50")
    exif.add(0x9201, SRATIONAL, [(6965784, 1000000)])
    exif.add(0x9204, SRATIONAL, [(-1, 3)])
    exif.add(0x920A, RATIONAL, [(50, 1)])
    exif.add(0x9286, UNDEFINED, user_comment)
    exif.add(0x9291, ASCII, "123")
    exif.add(0xA001, SHORT, [1])
    exif.add(0xA002, LONG, [16])
    exif.add(0xA003, SHORT, [16])          # SHORT-typed where the library exposes uint: exercises coercion
    exif.add(0xA300, UNDEFINED, b"\x03")
    exif.add(0xA405, SHORT, [75])
    exif.add(0xA434, ASCII, "50mm f/2.8")
    interop = Ifd()
    interop.add(0x0001, ASCII, "R98")
    interop.add(0x0002, UNDEFINED, b"0100")
    exif.subs[0xA005] = interop
    ifd0.subs[0x8769] = exif

    gps = Ifd()
    gps.add(0x0000, BYTE, [2, 3, 0, 0])
    gps.add(0x0001, ASCII, "N")
    gps.add(0x0002, RATIONAL, [(51, 1), (30, 1), (0, 1)])
    gps.add(0x0003, ASCII, "W")
    gps.add(0x0004, RATIONAL, [(0, 1), (7, 1), (3900, 100)])
    gps.add(0x0005, BYTE, [0])
    gps.add(0x0006, RATIONAL, [(100, 1)])
    gps.add(0x001D, ASCII, "2026:08:18")
    ifd0.subs[0x8825] = gps

    ifd1 = Ifd()
    ifd1.add(0x0103, SHORT, [6])
    ifd1.add(0x011A, RATIONAL, [(72, 1)])
    ifd1.add(0x011B, RATIONAL, [(72, 1)])
    ifd1.add(0x0128, SHORT, [2])
    thumb = io.BytesIO()
    make_card().resize((8, 6), Image.NEAREST).save(thumb, "JPEG", quality=50)
    return build_exif(ifd0, endian, ifd1, thumb.getvalue())


# ---------------------------------------------------------------------------------------------------
# Small helpers: minimal ICC profile, XMP packet, JPEG segment insertion, PNG chunk insertion.
# ---------------------------------------------------------------------------------------------------

def make_icc(description: str = "EasyImageSharp Test Profile") -> bytes:
    """A structurally valid v2 RGB monitor profile with a 'desc' tag (no colour data; passthrough only)."""
    desc_text = description.encode("ascii") + b"\0"
    desc_tag = b"desc" + b"\0\0\0\0" + struct.pack(">L", len(desc_text)) + desc_text
    desc_tag += b"\0" * ((4 - len(desc_tag) % 4) % 4)
    wtpt_tag = b"XYZ " + b"\0\0\0\0" + struct.pack(">lll", 63190, 65536, 54061)
    tags = [(b"desc", desc_tag), (b"wtpt", wtpt_tag)]
    table_size = 4 + 12 * len(tags)
    offset = 128 + table_size
    table = struct.pack(">L", len(tags))
    body = b""
    for sig, data in tags:
        table += sig + struct.pack(">LL", offset + len(body), len(data))
        body += data
    size = 128 + table_size + len(body)
    header = struct.pack(">L", size) + b"none" + bytes([2, 0x10, 0, 0]) + b"mntr" + b"RGB " + b"XYZ "
    header += struct.pack(">HHHHHH", 2026, 8, 18, 12, 0, 0) + b"acsp" + b"MSFT" + struct.pack(">L", 0)
    header += b"EIS " + b"test" + struct.pack(">LL", 0, 0) + struct.pack(">L", 0)
    header += struct.pack(">lll", 63190, 65536, 54061) + b"EIS " + b"\0" * 44
    assert len(header) == 128, len(header)
    return header + table + body


XMP_PACKET = (
    '<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>'
    '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
    '<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">'
    '<dc:title><rdf:Alt><rdf:li xml:lang="x-default">EasyImageSharp metadata fixture</rdf:li></rdf:Alt></dc:title>'
    '</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>'
).encode("utf-8")


def insert_jpeg_segments(jpeg: bytes, segments: list[tuple[int, bytes]]) -> bytes:
    """Inserts marker segments (marker byte, payload) right after SOI."""
    assert jpeg[:2] == b"\xff\xd8"
    out = bytearray(jpeg[:2])
    for marker, payload in segments:
        out += bytes([0xFF, marker]) + struct.pack(">H", len(payload) + 2) + payload
    out += jpeg[2:]
    return bytes(out)


def png_chunk(typ: bytes, data: bytes) -> bytes:
    return struct.pack(">L", len(data)) + typ + data + struct.pack(">L", zlib.crc32(typ + data) & 0xFFFFFFFF)


def insert_png_chunks_after_ihdr(png: bytes, chunks: list[bytes]) -> bytes:
    ihdr_end = 8 + 8 + 13 + 4
    return png[:ihdr_end] + b"".join(chunks) + png[ihdr_end:]


def gradient_rgb(w: int, h: int, seed: int) -> Image.Image:
    rng = np.random.default_rng(seed)
    base = rng.integers(0, 256, size=3)
    arr = np.zeros((h, w, 3), dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            arr[y, x] = ((base[0] + x * 9) % 256, (base[1] + y * 13) % 256, (base[2] + (x + y) * 5) % 256)
    return Image.fromarray(arr, "RGB")


# ---------------------------------------------------------------------------------------------------
# Generator
# ---------------------------------------------------------------------------------------------------

def gen_metadata(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    facts: dict[str, dict] = {}

    def path(name: str) -> str:
        return os.path.join(out_dir, name)

    def write(name: str, data: bytes) -> None:
        with open(path(name), "wb") as f:
            f.write(data)

    # 1. Orientation fixtures: the card as a quality-100 grayscale JPEG with EXIF Orientation 1..8.
    #    <name>.rgba holds Pillow's ImageOps.exif_transpose result (RGBA, row-major), i.e. the upright pixels.
    card = make_card()
    write("card.rgba", rgba_bytes(card))
    for orientation in range(1, 9):
        exif = Image.Exif()
        exif[0x0112] = orientation
        exif[0x010F] = "EasyImageSharp"
        exif[0x0131] = "gen_metadata.py"
        ex = exif.get_ifd(IFD.Exif)
        ex[0xA002] = CARD_W
        ex[0xA003] = CARD_H
        name = f"orient_{orientation}.jpg"
        card.save(path(name), "JPEG", quality=100, exif=exif.tobytes())
        with Image.open(path(name)) as reopened:
            assert reopened.mode == "L" and reopened.getexif()[0x0112] == orientation
            assert reopened.tobytes() == card.tobytes(), "quality-100 flat-block card must decode exactly"
            upright = ImageOps.exif_transpose(reopened)
            assert upright is not None
        write(f"orient_{orientation}.rgba", rgba_bytes(upright))
        upright.convert("L").save(path(f"orient_{orientation}.expected.png"), "PNG")
        facts[name] = {"orientation": orientation, "size": [CARD_W, CARD_H], "upright_size": list(upright.size)}

    # 2. All-types EXIF: little-endian in a JPEG APP1, big-endian in a PNG eXIf chunk (UNICODE user comment).
    photo = gradient_rgb(16, 16, seed=901)
    exif_le = all_types_exif("<", b"ASCII\0\0\0Hello EXIF")
    buf = io.BytesIO()
    photo.save(buf, "JPEG", quality=90)
    write("exif_alltypes.jpg", insert_jpeg_segments(buf.getvalue(), [(0xE1, b"Exif\0\0" + exif_le)]))
    exif_be = all_types_exif(">", b"UNICODE\0" + "Héllo 日本".encode("utf-16-be"))
    buf = io.BytesIO()
    photo.save(buf, "PNG")
    write("exif_alltypes_be.png", insert_png_chunks_after_ihdr(buf.getvalue(), [png_chunk(b"eXIf", exif_be)]))
    write("exif_alltypes_le.bin", exif_le)
    write("exif_alltypes_be.bin", exif_be)
    facts["exif_alltypes.jpg"] = {"byte_order": "II", "user_comment": "Hello EXIF"}
    facts["exif_alltypes_be.png"] = {"byte_order": "MM", "user_comment": "Héllo 日本"}

    # 3. Pillow-written EXIF (big-endian) with Exif + GPS sub-IFDs, in JPEG, PNG and (IFD0 only) TIFF.
    exif = Image.Exif()
    exif[0x010F] = "EasyImageSharp"
    exif[0x0110] = "Pillow Writer"
    exif[0x0112] = 1
    exif[0x0131] = "gen_metadata.py"
    exif[0x0132] = "2026:08:18 12:34:56"
    exif[0x013B] = "Fixture Author"
    exif[0x8298] = "(c) 2026"
    exif[0x010E] = "Pillow EXIF fixture"
    ex = exif.get_ifd(IFD.Exif)
    ex[0x829A] = TiffImagePlugin.IFDRational(1, 125)
    ex[0x829D] = TiffImagePlugin.IFDRational(28, 10)
    ex[0x8827] = (200,)
    ex[0x9000] = b"0232"
    ex[0x9003] = "2026:08:18 12:34:50"
    ex[0x9204] = TiffImagePlugin.IFDRational(-1, 3)
    ex[0x920A] = TiffImagePlugin.IFDRational(50, 1)
    ex[0x9286] = b"ASCII\0\0\0Pillow comment"
    ex[0xA001] = 1
    ex[0xA002] = 16
    ex[0xA003] = 16
    ex[0xA434] = "50mm f/2.8"
    gps = exif.get_ifd(IFD.GPSInfo)
    gps[0] = b"\x02\x03\x00\x00"
    gps[1] = "N"
    gps[2] = (TiffImagePlugin.IFDRational(51, 1), TiffImagePlugin.IFDRational(30, 1), TiffImagePlugin.IFDRational(0, 1))
    gps[3] = "W"
    gps[4] = (TiffImagePlugin.IFDRational(0, 1), TiffImagePlugin.IFDRational(7, 1), TiffImagePlugin.IFDRational(39, 1))
    gps[5] = b"\x00"
    gps[6] = TiffImagePlugin.IFDRational(100, 1)
    pillow_exif = exif.tobytes()
    write("exif_pillow.bin", pillow_exif)
    photo.save(path("exif_pillow.jpg"), "JPEG", quality=90, exif=pillow_exif, dpi=(300, 300))
    photo.save(path("exif_pillow.png"), "PNG", exif=pillow_exif, dpi=(150, 100))
    photo.save(path("exif_pillow.tif"), "TIFF", exif=exif, dpi=(200, 200), description="TIFF description", software="gen_metadata.py")
    facts["exif_pillow.jpg"] = {"byte_order": "MM", "dpi": [300, 300]}
    facts["exif_pillow.png"] = {"byte_order": "MM", "dpi": [150, 100], "phys_ppm": [5906, 3937]}
    facts["exif_pillow.tif"] = {"dpi": [200, 200], "ifd0_only": True}

    # 4. Resolution-only fixtures.
    photo.save(path("dpi_300.jpg"), "JPEG", quality=85, dpi=(300, 300))
    photo.save(path("dpi_150x100.png"), "PNG", dpi=(150, 100))
    photo.save(path("dpi_200.tif"), "TIFF", dpi=(200, 200))
    photo.save(path("dpi_96x120.bmp"), "BMP", dpi=(96, 120))
    photo.save(path("dpi_none.png"), "PNG")
    facts["dpi_300.jpg"] = {"dpi": [300, 300], "jfif_units": 1}
    facts["dpi_150x100.png"] = {"dpi": [150, 100], "phys_ppm": [5906, 3937]}
    facts["dpi_200.tif"] = {"dpi": [200, 200]}
    facts["dpi_96x120.bmp"] = {"dpi": [96, 120], "ppm": [3780, 4724]}
    facts["dpi_none.png"] = {"dpi": None}

    # 5. ICC profile (identical bytes) in JPEG, PNG and TIFF.
    icc = make_icc()
    write("icc_profile.bin", icc)
    photo.save(path("icc.jpg"), "JPEG", quality=85, icc_profile=icc)
    photo.save(path("icc.png"), "PNG", icc_profile=icc)
    photo.save(path("icc.tif"), "TIFF", icc_profile=icc)
    facts["icc.jpg"] = facts["icc.png"] = facts["icc.tif"] = {"icc_bytes": len(icc), "icc_description": "EasyImageSharp Test Profile"}

    # 6. XMP packet (identical bytes) in JPEG, PNG and TIFF.
    write("xmp_packet.xml", XMP_PACKET)
    photo.save(path("xmp.jpg"), "JPEG", quality=85, xmp=XMP_PACKET)
    buf = io.BytesIO()
    photo.save(buf, "PNG")
    # Pillow ignores `xmp=` when saving PNG, so the "XML:com.adobe.xmp" iTXt chunk is inserted by hand.
    itxt = b"XML:com.adobe.xmp" + bytes(5) + XMP_PACKET
    write("xmp.png", insert_png_chunks_after_ihdr(buf.getvalue(), [png_chunk(b"iTXt", itxt)]))
    photo.save(path("xmp.tif"), "TIFF", tiffinfo={700: XMP_PACKET})
    facts["xmp.jpg"] = facts["xmp.png"] = facts["xmp.tif"] = {"xmp_bytes": len(XMP_PACKET)}

    # 7. PNG text chunks: tEXt, zTXt, iTXt (with language tag) and a hand-inserted gAMA.
    info = PngImagePlugin.PngInfo()
    info.add_text("Title", "Metadata fixture")
    info.add_text("Author", "EasyImageSharp")
    info.add_text("Description", "z" * 300, zip=True)
    info.add_itxt("Comment", "Grüße 日本", lang="de", tkey="Kommentar")
    buf = io.BytesIO()
    photo.save(buf, "PNG", pnginfo=info)
    write("text.png", insert_png_chunks_after_ihdr(buf.getvalue(), [png_chunk(b"gAMA", struct.pack(">L", 45455))]))
    facts["text.png"] = {"texts": {"Title": "Metadata fixture", "Author": "EasyImageSharp", "Description": "z*300",
                                   "Comment": "Grüße 日本 (de, Kommentar)"}, "gamma": 0.45455}

    # 8. JPEG comment segments and quality (standard tables scaled by Pillow/libjpeg).
    photo.save(path("comment_q75.jpg"), "JPEG", quality=75, comment=b"First comment")
    photo.save(path("q50_progressive.jpg"), "JPEG", quality=50, progressive=True)
    facts["comment_q75.jpg"] = {"quality": 75, "comments": ["First comment"], "progressive": False}
    facts["q50_progressive.jpg"] = {"quality": 50, "progressive": True}

    # 9. Multi-page TIFF with per-page description and resolution, big-endian, hand-built.
    def tiff_page(width: int, height: int, gray: bytes, description: str, dpi: int) -> Ifd:
        ifd = Ifd()
        ifd.add(256, LONG, [width]).add(257, LONG, [height]).add(258, SHORT, [8]).add(259, SHORT, [1])
        ifd.add(262, SHORT, [1]).add(270, ASCII, description).add(277, SHORT, [1]).add(278, LONG, [height])
        ifd.add(279, LONG, [len(gray)]).add(282, RATIONAL, [(dpi, 1)]).add(283, RATIONAL, [(dpi, 1)]).add(284, SHORT, [1])
        ifd.add(296, SHORT, [2]).add(305, ASCII, "gen_metadata.py")
        return ifd

    pages = []
    for i, (w, h, dpi) in enumerate([(12, 8, 100), (9, 7, 250)]):
        arr = np.fromfunction(lambda y, x, i=i: (x * 17 + y * 29 + i * 50) % 256, (h, w), dtype=int).astype(np.uint8)
        pages.append((w, h, arr.tobytes(), f"Page {i + 1}", dpi, arr))
    endian = ">"
    out = bytearray(b"MM" + struct.pack(">HL", 42, 0))
    ifds = []
    data_offsets = []
    for w, h, gray, desc, dpi, _ in pages:
        if len(out) & 1:
            out += b"\0"
        data_offsets.append(len(out))
        out += gray
        ifds.append(tiff_page(w, h, gray, desc, dpi))
    ifd_offsets = []
    cursor = len(out) + (len(out) & 1)
    for ifd in ifds:
        ifd_offsets.append(cursor)
        cursor += ifd.measure(endian) + 12  # +12: the StripOffsets entry is added below (inline value)
    body = bytearray()
    for i, ifd in enumerate(ifds):
        ifd.add(273, LONG, [data_offsets[i]])
        blob = ifd.serialize(ifd_offsets[i], ifd_offsets[i + 1] if i + 1 < len(ifds) else 0, endian)
        assert len(blob) == ifd.measure(endian) and ifd_offsets[i] == len(out) + len(body) + (len(out) & 1)
        body += blob
    if len(out) & 1:
        out += b"\0"
    out += body
    struct.pack_into(">L", out, 4, ifd_offsets[0])
    write("multipage_meta.tif", bytes(out))
    write("multipage_meta.rgba", b"".join(rgba_bytes(Image.fromarray(p[5], "L")) for p in pages))
    with Image.open(path("multipage_meta.tif")) as t:
        assert t.n_frames == 2 and t.tag_v2[270] == "Page 1"
        t.seek(1)
        assert t.tag_v2[270] == "Page 2" and float(t.tag_v2[282]) == 250
    facts["multipage_meta.tif"] = {"pages": [{"description": p[3], "dpi": p[4], "size": [p[0], p[1]]} for p in pages],
                                   "byte_order": "MM"}

    # 10. Animated GIF with loop count, per-frame delays/disposal, transparency and a comment.
    palette = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0)]
    frames = []
    for i in range(3):
        im = Image.new("P", (16, 12), i)
        flat = []
        for r, g, b in palette:
            flat.extend((r, g, b))
        im.putpalette(flat)
        px = im.load()
        for y in range(4):
            for x in range(4):
                px[x + i * 4, y] = 3
        frames.append(im)
    frames[0].save(path("gif_meta.gif"), save_all=True, append_images=frames[1:], loop=3, duration=[100, 200, 300],
                   disposal=[2, 1, 3], transparency=3, comment=b"EasyImageSharp GIF metadata fixture")
    with Image.open(path("gif_meta.gif")) as g:
        assert g.n_frames == 3 and g.info.get("loop") == 3
    # Pillow rebuilds the palette when it saves, so the encoded transparent index is 1 (not the source's 3)
    # and frames 2-3 carry a 4-entry local colour table.
    facts["gif_meta.gif"] = {"loop": 3, "delays_cs": [10, 20, 30], "disposal": [2, 1, 3], "transparency": 1,
                             "comment": "EasyImageSharp GIF metadata fixture", "global_table": 4,
                             "local_tables": [0, 4, 4]}

    # 11. Hostile/corrupt EXIF that must degrade to "no EXIF" without failing the decode.
    buf = io.BytesIO()
    photo.save(buf, "JPEG", quality=85)
    plain = buf.getvalue()
    write("corrupt_exif_garbage.jpg", insert_jpeg_segments(plain, [(0xE1, b"Exif\0\0" + b"XXXXGARBAGE!!")]))
    # Valid header, IFD claims 500 entries but only two are present, and one entry has an absurd count.
    trunc = b"II" + struct.pack("<HL", 42, 8) + struct.pack("<H", 500)
    trunc += struct.pack("<HHL", 0x0112, SHORT, 1) + struct.pack("<H", 6) + b"\0\0"
    trunc += struct.pack("<HHLL", 0x010F, ASCII, 0x7FFFFFFF, 26)
    write("corrupt_exif_truncated.jpg", insert_jpeg_segments(plain, [(0xE1, b"Exif\0\0" + trunc)]))
    facts["corrupt_exif_garbage.jpg"] = {"exif": None}
    facts["corrupt_exif_truncated.jpg"] = {"exif": {"Orientation": 6}, "note": "IFD claims 500 entries; only Orientation is intact"}

    with open(path("manifest.json"), "w", encoding="utf-8") as f:
        json.dump(facts, f, indent=1, sort_keys=True, ensure_ascii=True)
        f.write("\n")
    write_expected_md(out_dir)
    print(f"metadata: {len(facts)} fixtures")


def write_expected_md(out_dir: str) -> None:
    text = """# Metadata fixtures

Generated by `gen_metadata.py` (run `python generate.py` from `Fixtures/`). Deterministic; see `manifest.json`.

| File | What it carries |
| --- | --- |
| `orient_1..8.jpg` | 64x48 grayscale JPEG (8x6 flat 8x8 blocks, unique gray per block, quality 100 so it decodes bit-exactly) with EXIF Orientation 1..8 written by Pillow (big-endian). `orient_N.rgba` = Pillow `ImageOps.exif_transpose` result (RGBA); `card.rgba` = the unrotated card. |
| `exif_alltypes.jpg` | Hand-built little-endian EXIF in APP1: every TIFF field type (BYTE..DOUBLE), IFD0 + Exif + Interop + GPS + IFD1 with JPEG thumbnail, unknown private tags 0xC001-0xC00B, ASCII UserComment. `exif_alltypes_le.bin` is the raw payload. |
| `exif_alltypes_be.png` | Same profile big-endian in a PNG eXIf chunk; UserComment is UNICODE ("Héllo 日本"). `exif_alltypes_be.bin` is the raw payload. |
| `exif_pillow.{jpg,png,tif}` | Pillow-written EXIF (`exif_pillow.bin`, big-endian, Exif + GPS sub-IFDs); JPEG at 300 DPI (JFIF), PNG at 150x100 DPI (pHYs), TIFF at 200 DPI (IFD0 tags only: Pillow does not write sub-IFDs into TIFF). |
| `dpi_300.jpg`, `dpi_150x100.png`, `dpi_200.tif`, `dpi_96x120.bmp`, `dpi_none.png` | Resolution only (JFIF density / pHYs / XResolution / pixels-per-metre / nothing). |
| `icc.{jpg,png,tif}` | Identical minimal v2 RGB profile (`icc_profile.bin`, description "EasyImageSharp Test Profile"). |
| `xmp.{jpg,png,tif}` | Identical XMP packet (`xmp_packet.xml`); JPEG APP1, PNG `iTXt` chunk with the `XML:com.adobe.xmp` keyword (inserted by hand: Pillow ignores `xmp=` for PNG), TIFF tag 700. |
| `text.png` | tEXt Title/Author, zTXt Description (300 x "z"), iTXt Comment (lang "de", translated "Kommentar", non-Latin-1 text), hand-inserted gAMA 45455. |
| `comment_q75.jpg`, `q50_progressive.jpg` | COM segment + quality 75; progressive quality 50. |
| `multipage_meta.tif` | Big-endian, hand-built, 2 grayscale pages (12x8 @ 100 DPI "Page 1", 9x7 @ 250 DPI "Page 2"); `multipage_meta.rgba` = both pages. |
| `gif_meta.gif` | 3 frames 16x12, NETSCAPE loop 3, delays 10/20/30 cs, disposal 2/1/3, transparency index 1 (Pillow rebuilds the palette when saving), global table of 4 entries, local tables on frames 2-3, comment. |
| `corrupt_exif_garbage.jpg` | APP1 "Exif\\0\\0" followed by garbage: must decode with no EXIF profile. |
| `corrupt_exif_truncated.jpg` | APP1 whose IFD0 claims 500 entries with only Orientation=6 intact and a second entry with an absurd count: must decode with just Orientation. |
"""
    with open(os.path.join(out_dir, "EXPECTED.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


if __name__ == "__main__":
    gen_metadata(os.path.join(os.path.dirname(os.path.abspath(__file__)), "metadata"))
