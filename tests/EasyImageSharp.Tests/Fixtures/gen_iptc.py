#!/usr/bin/env python
"""IPTC-IIM fixtures (JPEG APP13 Photoshop image resources, TIFF tags 33723/34377) under Fixtures/iptc/.

Discovered and run by generate.py: gen_iptc(out_dir). Requires Python 3.11 + Pillow 11 + numpy + tifffile.

The pixel containers come from independent encoders (Pillow writes the JPEGs, tifffile the TIFFs, in both byte
orders and with the IPTC-NAA tag typed LONG, UNDEFINED and BYTE). The IPTC streams and Photoshop resource blocks are
assembled byte by byte below, straight from IPTC-IIM 4.2 section 1.5 (dataset tags, standard and extended lengths)
and Adobe's image resource block layout ("8BIM", id, padded Pascal name, size, even-padded data). Every well-formed
fixture is read back by an independent reader before it is written: Pillow's IptcImagePlugin and JPEG APP13 parser,
and tifffile's tag reader. Pillow cannot read standard extended datasets (it takes the high length byte as
0x80 + n), so the extended fixture is checked at the resource level by Pillow and at the dataset level by the
spec parser in this file. The deliberately corrupt fixtures are checked only for what an independent decoder can
confirm: that the image itself still decodes.

Deterministic: fixed patterns, fixed metadata, no timestamps. See iptc/manifest.json for the per-file facts.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import struct

import numpy as np
import tifffile
from PIL import Image, IptcImagePlugin

W, H = 24, 16
ESC_UTF8 = b"\x1b%G"

# Datasets whose content IPTC-IIM 4.2 defines as binary; every other dataset is compared as text as well.
BINARY = {(1, 0), (1, 20), (1, 22), (1, 90), (1, 120), (1, 122), (2, 0), (2, 125), (2, 200), (2, 201), (2, 202)}


def source_pixels() -> np.ndarray:
    rng = np.random.default_rng(20261104)
    y, x = np.mgrid[0:H, 0:W]
    arr = np.stack([(x * 10) % 256, (y * 15) % 256, (x * y * 3) % 256], axis=-1).astype(np.uint8)
    return (arr ^ rng.integers(0, 8, size=arr.shape, dtype=np.uint8)).astype(np.uint8)


# ---------------------------------------------------------------------------------------------------
# IPTC-IIM and Photoshop image resource writers, written from the specifications.
# ---------------------------------------------------------------------------------------------------

def dataset(record: int, number: int, data: bytes) -> bytes:
    """One dataset: 0x1C, record, dataset, then a 2-byte length or (above 32767) the extended form 0x8004 + 4 bytes."""
    if len(data) > 0x7FFF:
        return bytes([0x1C, record, number]) + struct.pack(">HI", 0x8004, len(data)) + data
    return bytes([0x1C, record, number]) + struct.pack(">H", len(data)) + data


def iim(entries: list[tuple[int, int, bytes]]) -> bytes:
    return b"".join(dataset(r, d, v) for r, d, v in entries)


def parse_iim(data: bytes) -> list[tuple[int, int, bytes]]:
    """Reads datasets until the first byte that is not a tag marker (zero padding ends the stream)."""
    out = []
    pos = 0
    while pos + 5 <= len(data) and data[pos] == 0x1C:
        record, number = data[pos + 1], data[pos + 2]
        length = int.from_bytes(data[pos + 3:pos + 5], "big")
        pos += 5
        if length & 0x8000:
            width = length & 0x7FFF
            length = int.from_bytes(data[pos:pos + width], "big")
            pos += width
        out.append((record, number, data[pos:pos + length]))
        pos += length
    return out


def resource(rid: int, data: bytes, name: bytes = b"", signature: bytes = b"8BIM") -> bytes:
    """One image resource: signature, id, Pascal name padded to even length, 4-byte size, data padded to even length."""
    name_field = bytes([len(name)]) + name
    if len(name_field) % 2:
        name_field += b"\0"
    body = data + (b"\0" if len(data) % 2 else b"")
    return signature + struct.pack(">H", rid) + name_field + struct.pack(">I", len(data)) + body


def resolution_info() -> bytes:
    """Resource 0x03ED: 72 dpi horizontally and vertically (16.16 fixed point), units inches."""
    return struct.pack(">IHHIHH", 72 << 16, 1, 1, 72 << 16, 1, 1)


def insert_app13(jpeg: bytes, segments: list[bytes]) -> bytes:
    """Inserts APP13 segments, each "Photoshop 3.0\\0" + a slice of the resource block, after the JFIF APP0."""
    assert jpeg[:2] == b"\xff\xd8" and jpeg[2:4] == b"\xff\xe0"
    app0_end = 4 + int.from_bytes(jpeg[4:6], "big")
    out = bytearray(jpeg[:app0_end])
    for seg in segments:
        payload = b"Photoshop 3.0\0" + seg
        assert len(payload) + 2 <= 0xFFFF
        out += b"\xff\xed" + struct.pack(">H", len(payload) + 2) + payload
    out += jpeg[app0_end:]
    return bytes(out)


# ---------------------------------------------------------------------------------------------------
# The IPTC content of the fixtures.
# ---------------------------------------------------------------------------------------------------

def utf8_entries() -> list[tuple[int, int, bytes]]:
    u = lambda s: s.encode("utf-8")  # noqa: E731
    return [
        (1, 0, b"\x00\x04"),
        (1, 90, ESC_UTF8),
        (2, 0, b"\x00\x04"),
        (2, 5, u("Lisboa à noite")),
        (2, 10, b"5"),
        (2, 15, b"TRV"),
        (2, 20, u("Transportes")),
        (2, 20, u("Cidades")),
        (2, 25, u("Lisboa")),
        (2, 25, u("noite")),
        (2, 25, u("elétrico")),
        (2, 40, u("Embargo até 2026-02-01")),
        (2, 55, b"20260115"),
        (2, 60, b"213000+0000"),
        (2, 80, u("Inês Gonçalves")),
        (2, 85, u("Fotógrafa")),
        (2, 90, u("Lisboa")),
        (2, 92, u("Alfama")),
        (2, 95, u("Lisboa")),
        (2, 100, b"PRT"),
        (2, 101, u("Portugal")),
        (2, 103, u("JOB-0042")),
        (2, 105, u("Elétrico 28 sobe a colina")),
        (2, 110, u("Agência Exemplo")),
        (2, 115, u("Arquivo Exemplo")),
        (2, 116, u("© 2026 Inês Gonçalves")),
        (2, 120, u("Um elétrico amarelo à noite — 夜の路面電車, трамвай.")),
        (2, 122, u("Ed Itor")),
    ]


def latin1_entries() -> list[tuple[int, int, bytes]]:
    l1 = lambda s: s.encode("latin-1")  # noqa: E731
    return [
        (2, 0, b"\x00\x04"),
        (2, 5, l1("Café in Zürich")),
        (2, 25, l1("café")),
        (2, 25, b"plain ascii"),
        (2, 80, l1("José Muñoz")),
        (2, 90, l1("São Paulo")),
        (2, 116, l1("© 2025 José Muñoz")),
    ]


def unknown_record_entries() -> list[tuple[int, int, bytes]]:
    return [
        (1, 0, b"\x00\x04"),
        (1, 5, b"DEST-A"),
        (1, 5, b"DEST-B"),
        (1, 20, b"\x00\x01"),
        (2, 0, b"\x00\x04"),
        (2, 5, b"Unknown records"),
        (2, 231, b"history entry kept verbatim"),  # Not a named dataset: must round-trip untouched.
        (3, 10, b"\x01\x02\x03\x04"),  # Pre-object descriptor record.
        (7, 10, b"\x00\x00\x00\x2a"),
        (9, 10, b"\x00\x00\x10\x00"),
    ]


def extended_entries() -> list[tuple[int, int, bytes]]:
    preview = bytes(((i * 7) + 3) & 0xFF for i in range(33000))
    return [
        (2, 0, b"\x00\x04"),
        (2, 5, b"Extended dataset"),
        (2, 200, b"\x00\x0b"),
        (2, 201, b"\x00\x01"),
        (2, 202, preview),
    ]


# ---------------------------------------------------------------------------------------------------
# Independent read-back.
# ---------------------------------------------------------------------------------------------------

def pillow_iptc_matches(path: str, entries: list[tuple[int, int, bytes]]) -> None:
    """Pillow's IptcImagePlugin must find exactly the datasets we wrote (it stops at 8:10, the object data)."""
    expected: dict[tuple[int, int], object] = {}
    for record, number, value in entries:
        if (record, number) == (8, 10):
            break
        key = (record, number)
        if key in expected:
            prior = expected[key]
            expected[key] = (prior if isinstance(prior, list) else [prior]) + [value]
        else:
            expected[key] = value
    with Image.open(path) as im:
        got = IptcImagePlugin.getiptcinfo(im)
    assert got == expected, f"{path}: Pillow read {got!r}, expected {expected!r}"


