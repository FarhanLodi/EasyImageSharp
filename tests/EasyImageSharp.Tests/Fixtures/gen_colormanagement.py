#!/usr/bin/env python
"""Colour-management fixtures (ICC matrix/TRC profiles and LittleCMS reference conversions) under Fixtures/colormanagement/.

Discovered and run by generate.py: gen_colormanagement(out_dir). Requires Python 3.11 + Pillow 11 + numpy.

PROVENANCE. No third-party profile or image is used. Every ICC profile here is assembled byte by byte by the
writer in this file from the published colorimetry of each colour space, so the corpus carries no licence
beyond this repository's own:

  Display P3     DCI-P3 primaries (SMPTE EG 432-1) with the D65 white and the sRGB transfer function
                 (IEC 61966-2-1), as Apple's Display P3 defines them. ICC v4.3, parametricCurveType 3.
  Adobe RGB      Adobe RGB (1998) Color Image Encoding, version 2005-05: primaries, D65 white and the
                 gamma 563/256 = 2.19921875. ICC v2.1, curveType with one u8Fixed8 gamma entry.
  ProPhoto RGB   ROMM RGB (ISO 22028-2): primaries, D50 white, gamma 1.8 with the linear segment below
                 Et = 1/512 (slope 16). ICC v4.3, parametricCurveType 3.
  sRGB (table)   IEC 61966-2-1 primaries and transfer function as a 1024-entry curveType table, the shape of
                 the widely embedded ICC v2 "sRGB IEC61966-2.1" profiles. ICC v2.1.
  sRGB (para)    The same colour space with parametricCurveType 3. ICC v4.3.
  sRGB + LUT     sRGB (table) with an extra A2B0 tag, the shape of profiles that carry LUT transforms next to
                 their matrix/TRC tags. The A2B0 payload is a minimal placeholder; nothing here parses it.
  P3 + LUT       Display P3 with the same extra A2B0 tag: a LUT profile the managed engine must refuse.
  Gray gamma 2.2 A GRAY display profile with a kTRC, which the managed engine must refuse.

Colorants are the D50-adapted XYZ of each primary: RGB->XYZ from the xy chromaticities and the white point,
then Bradford adaptation to the ICC PCS white D50 = (0.9642, 1.0, 0.8249), rounded to s15Fixed16 as the ICC
specification requires. The v4 profiles carry the matching 'chad' tag; the header date is fixed.

REFERENCES. src.png is a 64x48 8-bit RGB test card (channel and grey ramps, primaries and secondaries, and
fixed-seed random colours). <space>_to_srgb.png is LittleCMS's conversion of that card from the profile to its
built-in sRGB profile, through Pillow's ImageCms with relative colorimetric intent and cmsFLAGS_NOOPTIMIZE, so the
transform is evaluated stage by stage rather than through 8-bit precalculated tables. These are the independent
ground truth for the library's managed matrix/TRC engine; the tests compare them by CIE76 delta E.

<space>_tagged.png / .tif embed the profile in the card itself so the decode-time conversion can be tested end to
end; multipage_tagged.tif carries a different profile on each page (P3, Adobe RGB, sRGB table).
"""
from __future__ import annotations

import io
import json
import os
import struct

import numpy as np
from PIL import Image, ImageCms

W, H = 64, 48

# ICC PCS illuminant (ICC.1:2010 7.2.16), the destination of every chromatic adaptation below.
D50_PCS = np.array([0.9642, 1.0, 0.8249])

BRADFORD = np.array([
    [0.8951, 0.2664, -0.1614],
    [-0.7502, 1.7135, 0.0367],
    [0.0389, -0.0685, 1.0296],
])

# (name, primaries xy (r, g, b), white xy)
SPACES = {
    "srgb": (((0.640, 0.330), (0.300, 0.600), (0.150, 0.060)), (0.3127, 0.3290)),
    "display_p3": (((0.680, 0.320), (0.265, 0.690), (0.150, 0.060)), (0.3127, 0.3290)),
    "adobe_rgb": (((0.640, 0.330), (0.210, 0.710), (0.150, 0.060)), (0.3127, 0.3290)),
    "prophoto": (((0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.0001)), (0.3457, 0.3585)),
}

