"""Create prediction-independent annotated text-block crops for recognition-with-detection evaluation."""
import argparse
import hashlib
import json
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageOps, ImageChops, ImageFilter, __version__ as pillow_version


def fingerprint(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def polygon(block):
    values = block["poly"]
    if len(values) < 6 or len(values) % 2 or not all(isinstance(v, (float, int)) and math.isfinite(v) for v in values):
        raise ValueError("Invalid annotation polygon")
    return list(zip(values[::2], values[1::2]))


def area(block):
    points = polygon(block)
    return abs(sum(x * yy - xx * y for (x, y), (xx, yy) in zip(points, points[1:] + points[:1]))) / 2


def select_block(blocks):
    for categories in (("text_block",), ("title", "header", "footer", "figure_caption", "table_caption", "page_footnote")):
        candidates = [(index, block) for index, block in enumerate(blocks)
                      if block.get("category_type") in categories and isinstance(block.get("text"), str) and block["text"].strip()]
        if candidates:
            return min(candidates, key=lambda pair: (-area(pair[1]), pair[0]))
    raise ValueError("Page has no eligible labeled text region; no silent replacement is allowed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("annotations", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    if fingerprint(args.annotations).lower() != manifest["annotationSha256"].lower():
        raise ValueError("Annotation file does not match pinned manifest fingerprint")
    annotations = {row["page_info"]["image_path"]: row for row in json.loads(args.annotations.read_text(encoding="utf-8"))}
    root = args.manifest.resolve().parent
    destination = args.output.resolve()
    (destination / "images").mkdir(parents=True, exist_ok=True)
    (destination / "labels").mkdir(exist_ok=True)
    Image.MAX_IMAGE_PIXELS = 150_000_000
    rows = []
    excluded = [{"id": row["id"], "reason": "Photograph annotations reuse clean-page coordinates; no per-photo registration is supplied"}
                for row in manifest["images"] if row["category"] != "digital"]
    for original in (row for row in manifest["images"] if row["category"] == "digital"):
        source = root / original["path"]
        if fingerprint(source) != original["sha256"].lower():
            raise ValueError("Image checksum differs: " + original["id"])
        index, block = select_block(annotations[source.name]["layout_dets"])
        points = polygon(block)
        with Image.open(source) as image:
            if image.width * image.height > 150_000_000:
                raise ValueError("ROI preparation source exceeds explicit 150-million-pixel limit")
            left = max(0, math.floor(min(p[0] for p in points)))
            top = max(0, math.floor(min(p[1] for p in points)))
            right = min(image.width, math.ceil(max(p[0] for p in points)))
            bottom = min(image.height, math.ceil(max(p[1] for p in points)))
            if left >= right or top >= bottom:
                raise ValueError("Selected annotation lies outside image")
            cropped = image.crop((left, top, right, bottom)).convert("RGB")
        with cropped:
            with Image.new("L", cropped.size, 0) as mask:
                ImageDraw.Draw(mask).polygon([(x - left, y - top) for x, y in points], fill=255)
                with Image.new("RGB", cropped.size, "white") as result:
                    result.paste(cropped, mask=mask)
                    output_image = destination / "images" / (original["id"] + ".png")
                    result.save(output_image)
                    # Fixed before OCR: preserve all text, add a small known skew and smooth illumination/blur.
                    with ImageOps.expand(result, border=12, fill="white") as padded:
                        with padded.rotate(-2, resample=Image.Resampling.BILINEAR, expand=True, fillcolor="white") as rotated:
                            with Image.linear_gradient("L").rotate(90).resize(rotated.size).point(lambda v: round(166 + v * 89 / 255)).convert("RGB") as illumination:
                                with ImageChops.multiply(rotated, illumination) as shaded:
                                    with shaded.filter(ImageFilter.GaussianBlur(.45)) as controlled:
                                        controlled_path = destination / "images" / (original["id"] + "__controlled.png")
                                        controlled.save(controlled_path)
        output_label = destination / "labels" / (original["id"] + ".txt")
        output_label.write_text(block["text"], encoding="utf-8")
        row = dict(original, path="images/" + output_image.name, groundTruthFile="labels/" + output_label.name,
                   sha256=fingerprint(output_image), sourceSha256=original["sha256"],
                   sourceImage=source.name, annotationIndex=index, annotationId=block.get("anno_id"),
                   annotationCategory=block["category_type"], sourcePolygon=block["poly"],
                   sourceCropBox=[left, top, right, bottom], labelSha256=fingerprint(output_label),
                   annotationGranularity="one annotated text block; eligible text-category fallback only when no text_block exists")
        row.pop("blocks", None)
        rows.append(row)
        rows.append(dict(row, id=original["id"] + "__controlled", path="images/" + controlled_path.name,
                         category="controlled", sha256=fingerprint(controlled_path), sourceBlockId=original["id"],
                         synthetic=True, controlledTransform="12px white border; clockwise2deg bilinear expanded white; left/right166..255 multiplicative illumination; Gaussian blur radius0.45px"))
        print(f"{original['id']}: {block['category_type']} {right-left}x{bottom-top}, {len(block['text'])} label characters", flush=True)
    manifest.update(corpusId=manifest["corpusId"] + "-digital-largest-text-block-v1", images=rows,
                    excludedSourceImages=excluded,
                    sourceManifestSha256=fingerprint(args.manifest), preparationPillowVersion=pillow_version,
                    selection="Largest polygon area nonempty text_block; fallback largest eligible title/header/footer/figure_caption/table_caption/page_footnote only when none exists; original annotation index breaks ties; no predictions used",
                    pixelPreparation="Bounding box floor/ceil clipped to source; original polygon retained, outside white; RGB PNG; no resampling, EXIF transform or padding",
                    limitations=manifest["limitations"] + [
                        "Measures OCR recognition with detection on an annotation-selected crop, not full-page end-to-end OCR.",
                        "Only digital pages have usable annotation geometry. Photograph variants repeat clean-page coordinates without registration and are explicitly excluded.",
                        "One largest text block per page; text-category fallback explicitly recorded. This does not represent all page text or difficult tiny text.",
                        "Controlled counterparts are synthetic skew/illumination/blur challenges, not real photographs. They reuse the exact same text label.",
                        "Families remain disjoint across local tuning/held-out splits. Clean and controlled crops from one family are dependent observations.",
                        "Annotation polygon/text errors and partial character clipping can remain; results depend on these public annotations."])
    (destination / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
