"""Fetch a pinned five-language, family-disjoint MDPBench evaluation subset.

Images/labels stay in the requested ignored artifacts directory. This samples a public
benchmark, not a private test set, and does not establish absence of OCR model training overlap.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import re
import urllib.request

REPOSITORY = "Delores-Lin/MDPBench"
REVISION = "252df132fe1b418dbd5830515ef4b5a06fa7164f"
BASE = f"https://huggingface.co/datasets/{REPOSITORY}/resolve/{REVISION}/"
LANGUAGES = {"en": ("Latin", "en", "English"), "ar": ("Arabic", "ar", "Arabic"),
             "hi": ("Devanagari", "hi", "Hindi"), "zh": ("Han", "ch_sim", "ChineseSimplified"),
             "jp": ("Japanese", "ja", "Japanese")}


def download(url, path):
    if not path.exists():
        temporary = path.with_suffix(path.suffix + ".partial")
        urllib.request.urlretrieve(url, temporary)
        temporary.replace(path)
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=True)
    (root / "images").mkdir(exist_ok=True)
    (root / "labels").mkdir(exist_ok=True)
    annotation_hash = download(BASE + "MDPBench_public.json", root / "MDPBench_public.json")
    download(BASE + "README.md", root / "UPSTREAM_README.md")
    annotations = json.loads((root / "MDPBench_public.json").read_text(encoding="utf-8"))
    families = {}
    for row in annotations:
        name = row["page_info"]["image_path"]
        match = re.match(r"^([a-z-]+)_([^_]+)_(\d+)", name)
        if match is None or match[1] not in LANGUAGES:
            continue
        families.setdefault(match[0], []).append(row)
    selected = []
    for language in LANGUAGES:
        candidates = sorted((key for key in families if key.startswith(language + "_")),
                            key=lambda key: hashlib.sha256(("easyimage-v1:" + key).encode()).hexdigest())
        # Two tuning and two evaluation families per language; clean/photo variants never cross splits.
        for index, key in enumerate(candidates[:4]):
            rows = sorted(families[key], key=lambda row: row["page_info"]["image_path"])
            clean = [row for row in rows if Path(row["page_info"]["image_path"]).stem == key]
            photo = [row for row in rows if Path(row["page_info"]["image_path"]).stem != key]
            if not clean or not photo:
                raise ValueError("Expected clean and photographic variants: " + key)
            for category, row in (("digital", clean[0]), ("photograph", photo[0])):
                selected.append((language, key, "tuning" if index < 2 else "held-out", category, row))

    def prepare(entry):
        language, family, split, category, row = entry
        name = row["page_info"]["image_path"]
        fingerprint = download(BASE + "MDPBench_img_public/" + name, root / "images" / name)
        # Keep annotated reading order; intentionally do not tune normalization against predictions.
        blocks = sorted((block for block in row["layout_dets"] if isinstance(block.get("text"), str)),
                        key=lambda block: (block.get("order") or 0, block.get("anno_id") or 0))
        text = "\n".join(block["text"] for block in blocks)
        identifier = Path(name).stem
        (root / "labels" / (identifier + ".txt")).write_text(text, encoding="utf-8")
        script, easy, paddle = LANGUAGES[language]
        return {"id": identifier, "path": "images/" + name, "groundTruthFile": "labels/" + identifier + ".txt",
                "category": category, "split": split, "family": family, "script": script, "language": language,
                "sha256": fingerprint, "easyOcrLanguages": [easy], "paddleOcrLanguages": [paddle],
                "annotationGranularity": "text block (not OCR line geometry)",
                "blocks": [{"text": block["text"], "poly": block["poly"]} for block in blocks]}
    with ThreadPoolExecutor(max_workers=4) as pool:
        samples = list(pool.map(prepare, selected))
    manifest = {"corpusId": "mdpbench-five-language-v1", "license": "Apache-2.0 (upstream dataset declaration)",
                "source": "https://huggingface.co/datasets/" + REPOSITORY, "revision": REVISION,
                "annotationSha256": annotation_hash, "selection": "SHA256 easyimage-v1 family ordering; four families per language; first clean/photo pair",
                "limitations": ["Public benchmark subset, not official full-benchmark score.",
                                "Model training overlap unknown; held-out means held out from local threshold selection.",
                                "Text blocks include layout reading-order conventions; block boxes are not line detection truth."],
                "easyOcrCache": "%LOCALAPPDATA%/EasyOcrSharp/models", "paddleOcrCache": "%LOCALAPPDATA%/PaddleOcrNet/models",
                "allowModelDownload": True, "images": samples}
    (root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"samples": len(samples), "languages": list(LANGUAGES), "manifest": str(root / "manifest.json")}))


if __name__ == "__main__":
    main()