# parametricCurveType 3 (ICC.1:2010 10.18): Y = (aX + b)^g for X >= d, else Y = cX.
SRGB_PARA = (2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)
ROMM_PARA = (1.8, 1.0, 0.0, 1 / 16, 1 / 32)
ADOBE_GAMMA_U8F8 = 563  # 563 / 256 = 2.19921875, the Adobe RGB (1998) gamma


def xy_to_xyz(x: float, y: float) -> np.ndarray:
    return np.array([x / y, 1.0, (1 - x - y) / y])


def adaptation(source_white: np.ndarray, target_white: np.ndarray) -> np.ndarray:
    """Bradford chromatic adaptation matrix (column-vector convention)."""
    s = BRADFORD @ source_white
    t = BRADFORD @ target_white
    return np.linalg.inv(BRADFORD) @ np.diag(t / s) @ BRADFORD


def colorants(space: str) -> tuple[np.ndarray, np.ndarray]:
    """Returns (3x3 D50 colorant matrix with the r, g, b XYZ as columns, the chad matrix)."""
    primaries, white_xy = SPACES[space]
    p = np.column_stack([xy_to_xyz(*xy) for xy in primaries])
    white = xy_to_xyz(*white_xy)
    m = p @ np.diag(np.linalg.solve(p, white))
    chad = adaptation(white, D50_PCS)
    return chad @ m, chad


# ---------------------------------------------------------------------------------------------------
# A minimal ICC writer: header, tag table, and the handful of tag types a matrix/TRC profile needs.
# ---------------------------------------------------------------------------------------------------

def s15f16(value: float) -> bytes:
    return struct.pack(">i", int(round(value * 65536)))


def xyz_tag(xyz) -> bytes:
    return b"XYZ " + bytes(4) + b"".join(s15f16(v) for v in xyz)


def sf32_tag(matrix: np.ndarray) -> bytes:
    return b"sf32" + bytes(4) + b"".join(s15f16(v) for v in matrix.reshape(-1))


def para3_tag(g: float, a: float, b: float, c: float, d: float) -> bytes:
    return b"para" + bytes(4) + struct.pack(">HH", 3, 0) + b"".join(s15f16(v) for v in (g, a, b, c, d))


def curv_gamma_tag(u8f8: int) -> bytes:
    return b"curv" + bytes(4) + struct.pack(">IH", 1, u8f8)


def curv_table_tag(values: np.ndarray) -> bytes:
    entries = np.round(np.clip(values, 0, 1) * 65535).astype(">u2")
    return b"curv" + bytes(4) + struct.pack(">I", len(entries)) + entries.tobytes()


def mluc_tag(text: str) -> bytes:
    encoded = text.encode("utf-16-be")
    return b"mluc" + bytes(4) + struct.pack(">II", 1, 12) + b"enUS" + struct.pack(">II", len(encoded), 28) + encoded


def desc_v2_tag(text: str) -> bytes:
    ascii_text = text.encode("ascii") + b"\0"
    return (b"desc" + bytes(4) + struct.pack(">I", len(ascii_text)) + ascii_text
            + struct.pack(">II", 0, 0) + struct.pack(">HB", 0, 0) + bytes(67))


def text_v2_tag(text: str) -> bytes:
    return b"text" + bytes(4) + text.encode("ascii") + b"\0"


def build_profile(version: int, color_space: bytes, tags: list[tuple[bytes, bytes]]) -> bytes:
    """Assembles a profile. Tags with identical payloads share one copy, as ICC permits."""
    table_end = 128 + 4 + 12 * len(tags)
    payload = bytearray()
    placed: dict[bytes, int] = {}
    entries = []
    for signature, data in tags:
        if data not in placed:
            while (table_end + len(payload)) % 4:
                payload.append(0)
            placed[data] = table_end + len(payload)
            payload += data
        entries.append((signature, placed[data], len(data)))
    while (table_end + len(payload)) % 4:
        payload.append(0)
    size = table_end + len(payload)
    version_bytes = bytes([4, 0x30, 0, 0]) if version == 4 else bytes([2, 0x10, 0, 0])
    header = (struct.pack(">I", size) + bytes(4) + version_bytes + b"mntr" + color_space + b"XYZ "
              + struct.pack(">6H", 2026, 1, 1, 0, 0, 0) + b"acsp" + bytes(4) + bytes(4) + bytes(4) + bytes(4)
              + bytes(8) + struct.pack(">I", 0) + b"".join(s15f16(v) for v in D50_PCS) + bytes(4) + bytes(16)
              + bytes(28))
    assert len(header) == 128
    table = struct.pack(">I", len(tags)) + b"".join(struct.pack(">4sII", s, o, n) for s, o, n in entries)
    return header + table + bytes(payload)


