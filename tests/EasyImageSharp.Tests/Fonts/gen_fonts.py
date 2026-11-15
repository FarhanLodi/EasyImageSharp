"""Builds the test fonts and their independent reference data for the managed font engine tests.

Inputs are the unmodified DejaVu Sans 2.37 release fonts (see PROVENANCE.txt). Everything written here is
derived from them with fontTools (subsetting, CFF conversion, collection and WOFF packaging), and the
reference data comes from fontTools (metrics, cmap, kerning) and FreeType through freetype-py (unhinted glyph
coverage). Nothing is produced by EasyImageSharp itself.

    python gen_fonts.py <dir with DejaVuSans.ttf and DejaVuSans-Bold.ttf> [output dir]

Requires fontTools 4.60, brotli and freetype-py (FreeType 2.13) - install them in a virtual environment.
"""

import base64
import hashlib
import json
import os
import sys

import freetype
from fontTools import subset
from fontTools.cffLib import FDArrayIndex, FDSelect, FontDict, PrivateDict, SubrsIndex
from fontTools.fontBuilder import FontBuilder
from fontTools.misc.psCharStrings import T2CharString
from fontTools.pens.boundsPen import ControlBoundsPen
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._c_m_a_p import CmapSubtable
from fontTools.ttLib.ttCollection import TTCollection
from fontTools.ttLib import woff2

# The repertoire kept in the subsets: ASCII, Latin-1, a few typographic marks, and Greek/Cyrillic samples.
UNICODES = (
    list(range(0x20, 0x7F))
    + list(range(0xA0, 0x100))
    + [0x0131, 0x0152, 0x0153, 0x0160, 0x0161, 0x0178, 0x017D, 0x017E, 0x0192]
    + [0x2010, 0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026, 0x20AC, 0x2122]
    + [0x0391, 0x0392, 0x03A9, 0x03B1, 0x03B2, 0x03C9, 0x0410, 0x0416, 0x0430, 0x0436]
)

# Characters only the bold subset has, so tests can exercise fallback between faces.
BOLD_ONLY = [0x2665, 0x263A]

# A code point outside the BMP mapped through a format 12 subtable, to exercise that path.
SUPPLEMENTARY = 0x1D400  # MATHEMATICAL BOLD CAPITAL A, mapped to the glyph of 'A'

SAMPLE_TEXT = "AaBgQ&@%8e"
RENDER_SIZES = [12, 24, 48]
KERN_PAIRS = ["AV", "AT", "To", "Ty", "LT", "Yo", "VA", "Wa", "P.", "F,", "av", "rn", "AA"]


def sha256(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def make_subset(source, target, extra=()):
    options = subset.Options()
    options.layout_features = ["kern"]
    options.legacy_kern = True
    options.hinting = False
    options.notdef_outline = True
    options.name_IDs = [0, 1, 2, 3, 4, 5, 6, 13, 14, 16, 17]
    options.name_languages = ["*"]
    options.drop_tables += ["FFTM", "MATH", "GSUB", "gasp"]
    font = subset.load_font(source, options)
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=UNICODES + list(extra))
    subsetter.subset(font)

    # Add a Windows full-repertoire format 12 subtable alongside format 4, including one supplementary-plane
    # code point, so both lookup paths are exercised.
    best = dict(font.getBestCmap())
    best[SUPPLEMENTARY] = best[ord("A")]
    table = CmapSubtable.newSubtable(12)
    table.platformID, table.platEncID, table.language = 3, 10, 0
    table.cmap = best
    font["cmap"].tables = [t for t in font["cmap"].tables if t.format != 12] + [table]
    font.save(target)
    return target


def glyph_charstring(glyph_set, name, advance, nominal_width):
    # roundTolerance=0 keeps the exact 2/3 control points of converted quadratics as 16.16 fixed-point operands.
    pen = T2CharStringPen(advance - nominal_width, glyph_set, roundTolerance=0)
    glyph_set[name].draw(pen)
    return pen.getCharString()