def pillow_resources(path: str) -> dict[int, bytes]:
    """The resources Pillow's APP13 parser finds. Pillow interprets resolution info (0x03ED) itself, so that one is
    checked field by field and reported back as the bytes this script wrote for it."""
    with Image.open(path) as im:
        im.load()
        found = dict(im.info.get("photoshop", {}))
    if isinstance(found.get(0x03ED), dict):
        info = found[0x03ED]
        assert info["XResolution"] == 72 and info["YResolution"] == 72, info
        assert info["DisplayedUnitsX"] == 1 and info["DisplayedUnitsY"] == 1, info
        found[0x03ED] = resolution_info()
    return found


def tifffile_tag_bytes(path: str, code: int) -> tuple[bytes | None, int | None]:
    with tifffile.TiffFile(path) as tf:
        tag = tf.pages[0].tags.get(code)
        if tag is None:
            return None, None
        value = tag.value
        if isinstance(value, (bytes, bytearray)):
            return bytes(value), tag.dtype
        # LONG-typed blocks come back as integers in the file's byte order.
        fmt = (">" if tf.byteorder == ">" else "<") + "I"
        return b"".join(struct.pack(fmt, v) for v in (value if isinstance(value, tuple) else (value,))), tag.dtype


# ---------------------------------------------------------------------------------------------------
# Manifest helpers.
# ---------------------------------------------------------------------------------------------------