def rgb_profile(space: str, version: int, description: str, trc: bytes, extra: list[tuple[bytes, bytes]] = ()) -> bytes:
    matrix, chad = colorants(space)
    _, white_xy = SPACES[space]
    tags: list[tuple[bytes, bytes]] = []
    if version == 4:
        tags += [(b"desc", mluc_tag(description)), (b"cprt", mluc_tag("No copyright, use freely")),
                 (b"wtpt", xyz_tag(D50_PCS)), (b"chad", sf32_tag(chad))]
    else:
        # ICC v2 records the media white itself; relative colorimetric conversion ignores it.
        tags += [(b"desc", desc_v2_tag(description)), (b"cprt", text_v2_tag("No copyright, use freely")),
                 (b"wtpt", xyz_tag(chad @ xy_to_xyz(*white_xy)))]
    tags += [(b"rXYZ", xyz_tag(matrix[:, 0])), (b"gXYZ", xyz_tag(matrix[:, 1])), (b"bXYZ", xyz_tag(matrix[:, 2])),
             (b"rTRC", trc), (b"gTRC", trc), (b"bTRC", trc)]
    tags += list(extra)
    return build_profile(version, b"RGB ", tags)


def srgb_decode(v: np.ndarray) -> np.ndarray:
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)


# A structurally plausible lut8Type header with no meaningful content: nothing in the library reads A2B0 bodies.
PLACEHOLDER_A2B0 = b"mft1" + bytes(4) + bytes([3, 3, 2, 0]) + bytes(36)


def profiles() -> dict[str, bytes]:
    srgb_table = curv_table_tag(srgb_decode(np.arange(1024) / 1023.0))
    return {
        "display_p3_v4.icc": rgb_profile("display_p3", 4, "Display P3 (EasyImageSharp fixture)", para3_tag(*SRGB_PARA)),
        "adobe_rgb_v2.icc": rgb_profile("adobe_rgb", 2, "Adobe RGB (1998) compatible (EasyImageSharp fixture)",
                                        curv_gamma_tag(ADOBE_GAMMA_U8F8)),
        "prophoto_v4.icc": rgb_profile("prophoto", 4, "ROMM RGB (EasyImageSharp fixture)", para3_tag(*ROMM_PARA)),
        "srgb_v2_table.icc": rgb_profile("srgb", 2, "sRGB IEC61966-2.1 table (EasyImageSharp fixture)", srgb_table),
        "srgb_v4_para.icc": rgb_profile("srgb", 4, "sRGB parametric (EasyImageSharp fixture)", para3_tag(*SRGB_PARA)),
        "srgb_v2_with_lut.icc": rgb_profile("srgb", 2, "sRGB with A2B0 (EasyImageSharp fixture)", srgb_table,
                                            [(b"A2B0", PLACEHOLDER_A2B0)]),
        "display_p3_with_lut.icc": rgb_profile("display_p3", 4, "Display P3 with A2B0 (EasyImageSharp fixture)",
                                               para3_tag(*SRGB_PARA), [(b"A2B0", PLACEHOLDER_A2B0)]),
        "gray_gamma22_v4.icc": build_profile(4, b"GRAY", [
            (b"desc", mluc_tag("Gray gamma 2.2 (EasyImageSharp fixture)")),
            (b"cprt", mluc_tag("No copyright, use freely")),
            (b"wtpt", xyz_tag(D50_PCS)),
            (b"kTRC", para3_tag(2.2, 1.0, 0.0, 0.0, 0.0)),
        ]),
    }


# ---------------------------------------------------------------------------------------------------
# Test card and references
# ---------------------------------------------------------------------------------------------------