def make_cff(ttf_path, target, cid):
    """Converts the TrueType subset to CFF outlines (quadratic segments become exact cubics)."""
    src = TTFont(ttf_path, recalcTimestamp=False)
    glyph_set = src.getGlyphSet()
    order = src.getGlyphOrder()
    names = order if not cid else [".notdef"] + ["cid%05d" % i for i in range(1, len(order))]
    rename = dict(zip(order, names))
    nominal = 0
    advances = {rename[g]: src["hmtx"][g] for g in order}

    charstrings = {}
    for g in order:
        cs = glyph_charstring(glyph_set, g, src["hmtx"][g][0], nominal)
        charstrings[rename[g]] = cs

    fb = FontBuilder(src["head"].unitsPerEm, isTTF=False)
    fb.setupGlyphOrder(names)
    fb.setupCharacterMap({cp: rename[g] for cp, g in src.getBestCmap().items()})
    family = "DejaVu Sans CFF Test" if not cid else "DejaVu Sans CID Test"
    ps = family.replace(" ", "")
    fb.setupCFF(ps, {"FullName": family, "FamilyName": family, "Weight": "Book"}, charstrings, {"nominalWidthX": nominal})
    fb.setupHorizontalMetrics(advances)
    hhea = src["hhea"]
    fb.setupHorizontalHeader(ascent=hhea.ascent, descent=hhea.descent, lineGap=hhea.lineGap)
    fb.setupNameTable({"familyName": family, "styleName": "Book", "psName": ps, "fullName": family})
    os2 = src["OS/2"]
    fb.setupOS2(
        sTypoAscender=os2.sTypoAscender, sTypoDescender=os2.sTypoDescender, sTypoLineGap=os2.sTypoLineGap,
        usWinAscent=os2.usWinAscent, usWinDescent=os2.usWinDescent, usWeightClass=400, fsSelection=0x40)
    fb.setupPost()
    if not cid and "GPOS" in src:
        fb.font["GPOS"] = src["GPOS"]
    font = fb.font

    # Keep the source's timestamps so regenerating reproduces the same bytes.
    font["head"].created = src["head"].created
    font["head"].modified = src["head"].modified

    cff = font["CFF "].cff
    top = cff.topDictIndex[0]
    charstrings_index = top.CharStrings
    gsubrs = cff.GlobalSubrs

    # Hand-made subroutines. In every third glyph a middle chunk of operators moves into a local subroutine
    # ending in 'return'; in the next glyph the tail (ending in 'endchar') moves into a global subroutine; one
    # extra local subroutine calls a global one (a nested call); and every fifth glyph starts with stem hints
    # and a hintmask. This exercises callsubr/callgsubr with their biases, return, endchar inside a
    # subroutine, nesting and hint skipping.
    private = top.Private
    local = []
    plan = {}
    for i, name in enumerate(names):
        cs = charstrings_index[name]
        cs.decompile()
        program = list(cs.program)
        ops = [k for k, token in enumerate(program) if isinstance(token, str)]
        if len(ops) >= 4 and i % 3 == 0:
            start, end = ops[0] + 1, ops[len(ops) // 2] + 1
            local.append(program[start:end] + ["return"])
            plan[name] = ("local", program[:start], len(local) - 1, program[end:])
        elif len(ops) >= 3 and i % 3 == 1:
            cut = ops[len(ops) // 2] + 1
            gsubrs.append(T2CharString(program=program[cut:], private=private, globalSubrs=gsubrs))
            plan[name] = ("global", program[:cut], len(gsubrs) - 1, [])
        else:
            plan[name] = ("none", program, 0, [])

    # The nested call: a local subroutine that only calls the first global subroutine (which ends the glyph).
    nested_caller = None
    if gsubrs:
        local.append([None, "callgsubr"])
        nested_caller = len(local) - 1
        for name, (kind, head, index, rest) in plan.items():
            if kind == "global" and index == 0:
                plan[name] = ("local", head, nested_caller, [])
                break

    lbias, gbias = bias(len(local)), bias(len(gsubrs))
    if nested_caller is not None:
        local[nested_caller][0] = 0 - gbias
    for name, (kind, head, index, rest) in plan.items():
        if kind == "local":
            program = head + [index - lbias, "callsubr"] + rest
        elif kind == "global":
            program = head + [index - gbias, "callgsubr"]
        else:
            program = head
        first_op = next((k for k, token in enumerate(program) if isinstance(token, str)), None)
        has_width = first_op is not None and (
            (program[first_op] == "rmoveto" and first_op == 3)
            or (program[first_op] in ("hmoveto", "vmoveto") and first_op == 2)
            or (program[first_op] == "endchar" and first_op == 1))
        if names.index(name) % 5 == 0 and has_width:
            program = program[:1] + [10, 20, "hstem", 30, 40, "vstem", "hintmask", bytes([0xC0])] + program[1:]
        cs = charstrings_index[name]
        cs.program = program
        cs.bytecode = None

    subrs = SubrsIndex()
    for body in local:
        subrs.append(T2CharString(program=body, private=private, globalSubrs=gsubrs))
    private.Subrs = subrs
    local = subrs

    if cid:
        # Rebuild as a CID-keyed font: two Font DICTs (with their own Private DICTs and local subroutines),
        # glyphs assigned alternately through a format 3 FDSelect.
        top.ROS = ("Adobe", "Identity", 0)
        top.CIDCount = len(names)
        fd_array = FDArrayIndex()
        for k in range(2):
            fd = FontDict()
            fd.setCFF2(False)
            # The top DICT's matrix already maps font units to the em; the Font DICT's must not scale again.
            fd.FontMatrix = [1, 0, 0, 1, 0, 0]
            fd.FontName = "%s-FD%d" % (ps, k)
            priv = PrivateDict()
            priv.nominalWidthX = nominal
            priv.defaultWidthX = 0
            fd.Private = priv
            fd_array.append(fd)
        # FD 0 keeps the local subroutines; FD 1 has none, so glyphs that call them must use FD 0.
        fd_array[0].Private.Subrs = local
        select = []
        for name in names:
            calls_local = "callsubr" in charstrings_index[name].program
            select.append(0 if calls_local or names.index(name) % 2 == 0 else 1)
        top.FDArray = fd_array
        top.FDSelect = FDSelect()
        top.FDSelect.format = 3
        top.FDSelect.gidArray = select
        for name, fd_index in zip(names, select):
            cs = charstrings_index[name]
            cs.private = fd_array[fd_index].Private
        for subr in local:
            subr.private = fd_array[0].Private
        del top.Private
        top.rawDict.pop("Private", None)
        if hasattr(top, "Encoding"):
            del top.Encoding
    font.recalcTimestamp = False
    font.save(target)
    TTFont(target)["CFF "].cff  # parse back once to catch encoding mistakes
    return target


def bias(count):
    return 107 if count < 1240 else 1131 if count < 33900 else 32768


def reference_for(path, face_index=0):
    font = TTFont(path, fontNumber=face_index)
    cmap = font.getBestCmap()
    order = font.getGlyphOrder()
    glyph_set = font.getGlyphSet()
    hhea = font["hhea"]
    name = font["name"]
    samples = sorted(set([ord(c) for c in SAMPLE_TEXT + "Hello, World! " + "".join(KERN_PAIRS)] + [0xE9, 0x20AC, 0x0416, SUPPLEMENTARY]))
    entries = []
    for cp in samples:
        gname = cmap.get(cp)
        gid = order.index(gname) if gname else 0
        pen = ControlBoundsPen(glyph_set)
        glyph_set[order[gid]].draw(pen)
        bounds = pen.bounds
        entries.append({
            "codepoint": cp,
            "glyph": gid,
            "advance": font["hmtx"][order[gid]][0],
            "bounds": list(bounds) if bounds else None,
        })
    return {
        "unitsPerEm": font["head"].unitsPerEm,
        "ascender": hhea.ascent,
        "descender": hhea.descent,
        "lineGap": hhea.lineGap,
        "numGlyphs": font["maxp"].numGlyphs,
        "familyName": name.getBestFamilyName(),
        "subfamilyName": name.getBestSubFamilyName(),
        "fullName": name.getDebugName(4),
        "weight": font["OS/2"].usWeightClass,
        "glyphs": entries,
        "kerning": gpos_kerning(font, cmap, order),
        "legacyKerning": legacy_kerning(font, cmap, order),
    }


def gpos_kerning(font, cmap, order):
    """Pair kerning computed from GPOS with fontTools objects: PairPos lookups of the 'kern' feature."""
    if "GPOS" not in font:
        return []
    gpos = font["GPOS"].table
    indices = set()
    for record in gpos.FeatureList.FeatureRecord:
        if record.FeatureTag == "kern":
            indices.update(record.Feature.LookupListIndex)
    lookups = [gpos.LookupList.Lookup[i] for i in sorted(indices)]
    result = []
    for pair in KERN_PAIRS:
        left, right = cmap.get(ord(pair[0])), cmap.get(ord(pair[1]))
        total = 0
        for lookup in lookups:
            for sub in lookup.SubTable:
                if lookup.LookupType == 9:
                    sub = sub.ExtSubTable
                if sub.LookupType != 2 if hasattr(sub, "LookupType") else False:
                    continue
                value = pair_value(sub, left, right)
                if value is not None:
                    total += value
                    break
        result.append({"pair": pair, "left": order.index(left), "right": order.index(right), "value": total})
    return result


def pair_value(sub, left, right):
    if left not in sub.Coverage.glyphs:
        return None
    if sub.Format == 1:
        pair_set = sub.PairSet[sub.Coverage.glyphs.index(left)]
        for record in pair_set.PairValueRecord:
            if record.SecondGlyph == right:
                return getattr(record.Value1, "XAdvance", 0) if record.Value1 else 0
        return None
    class1 = sub.ClassDef1.classDefs.get(left, 0)
    class2 = sub.ClassDef2.classDefs.get(right, 0)
    record = sub.Class1Record[class1].Class2Record[class2]
    return getattr(record.Value1, "XAdvance", 0) if record.Value1 else 0


def legacy_kerning(font, cmap, order):
    if "kern" not in font:
        return []
    table = font["kern"].kernTables[0].kernTable
    result = []
    for pair in KERN_PAIRS:
        left, right = cmap.get(ord(pair[0])), cmap.get(ord(pair[1]))
        result.append({"pair": pair, "left": order.index(left), "right": order.index(right), "value": table.get((left, right), 0)})
    return result


def freetype_renders(path, face_index=0):
    """Unhinted FreeType coverage for the sample glyphs: the independent rasteriser the tests compare against."""
    face = freetype.Face(path, index=face_index)
    renders = []
    for size in RENDER_SIZES:
        face.set_pixel_sizes(0, size)
        for ch in SAMPLE_TEXT:
            gid = face.get_char_index(ord(ch))
            face.load_glyph(gid, freetype.FT_LOAD_NO_HINTING | freetype.FT_LOAD_NO_BITMAP)
            face.glyph.render(freetype.FT_RENDER_MODE_NORMAL)
            bitmap = face.glyph.bitmap
            rows = bitmap.rows
            width = bitmap.width
            pitch = bitmap.pitch
            raw = bytes(bitmap.buffer)
            data = b"".join(raw[r * pitch:r * pitch + width] for r in range(rows))
            renders.append({
                "size": size,
                "codepoint": ord(ch),
                "glyph": gid,
                "left": face.glyph.bitmap_left,
                "top": face.glyph.bitmap_top,
                "width": width,
                "height": rows,
                "coverage": base64.b64encode(data).decode("ascii"),
            })
    return renders


def main():
    source_dir = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.dirname(os.path.abspath(__file__))
    regular = make_subset(os.path.join(source_dir, "DejaVuSans.ttf"), os.path.join(out, "DejaVuSans-Subset.ttf"))
    bold = make_subset(os.path.join(source_dir, "DejaVuSans-Bold.ttf"), os.path.join(out, "DejaVuSans-Bold-Subset.ttf"), BOLD_ONLY)
    otf = make_cff(regular, os.path.join(out, "DejaVuSans-Subset.otf"), cid=False)
    cid = make_cff(regular, os.path.join(out, "DejaVuSans-Subset-CID.otf"), cid=True)

    collection = TTCollection()
    collection.fonts = [TTFont(regular, recalcTimestamp=False), TTFont(bold, recalcTimestamp=False)]
    ttc = os.path.join(out, "DejaVuSans-Subset.ttc")
    collection.save(ttc)

    woff = os.path.join(out, "DejaVuSans-Subset.woff")
    f = TTFont(regular, recalcTimestamp=False)
    f.flavor = "woff"
    f.save(woff)
    woff2_path = os.path.join(out, "DejaVuSans-Subset.woff2")
    woff2.compress(regular, woff2_path, transform_tables={"glyf", "loca", "hmtx"})

    files = {
        "DejaVuSans-Subset.ttf": (regular, 0),
        "DejaVuSans-Bold-Subset.ttf": (bold, 0),
        "DejaVuSans-Subset.otf": (otf, 0),
        "DejaVuSans-Subset-CID.otf": (cid, 0),
    }
    reference = {"tools": {"fontTools": __import__("fontTools").version, "freetype": ".".join(map(str, freetype.version()))}, "fonts": {}}
    for name, (path, index) in files.items():
        entry = reference_for(path, index)
        entry["renders"] = freetype_renders(path, index)
        reference["fonts"][name] = entry
    with open(os.path.join(out, "reference.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(reference, f, indent=1, sort_keys=True)
        f.write("\n")

    for name in sorted(os.listdir(out)):
        if name.endswith((".ttf", ".otf", ".ttc", ".woff", ".woff2", ".json")):
            print(sha256(os.path.join(out, name)), name)


if __name__ == "__main__":
    main()