def describe_datasets(entries: list[tuple[int, int, bytes]], encoding: str) -> list[dict]:
    """Expected datasets in order: the raw bytes (as hex, or as length + SHA-256 when large) and, for datasets the
    specification defines as text, the decoded text."""
    out = []
    for record, number, value in entries:
        item: dict = {"record": record, "dataset": number}
        if len(value) > 256:
            item["length"] = len(value)
            item["contentSha256"] = hashlib.sha256(value).hexdigest()
        else:
            item["hex"] = value.hex()
        if (record, number) not in BINARY:
            item["text"] = value.decode(encoding).rstrip("\0")
        out.append(item)
    return out


def describe_resources(block_resources: list[tuple[int, bytes, bytes]]) -> list[dict]:
    return [{"id": rid, "name": name.decode("latin-1"), "hex": data.hex()} for rid, data, name in block_resources]


def gen_iptc(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    pixels = source_pixels()
    fixtures: list[dict] = []

    def path(name: str) -> str:
        return os.path.join(out_dir, name)

    base = io.BytesIO()
    Image.fromarray(pixels).save(base, "JPEG", quality=90)
    base_jpeg = base.getvalue()

    def add_jpeg(name: str, segments: list[bytes], entry: dict) -> None:
        with open(path(name + ".jpg"), "wb") as f:
            f.write(insert_app13(base_jpeg, segments))
        with Image.open(path(name + ".jpg")) as im:
            im.load()
            assert im.size == (W, H), name
        fixtures.append({"name": name, "file": name + ".jpg", "container": "jpeg", "width": W, "height": H, **entry})

    # 1. UTF-8 IPTC between other resources, with a matching digest and a named resource after it.
    utf8 = iim(utf8_entries())
    utf8_resources = [
        (0x03ED, resolution_info(), b""),
        (0x0404, utf8, b""),
        (0x0425, hashlib.md5(utf8).digest(), b""),
        (0x0FA0, b"plugin", b"EIS plug-in"),
    ]
    utf8_block = b"".join(resource(rid, data, name) for rid, data, name in utf8_resources)
    kept = [r for r in utf8_resources if r[0] != 0x0404]
    utf8_iptc = {"utf8": True, "datasets": describe_datasets(utf8_entries(), "utf-8")}
    add_jpeg("jpeg_utf8", [utf8_block], {
        "iptc": utf8_iptc, "resources": describe_resources(kept), "irb": utf8_block.hex(),
        "notes": "UTF-8 (1:90 ESC % G) IPTC as resource 0x0404 between resolution info and a matching MD5 digest",
    })
    pillow_iptc_matches(path("jpeg_utf8.jpg"), utf8_entries())
    assert pillow_resources(path("jpeg_utf8.jpg")) == {rid: data for rid, data, _ in utf8_resources}

    # 2. The same block split mid-resource over two APP13 segments: readers must join consecutive segments.
    cut = len(utf8_block) // 2
    add_jpeg("jpeg_multisegment", [utf8_block[:cut], utf8_block[cut:]], {
        "iptc": utf8_iptc, "resources": describe_resources(kept), "irb": utf8_block.hex(), "twin": "jpeg_utf8",
        "notes": f"jpeg_utf8's resource block split at byte {cut} (inside resource 0x0404) across two APP13 segments",
    })

    # 3. No coded character set: Latin-1 text, which is not valid UTF-8, and no digest.
    latin1 = iim(latin1_entries())
    add_jpeg("jpeg_latin1", [resource(0x0404, latin1)], {
        "iptc": {"utf8": False, "datasets": describe_datasets(latin1_entries(), "latin-1")}, "resources": [],
        "notes": "No 1:90: ISO 8859-1 text (invalid as UTF-8) must decode as Latin-1",
    })
    pillow_iptc_matches(path("jpeg_latin1.jpg"), latin1_entries())

    # 4. A digest that does not describe the IPTC block (another application edited it): kept as read.
    stale = hashlib.md5(b"an older IPTC block").digest()
    add_jpeg("jpeg_stale_digest", [resource(0x0404, latin1) + resource(0x0425, stale)], {
        "iptc": {"utf8": False, "datasets": describe_datasets(latin1_entries(), "latin-1")},
        "resources": describe_resources([(0x0425, stale, b"")]),
        "notes": "Resource 0x0425 holds the MD5 of different data; a decoder must keep it unchanged",
    })
    pillow_iptc_matches(path("jpeg_stale_digest.jpg"), latin1_entries())

    # 5. Datasets in records the library does not name, including binary ones: lossless round trip.
    unknown = iim(unknown_record_entries())
    add_jpeg("jpeg_unknown_records", [resource(0x0404, unknown)], {
        "iptc": {"utf8": False, "datasets": describe_datasets(unknown_record_entries(), "latin-1")},
        "resources": [],
        "notes": "Records 1, 2, 3, 7 and 9 and the unnamed dataset 2:231; every dataset must survive a re-encode",
    })
    pillow_iptc_matches(path("jpeg_unknown_records.jpg"), unknown_record_entries())

    # 6. An extended dataset (33 000 bytes, above the 32 767 standard maximum).
    extended = iim(extended_entries())
    add_jpeg("jpeg_extended", [resource(0x0404, extended)], {
        "iptc": {"utf8": False, "datasets": describe_datasets(extended_entries(), "latin-1")}, "resources": [],
        "notes": "2:202 preview data of 33000 bytes in the extended length form (0x8004 + 4-byte length)",
    })
    assert pillow_resources(path("jpeg_extended.jpg")) == {0x0404: extended}
    assert parse_iim(extended) == extended_entries()

    # 7-11. Corrupt IPTC in an intact JPEG. The decode must succeed and keep what can be read.
    good = utf8_entries()[:4]
    truncated = iim(good) + bytes([0x1C, 2, 105]) + struct.pack(">H", 500) + b"only twenty bytes..."
    add_jpeg("jpeg_corrupt_truncated_dataset", [resource(0x0404, truncated)], {
        "iptc": {"utf8": True, "datasets": describe_datasets(good, "utf-8")}, "resources": [],
        "notes": "The fifth dataset declares 500 bytes but 20 remain: the four before it are kept",
    })
    oversized = resource(0x03ED, resolution_info()) + b"8BIM" + struct.pack(">HHI", 0x0404, 0, 100000) + utf8[:64]
    add_jpeg("jpeg_corrupt_resource_size", [oversized], {
        "iptc": None, "resources": describe_resources([(0x03ED, resolution_info(), b"")]),
        "notes": "Resource 0x0404 declares 100000 bytes in a short block: dropped, the resource before it kept",
    })
    not_iim = bytes((i * 37 + 11) & 0xFF for i in range(1, 80))
    add_jpeg("jpeg_corrupt_not_iim", [resource(0x0404, not_iim) + resource(0x03ED, resolution_info())], {
        "iptc": None, "resources": describe_resources([(0x03ED, resolution_info(), b"")]),
        "notes": "Resource 0x0404 does not start with a dataset tag marker: no IPTC, other resources kept",
    })
    bad_signature = resource(0x03ED, resolution_info()) + resource(0x0404, utf8, signature=b"XXXX")
    add_jpeg("jpeg_corrupt_bad_signature", [bad_signature], {
        "iptc": None, "resources": describe_resources([(0x03ED, resolution_info(), b"")]),
        "notes": "The second resource has an unknown signature: the block ends there",
    })
    bad_extended = iim(good) + bytes([0x1C, 2, 120, 0x80, 0x00]) + b"\x00\x00\x00\x10" + b"x" * 16
    add_jpeg("jpeg_corrupt_extended_length", [resource(0x0404, bad_extended)], {
        "iptc": {"utf8": True, "datasets": describe_datasets(good, "utf-8")}, "resources": [],
        "notes": "An extended dataset whose length-of-length is zero: the datasets before it are kept",
    })

    # 12-17. TIFF: tifffile writes the container, uncompressed RGB, so the pixels are exact.
    def add_tiff(name: str, byteorder: str, extratags: list, entry: dict, patch=None) -> None:
        file = path(name + ".tif")
        tifffile.imwrite(file, pixels, byteorder=byteorder, photometric="rgb", metadata=None, software=False,
                         extratags=extratags)
        if patch is not None:
            with open(file, "rb") as f:
                data = bytearray(f.read())
            patch(data, byteorder)
            with open(file, "wb") as f:
                f.write(bytes(data))
        with Image.open(file) as im:
            assert np.array_equal(np.asarray(im.convert("RGB")), pixels), name
            with open(path(name + ".rgba"), "wb") as f:
                f.write(im.convert("RGBA").tobytes())
        fixtures.append({
            "name": name, "file": name + ".tif", "container": "tiff", "width": W, "height": H, "rgba": name + ".rgba",
            "byteOrder": "MM" if byteorder == ">" else "II", **entry})

    def long_tag(code: int, data: bytes, byteorder: str) -> tuple:
        padded = data + b"\0" * (-len(data) % 4)
        ints = struct.unpack(byteorder + "%dI" % (len(padded) // 4), padded)
        return (code, 4, len(ints), ints, True)

    tiff_irb_resources = [(0x03ED, resolution_info(), b""), (0x0425, hashlib.md5(utf8).digest(), b"")]
    tiff_irb = b"".join(resource(rid, data, name) for rid, data, name in tiff_irb_resources)
    for name, order, typ in (("tiff_le_long", "<", 4), ("tiff_be_long", ">", 4),
                             ("tiff_le_undefined", "<", 7), ("tiff_be_byte", ">", 1)):
        iptc_tag = long_tag(33723, utf8, order) if typ == 4 else (33723, typ, len(utf8), utf8, True)
        tags = [iptc_tag]
        resources = []
        if typ == 4:
            tags.append((34377, 1, len(tiff_irb), tiff_irb, True))
            resources = describe_resources(tiff_irb_resources)
        add_tiff(name, order, tags, {
            "iptc": utf8_iptc, "resources": resources, "iptcType": {4: "LONG", 7: "UNDEFINED", 1: "BYTE"}[typ],
            "irb": tiff_irb.hex() if typ == 4 else None,
            "notes": f"IPTC-NAA (33723) typed {typ} in a {'big' if order == '>' else 'little'}-endian file"
                     + ("; Photoshop block (34377) with resolution info and the IPTC digest" if typ == 4 else ""),
        })
        raw, dtype = tifffile_tag_bytes(path(name + ".tif"), 33723)
        assert dtype == typ and raw is not None and raw.rstrip(b"\0") == utf8.rstrip(b"\0"), name
        pillow_iptc_matches(path(name + ".tif"), utf8_entries())

    # IPTC only inside the Photoshop block: the decoder falls back to resource 0x0404 of tag 34377.
    ps_only = resource(0x03ED, resolution_info()) + resource(0x0404, latin1)
    add_tiff("tiff_photoshop_only", "<", [(34377, 7, len(ps_only), ps_only, True)], {
        "iptc": {"utf8": False, "datasets": describe_datasets(latin1_entries(), "latin-1")},
        "resources": describe_resources([(0x03ED, resolution_info(), b"")]), "iptcType": None,
        "notes": "No tag 33723; the IPTC block is resource 0x0404 of the Photoshop tag 34377",
    })
    raw, _ = tifffile_tag_bytes(path("tiff_photoshop_only.tif"), 34377)
    assert raw == ps_only

    def patch_count(data: bytearray, byteorder: str) -> None:
        """Rewrites the element count of the 33723 entry in the first directory to 0x7FFFFFF0."""
        ifd = struct.unpack_from(byteorder + "I", data, 4)[0]
        count = struct.unpack_from(byteorder + "H", data, ifd)[0]
        for i in range(count):
            entry = ifd + 2 + (12 * i)
            if struct.unpack_from(byteorder + "H", data, entry)[0] == 33723:
                struct.pack_into(byteorder + "I", data, entry + 4, 0x7FFFFFF0)
                return
        raise AssertionError("no 33723 entry")

    add_tiff("tiff_corrupt_count", "<", [(33723, 7, len(utf8), utf8, True)], {
        "iptc": None, "resources": [], "iptcType": "UNDEFINED",
        "notes": "The 33723 entry's element count is patched to 0x7FFFFFF0, far past the file: the entry is skipped",
    }, patch=patch_count)
    add_tiff("tiff_corrupt_iim", "<", [long_tag(33723, truncated, "<")], {
        "iptc": {"utf8": True, "datasets": describe_datasets(good, "utf-8")}, "resources": [], "iptcType": "LONG",
        "notes": "The IPTC stream's fifth dataset runs past the end: the four before it are kept",
    })

    with open(path("manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"fixtures": fixtures}, f, indent=1, sort_keys=True, ensure_ascii=True)
        f.write("\n")

    with open(path("EXPECTED.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(EXPECTED_MD)


EXPECTED_MD = """# IPTC fixtures

Generated by `gen_iptc.py` (run `python generate.py` from `Fixtures/`). Deterministic; `manifest.json` lists
every file with the datasets (`record`, `dataset`, raw bytes and, for text datasets, the decoded text) and the
non-IPTC Photoshop resources (`resources`) a decoder must report. `iptc: null` means no IPTC profile.

Provenance: no third-party data. The JPEG and TIFF containers are written by Pillow and tifffile from a seeded
NumPy pattern; the IPTC streams and Photoshop resource blocks are assembled by `gen_iptc.py` from IPTC-IIM 4.2
section 1.5 and Adobe's image resource layout, and every well-formed one is read back by Pillow's
`IptcImagePlugin` (JPEG APP13 and TIFF 33723) or tifffile before it is written. Pillow reads standard extended
datasets wrongly, so `jpeg_extended` is checked by Pillow at the resource level and by the spec parser in
`gen_iptc.py` at the dataset level.

| Group | Files | What it covers |
| --- | --- | --- |
| JPEG | `jpeg_utf8`, `jpeg_multisegment` | UTF-8 IPTC (1:90 `ESC % G`) as resource 0x0404 between other resources and a matching 0x0425 digest; the same block split over two APP13 segments |
| JPEG | `jpeg_latin1`, `jpeg_stale_digest` | no coded character set (Latin-1 text); a digest that does not match |
| JPEG | `jpeg_unknown_records`, `jpeg_extended` | records 1/3/7/9 and an unnamed dataset; a 33 000-byte extended dataset |
| JPEG | `jpeg_corrupt_*` | truncated dataset, oversized resource, non-IIM payload, bad signature, zero length-of-length |
| TIFF | `tiff_le_long`, `tiff_be_long`, `tiff_le_undefined`, `tiff_be_byte` | tag 33723 typed LONG, UNDEFINED and BYTE in both byte orders; tag 34377 beside it |
| TIFF | `tiff_photoshop_only` | IPTC only as resource 0x0404 of tag 34377 |
| TIFF | `tiff_corrupt_count`, `tiff_corrupt_iim` | an element count far past the file; a truncated IPTC stream |
"""


if __name__ == "__main__":
    gen_iptc(os.path.join(os.path.dirname(os.path.abspath(__file__)), "iptc"))