def make_card() -> np.ndarray:
    card = np.zeros((H, W, 3), dtype=np.uint8)
    ramp = (np.arange(W) * 255 // (W - 1)).astype(np.uint8)
    card[0, :, 0] = ramp
    card[1, :, 1] = ramp
    card[2, :, 2] = ramp
    card[3, :, :] = ramp[:, None]
    corners = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 255, 255), (255, 0, 255), (255, 255, 0),
               (255, 255, 255), (0, 0, 0)]
    for x in range(W):
        card[4, x] = corners[x // 8]
    # Every 8-bit level of every channel appears at least once in rows 5-16 (64 * 12 >= 3 * 256).
    levels = np.arange(W * 4) % 256
    for i in range(4):
        card[5 + i, :, 0] = levels[i * W:(i + 1) * W]
        card[9 + i, :, 1] = levels[i * W:(i + 1) * W]
        card[13 + i, :, 2] = levels[i * W:(i + 1) * W]
    rng = np.random.RandomState(20261104)  # legacy generator: its stream is frozen across NumPy versions
    card[17:, :, :] = rng.randint(0, 256, size=(H - 17, W, 3)).astype(np.uint8)
    return card


def lcms_to_srgb(card: Image.Image, profile: bytes) -> Image.Image:
    source = ImageCms.ImageCmsProfile(io.BytesIO(profile))
    target = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB"))
    transform = ImageCms.buildTransform(source, target, "RGB", "RGB",
                                        renderingIntent=ImageCms.Intent.RELATIVE_COLORIMETRIC,
                                        flags=ImageCms.Flags.NOOPTIMIZE)
    result = ImageCms.applyTransform(card, transform)
    # ImageCms tags the result with LittleCMS's sRGB profile, whose header carries the creation time. The
    # references are untagged sRGB, which also keeps the files reproducible.
    result.info.pop("icc_profile", None)
    return result


def gen_colormanagement(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    built = profiles()
    for name, data in built.items():
        with open(os.path.join(out_dir, name), "wb") as f:
            f.write(data)

    card = Image.fromarray(make_card())
    card.save(os.path.join(out_dir, "src.png"))

    references = {
        "display_p3": "display_p3_v4.icc",
        "adobe_rgb": "adobe_rgb_v2.icc",
        "prophoto": "prophoto_v4.icc",
        "srgb_table": "srgb_v2_table.icc",
    }
    manifest: dict[str, object] = {"profiles": {}, "references": {}, "tagged": {}}
    for name, data in built.items():
        manifest["profiles"][name] = {"bytes": len(data)}
    for space, profile in references.items():
        reference = lcms_to_srgb(card, built[profile])
        reference.save(os.path.join(out_dir, f"{space}_to_srgb.png"))
        manifest["references"][f"{space}_to_srgb.png"] = {
            "source": "src.png", "profile": profile, "intent": "relative_colorimetric",
            "engine": "LittleCMS via Pillow ImageCms, cmsFLAGS_NOOPTIMIZE", "target": "LittleCMS built-in sRGB",
        }

    for space, profile in (("display_p3", "display_p3_v4.icc"), ("adobe_rgb", "adobe_rgb_v2.icc"),
                           ("prophoto", "prophoto_v4.icc"), ("gray", "gray_gamma22_v4.icc"),
                           ("p3_lut", "display_p3_with_lut.icc"), ("srgb_lut", "srgb_v2_with_lut.icc")):
        name = f"{space}_tagged.png"
        card.save(os.path.join(out_dir, name), icc_profile=built[profile])
        manifest["tagged"][name] = {"pixels": "src.png", "profile": profile}

    tiff = f"display_p3_tagged.tif"
    card.save(os.path.join(out_dir, tiff), icc_profile=built["display_p3_v4.icc"])
    manifest["tagged"][tiff] = {"pixels": "src.png", "profile": "display_p3_v4.icc"}

    # One profile per page. Without an icc_profile argument Pillow takes each page's own info["icc_profile"].
    pages = []
    for profile in ("display_p3_v4.icc", "adobe_rgb_v2.icc", "srgb_v2_table.icc"):
        page = card.copy()
        page.info["icc_profile"] = built[profile]
        pages.append(page)
    pages[0].save(os.path.join(out_dir, "multipage_tagged.tif"), save_all=True, append_images=pages[1:])
    manifest["tagged"]["multipage_tagged.tif"] = {
        "pixels": "src.png on every page",
        "profiles": ["display_p3_v4.icc", "adobe_rgb_v2.icc", "srgb_v2_table.icc"],
    }

    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")
    print(f"colormanagement: {len(built)} profiles, {len(references)} references")


if __name__ == "__main__":
    gen_colormanagement(os.path.join(os.path.dirname(os.path.abspath(__file__)), "colormanagement"))
